using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Diva.Infrastructure.Data;

/// <summary>
/// Design-time factory for SessionTraceDbContext.
/// Required by "dotnet ef migrations add" when using a separate context in the same assembly.
/// Usage:
///   dotnet ef migrations add InitialTraceSchema \
///     --project src/Diva.Infrastructure \
///     --startup-project src/Diva.Host \
///     --context SessionTraceDbContext \
///     -- --provider SQLite
/// </summary>
public sealed class SessionTraceDbContextFactory : IDesignTimeDbContextFactory<SessionTraceDbContext>
{
    public SessionTraceDbContext CreateDbContext(string[] args)
    {
        var provider = ParseProvider(args);
        var optionsBuilder = new DbContextOptionsBuilder<SessionTraceDbContext>();

        if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
            optionsBuilder.UseSqlServer("Server=localhost;Database=DivaTrace;Trusted_Connection=True;TrustServerCertificate=true");
        else
            optionsBuilder.UseSqlite("Data Source=sessions-trace.db");

        return new SessionTraceDbContext(optionsBuilder.Options);
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
