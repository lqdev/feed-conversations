using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Accounts;

public sealed record LoginRequest(string Handle, string Password);
public sealed record RedeemRequest(string Code, string Handle, string Password, string? DisplayName = null);
public sealed record PasswordRequest(string Password, string? NewPassword = null);
public sealed record ProfilePatch(string? DisplayName = null, string? Bio = null, bool? DirectoryVisible = null);

public static partial class AccountEndpoints
{
    public static string Owner(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new ApiProblem(401, "authentication-required", "Authentication is required.");
    public static async Task<Account> Current(HttpContext context, AppDbContext db, CancellationToken token)
    {
        var id = Owner(context);
        var account = await db.Users.SingleOrDefaultAsync(a => a.Id == id, token);
        if (account is null || account.Disabled) throw new ApiProblem(401, "account-unavailable", "This session is unavailable.");
        return account;
    }
    public static async Task<Account> Admin(HttpContext context, AppDbContext db, CancellationToken token)
    {
        var account = await Current(context, db, token);
        if (!account.IsAdmin) throw new ApiProblem(403, "admin-required", "Administrator access is required.");
        return account;
    }
    public static object Profile(Account a) => new
    { a.Id, handle = a.UserName, a.DisplayName, a.Bio, a.DirectoryVisible, a.IsAdmin, a.Revision };

    public static void MapAccounts(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapGet("/auth/csrf", (HttpContext ctx, IAntiforgery anti) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = anti.GetAndStoreTokens(ctx).RequestToken });
        });
        api.MapPost("/auth/login", async (LoginRequest request, SignInManager<Account> signin,
            UserManager<Account> users, HttpContext ctx, LoginAttempts attempts) =>
        {
            if (request.Handle.Length > 64 || request.Password.Length > 1024) throw ApiProblem.Invalid("login-input-limit");
            attempts.Check(request.Handle, ctx);
            var account = await users.FindByNameAsync(request.Handle);
            if (account is null || account.Disabled)
                throw new ApiProblem(401, "login-failed", "Login failed.");
            var result = await signin.PasswordSignInAsync(account, request.Password, false, true);
            if (!result.Succeeded) throw new ApiProblem(401, "login-failed", "Login failed.");
            return Results.Ok(Profile(account));
        });
        api.MapPost("/auth/logout", async (SignInManager<Account> signin) =>
        {
            await signin.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();
        api.MapPost("/auth/redeem-invite", async (RedeemRequest request, AppDbContext db,
            UserManager<Account> users, SignInManager<Account> signin, TimeProvider clock, HttpContext ctx, CreateReceipts receipts, CancellationToken ct) =>
        {
            if (!Handle().IsMatch(request.Handle) || request.Code.Length > 200 || request.Password.Length > 1024 || request.DisplayName is { Length: > 200 })
                throw ApiProblem.Invalid("invalid-handle-or-code");
            Syndication.SemanticProfile.ValidateText(request.DisplayName, 800, "display-name-too-long");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var hash = Wire.Hash(request.Code);
            var invite = await db.Invites.SingleOrDefaultAsync(i => i.TokenHash == hash, ct);
            if (invite is null || invite.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
                throw ApiProblem.Invalid("invite-unavailable");
            var input = new { invite.Id, request.Handle, request.DisplayName };
            if (invite.ConsumedBy is { } consumedBy)
            {
                var previous = await receipts.Prepare(consumedBy, "redeem", ctx.Request, input, ct);
                if (previous.ResourceId is null) throw ApiProblem.Invalid("invite-unavailable");
                var existing = await users.FindByIdAsync(consumedBy);
                if (existing is null || existing.Disabled || !await users.CheckPasswordAsync(existing, request.Password))
                    throw new ApiProblem(409, "idempotency-mismatch", "The original account credentials do not match.");
                await tx.CommitAsync(ct);
                await signin.SignInAsync(existing, false);
                return Results.Ok(Profile(existing));
            }
            var account = new Account { UserName = request.Handle, DisplayName = request.DisplayName ?? request.Handle };
            var intent = await receipts.Prepare(account.Id, "redeem", ctx.Request, input, ct);
            var created = await users.CreateAsync(account, request.Password);
            if (!created.Succeeded) throw ApiProblem.Invalid("account-validation");
            invite.ConsumedBy = account.Id;
            receipts.Record(account.Id, "redeem", intent, Guid.Parse(account.Id));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await signin.SignInAsync(account, false);
            return Results.Ok(Profile(account));
        }).RequireRateLimiting("invite");
        api.MapGet("/me", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var account = await Current(ctx, db, ct);
            ctx.Response.Headers.ETag = Wire.ETag("account", account.Id, account.Revision);
            return Results.Ok(Profile(account));
        }).RequireAuthorization();
        api.MapPatch("/me", async (ProfilePatch patch, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var account = await Current(ctx, db, ct);
            Wire.CheckMatch(ctx.Request, "account", account.Id, account.Revision);
            if (patch.DisplayName is { Length: > 200 } || patch.Bio is { Length: > 4000 }) throw ApiProblem.Invalid();
            Syndication.SemanticProfile.ValidateText(patch.DisplayName, 800, "display-name-too-long");
            Syndication.SemanticProfile.ValidateText(patch.Bio, 16000, "bio-too-long");
            if (patch.DisplayName is not null) account.DisplayName = patch.DisplayName;
            if (patch.Bio is not null) account.Bio = patch.Bio;
            if (patch.DirectoryVisible is not null) account.DirectoryVisible = patch.DirectoryVisible.Value;
            account.Revision++;
            db.Changes.Add(new LocalChange { ResourceType = "profile", ResourceId = account.Id,
                Revision = account.Revision, Operation = "invalidate", OwnerId = account.Id, At = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            ctx.Response.Headers.ETag = Wire.ETag("account", account.Id, account.Revision);
            return Results.Ok(Profile(account));
        }).RequireAuthorization();
        api.MapPost("/auth/confirm-password", async (PasswordRequest request, HttpContext ctx,
            AppDbContext db, UserManager<Account> users, SignInManager<Account> signin, TimeProvider clock, CancellationToken ct) =>
        {
            var account = await Current(ctx, db, ct);
            if (request.Password.Length > 1024) throw ApiProblem.Invalid("password-input-limit");
            if (!await users.CheckPasswordAsync(account, request.Password)) throw new ApiProblem(403, "password-confirmation-failed", "Password confirmation failed.");
            await signin.SignInWithClaimsAsync(account, false,
                [new Claim("confirmed-at", clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))]);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("login");
        api.MapPost("/auth/change-password", async (PasswordRequest request, HttpContext ctx, AppDbContext db,
            UserManager<Account> users, SignInManager<Account> signin, CancellationToken ct) =>
        {
            var account = await Current(ctx, db, ct);
            if (request.Password.Length > 1024 || request.NewPassword is null || request.NewPassword.Length > 1024
                || !(await users.ChangePasswordAsync(account, request.Password, request.NewPassword)).Succeeded)
                throw ApiProblem.Invalid("password-change-failed");
            await signin.RefreshSignInAsync(account);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("login");
        api.MapPost("/admin/invites", async (HttpContext ctx, AppDbContext db, TimeProvider clock, CreateReceipts receipts, CancellationToken ct) =>
        {
            var account = await Admin(ctx, db, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var intent = await receipts.Prepare(account.Id, "invite", ctx.Request, null, ct);
            if (intent.ResourceId is { } previous)
                return Results.Ok(new { id = previous, code = (string?)null, replayed = true });
            if (await db.Invites.CountAsync(i => i.ConsumedBy == null && i.ExpiresAt > DateTime.UtcNow, ct) >= 100)
                throw new ApiProblem(409, "invite-quota", "The invite quota is exhausted.");
            var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var invite = new Invite { TokenHash = Wire.Hash(code), CreatorId = account.Id, ExpiresAt = clock.GetUtcNow().UtcDateTime.AddDays(2) };
            db.Invites.Add(invite);
            receipts.Record(account.Id, "invite", intent, invite.Id);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { id = invite.Id, code, replayed = false });
        }).RequireAuthorization();
        foreach (var disable in new[] { true, false })
            api.MapPost($"/admin/accounts/{{id}}/{(disable ? "disable" : "enable")}",
                async (string id, HttpContext ctx, AppDbContext db, UserManager<Account> users, CancellationToken ct) =>
                {
                    await Admin(ctx, db, ct);
                    var account = await db.Users.SingleOrDefaultAsync(a => a.Id == id, ct)
                        ?? throw new ApiProblem(404, "account-missing", "Account not found.");
                    account.Disabled = disable;
                    account.Revision++;
                    await users.UpdateSecurityStampAsync(account);
                    await db.SaveChangesAsync(ct);
                    return Results.NoContent();
                }).RequireAuthorization();
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    internal static partial Regex Handle();
}
