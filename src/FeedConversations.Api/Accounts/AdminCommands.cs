using System.Security.Cryptography;
using System.Text.Json;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using Microsoft.AspNetCore.Identity;

namespace FeedConversations.Api.Accounts;

public static class AdminCommands
{
    public static async Task<bool> RunAsync(WebApplication app, string[] args)
    {
        if (!args.Contains("--create-admin") && !args.Contains("--seed-demo")) return false;
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var options = scope.ServiceProvider.GetRequiredService<InstanceOptions>();
        if (args.Contains("--seed-demo"))
        {
            if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("Demo seed requires Development.");
            var handles = options.Id.EndsWith('a') ? new[] { "alice", "bob" } : new[] { "carol", "david" };
            var credentials = new List<object>();
            foreach (var handle in handles)
            {
                if (await users.FindByNameAsync(handle) is not null) continue;
                var password = $"Demo-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24))}!9a";
                var account = new Account { UserName = handle, DisplayName = handle, IsAdmin = credentials.Count == 0 };
                var result = await users.CreateAsync(account, password);
                if (!result.Succeeded) throw new InvalidOperationException("Demo account creation failed.");
                credentials.Add(new { handle, password });
            }
            if (credentials.Count > 0)
                await File.WriteAllTextAsync(Path.Combine(options.DataDirectory, "demo-credentials.json"),
                    JsonSerializer.Serialize(credentials));
        }
        else
        {
            Console.Write("Admin handle: ");
            var handle = Console.ReadLine() ?? "";
            Console.Write("Password (not echoed): ");
            var password = "";
            ConsoleKeyInfo key;
            while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
                if (key.Key == ConsoleKey.Backspace) password = password.Length > 0 ? password[..^1] : "";
                else if (!char.IsControl(key.KeyChar)) password += key.KeyChar;
            Console.WriteLine();
            var result = await users.CreateAsync(new Account { UserName = handle, DisplayName = handle, IsAdmin = true }, password);
            if (!result.Succeeded) throw new InvalidOperationException("Admin creation failed: check handle uniqueness and password policy.");
        }
        return true;
    }
}
