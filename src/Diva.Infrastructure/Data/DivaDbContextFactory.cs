using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Diva.Infrastructure.Data;

/// <summary>
/// Design-time factory used by dotnet-ef migrations.
/// Defaults to SQLite (migrations live in this assembly). Pass
/// <c>-- --provider SqlServer</c> to target the SQL Server migrations assembly
/// (<c>Diva.Infrastructure.SqlServer</c>). Example:
/// <code>
/// dotnet ef migrations add InitialCreate \
///   --project src/Diva.Infrastructure.SqlServer \
///   --startup-project src/Diva.Host \
///   --context DivaDbContext -- --provider SqlServer
/// </code>
/// </summary>
public sealed class DivaDbContextFactory : IDesignTimeDbContextFactory<DivaDbContext>
{
    public const string SqlServerMigrationsAssembly = "Diva.Infrastructure.SqlServer";

    public DivaDbContext CreateDbContext(string[] args)
    {
        var provider = ParseProvider(args);
        var builder = new DbContextOptionsBuilder<DivaDbContext>();

        if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            // Connection string is irrelevant for `migrations add` (no DB is touched);
            // a placeholder keeps the design-time context valid.
            builder.UseSqlServer(
                "Server=localhost;Database=Diva;Trusted_Connection=True;TrustServerCertificate=true",
                o => o.MigrationsAssembly(SqlServerMigrationsAssembly));
        }
        else
        {
            builder.UseSqlite("Data Source=diva.db");
        }

        return new DivaDbContext(builder.Options, currentTenantId: 0);
    }

    private static string ParseProvider(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--provider", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return "SQLite";
    }
}
