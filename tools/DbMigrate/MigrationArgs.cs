namespace Diva.Tools.DbMigrate;

/// <summary>Parsed command-line / environment configuration for the migration tool.</summary>
internal sealed record MigrationArgs(
    string SourceConnectionString,
    string TargetConnectionString,
    bool ApplyMigrations,
    bool Force)
{
    public static MigrationArgs Parse(string[] args)
    {
        string? source = null;
        string? target = null;
        var applyMigrations = false;
        var force = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--source" when i + 1 < args.Length:
                    source = args[++i];
                    break;
                case "--target" when i + 1 < args.Length:
                    target = args[++i];
                    break;
                case "--apply-migrations":
                    applyMigrations = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "-h" or "--help":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
            }
        }

        source ??= Environment.GetEnvironmentVariable("Database__SQLite__ConnectionString")
                   ?? "Data Source=diva.db";
        target ??= Environment.GetEnvironmentVariable("Database__SqlServer__ConnectionString");

        if (string.IsNullOrWhiteSpace(target))
        {
            PrintUsage();
            throw new ArgumentException(
                "Target SQL Server connection string is required " +
                "(--target \"...\" or Database__SqlServer__ConnectionString).");
        }

        return new MigrationArgs(source, target, applyMigrations, force);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: DbMigrate --target "<sql-server-connection>" [options]

            Options:
              --source "<conn>"     SQLite source (default: Data Source=diva.db,
                                    or env Database__SQLite__ConnectionString)
              --target "<conn>"     SQL Server target (or env Database__SqlServer__ConnectionString)
              --apply-migrations    Run the SQL Server InitialCreate migration on the target first
              --force               Migrate even if target tables already contain rows
              -h, --help            Show this help

            The session-trace database is NOT migrated (it is recreated fresh on host startup).
            """);
    }
}
