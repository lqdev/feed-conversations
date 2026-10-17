using System.Security.Cryptography;
using System.Text.Json;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;

namespace FeedConversations.Api.Accounts;

public static class AdminCommands
{
    private sealed record DemoCredential(string Handle, string Password);
    public static async Task<bool> RunAsync(WebApplication app, string[] args)
    {
        if (!args.Contains("--create-admin") && !args.Contains("--seed-demo") && !args.Contains("--reset-password")) return false;
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var options = scope.ServiceProvider.GetRequiredService<InstanceOptions>();
        if (args.Contains("--seed-demo"))
        {
            if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("Demo seed requires Development.");
            var handles = options.Id.EndsWith('a') ? new[] { "alice", "bob" } : new[] { "carol", "david" };
            var credentialFile = Path.Combine(options.DataDirectory, "demo-credentials.json");
            var credentials = File.Exists(credentialFile)
                ? JsonSerializer.Deserialize<List<DemoCredential>>(await File.ReadAllTextAsync(credentialFile), Wire.Json)
                    ?? throw new InvalidOperationException("The private demo credential file is invalid.")
                : [];
            var changed = false;
            foreach (var handle in handles)
            {
                if (await users.FindByNameAsync(handle) is not null) continue;
                var password = $"Demo-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24))}!9a";
                var account = new Account { UserName = handle, DisplayName = handle, IsAdmin = handle is "alice" or "carol" };
                var result = await users.CreateAsync(account, password);
                if (!result.Succeeded) throw new InvalidOperationException("Demo account creation failed.");
                credentials.RemoveAll(c => c.Handle == handle);
                credentials.Add(new DemoCredential(handle, password));
                changed = true;
            }
            if (changed) await File.WriteAllTextAsync(credentialFile, JsonSerializer.Serialize(credentials, Wire.Json));
        }
        else
        {
            var reset = args.Contains("--reset-password");
            Console.Write(reset ? "Account handle to recover: " : "Admin handle: ");
            var handle = Console.ReadLine() ?? "";
            if (!AccountEndpoints.Handle().IsMatch(handle)) throw new InvalidOperationException("The handle must match the immutable public account-handle format.");
            Console.Write("Password (not echoed): ");
            var password = "";
            ConsoleKeyInfo key;
            while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
                if (key.Key == ConsoleKey.Backspace) password = password.Length > 0 ? password[..^1] : "";
                else if (!char.IsControl(key.KeyChar))
                {
                    if (password.Length >= 1024) throw new InvalidOperationException("Password exceeds its input limit.");
                    password += key.KeyChar;
                }
            Console.WriteLine();
            IdentityResult result;
            if (reset)
            {
                var account = await users.FindByNameAsync(handle) ?? throw new InvalidOperationException("The account does not exist.");
                result = await users.ResetPasswordAsync(account, await users.GeneratePasswordResetTokenAsync(account), password);
            }
            else result = await users.CreateAsync(new Account { UserName = handle, DisplayName = handle, IsAdmin = true }, password);
            if (!result.Succeeded) throw new InvalidOperationException("Local account command failed: check handle uniqueness and password policy.");
            Console.WriteLine(reset ? "Password reset; prior sessions are revoked. Disabled state was not changed." : "Administrator created.");
        }
        return true;
    }
}
