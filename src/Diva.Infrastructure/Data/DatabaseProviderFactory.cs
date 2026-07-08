using Diva.Core.Configuration;
using Diva.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diva.Infrastructure.Data;

public interface IDatabaseProviderFactory
{
    DivaDbContext CreateDbContext(TenantContext? tenant = null);
    Task ApplyMigrationsAsync();
}

public sealed class DatabaseProviderFactory : IDatabaseProviderFactory
{
    private readonly DatabaseOptions _options;

    public DatabaseProviderFactory(IOptions<DatabaseOptions> options)
        => _options = options.Value;

    public DivaDbContext CreateDbContext(TenantContext? tenant = null)
    {
        var tenantId = tenant?.TenantId ?? 0;
        var optionsBuilder = new DbContextOptionsBuilder<DivaDbContext>();
        Configure(optionsBuilder);
        return new DivaDbContext(optionsBuilder.Options, tenantId);
    }

    public async Task ApplyMigrationsAsync()
    {
        using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    private void Configure(DbContextOptionsBuilder builder)
    {
        if (_options.Provider == "SqlServer")
        {
            builder.UseSqlServer(
                _options.SqlServer.ConnectionString,
                opts =>
                {
                    opts.EnableRetryOnFailure();
                    opts.MigrationsAssembly(DivaDbContextFactory.SqlServerMigrationsAssembly);
                });
        }
        else
        {
            builder.UseSqlite(_options.SQLite.ConnectionString);
        }
    }
}
