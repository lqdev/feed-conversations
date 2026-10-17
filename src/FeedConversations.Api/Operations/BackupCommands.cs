using FeedConversations.Api.Configuration;
using Microsoft.Data.Sqlite;

namespace FeedConversations.Api.Operations;

public static class BackupCommands
{
    public static bool Run(InstanceOptions options, string[] args)
    {
        var index = Array.IndexOf(args, "--backup");
        if (index < 0) return false;
        if (index + 1 >= args.Length) throw new InvalidOperationException("Supply a new private backup destination.");
        var destination = Path.GetFullPath(args[index + 1]);
        if (File.Exists(destination)) throw new InvalidOperationException("Backup destination already exists; refusing to overwrite it.");
        using var source = new SqliteConnection($"Data Source={Path.Combine(options.DataDirectory, "app.db")};Mode=ReadOnly");
        using var backup = new SqliteConnection($"Data Source={destination};Mode=ReadWriteCreate");
        source.Open();
        backup.Open();
        source.BackupDatabase(backup);
        Console.WriteLine("SQLite backup completed. Protect instance configuration and Data Protection keys separately.");
        return true;
    }
}
