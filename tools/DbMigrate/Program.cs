using System.Collections;
using System.Data;
using Diva.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Diva.Tools.DbMigrate;

/// <summary>
/// One-way data migration: copies all business data from a SQLite DivaDbContext database
/// into an already-schema-provisioned SQL Server database.
///
/// Design:
///  - Source is read with the SYSTEM tenant (TenantId = 0) and IgnoreQueryFilters() so
///    every tenant's rows are copied (global query filters are bypassed).
///  - Target schema is created by the SQL Server InitialCreate migration (run with
///    --apply-migrations, or point at an already-migrated DB).
///  - Each table is copied with SqlBulkCopy(KeepIdentity); identity tables are wrapped in
///    SET IDENTITY_INSERT ON/OFF so primary keys and all FK references stay intact.
///  - All copies run inside a single transaction with FK constraints disabled, then
///    re-enabled WITH CHECK. Per-table row counts are validated; any mismatch rolls back.
///  - Secret columns (McpCredentials ciphertext, PlatformApiKeys.KeyHash, LocalUsers hashes)
///    are copied verbatim — keep CREDENTIALS_MASTER_KEY / LOCAL_AUTH_SIGNING_KEY unchanged.
///  - The session-trace database is intentionally NOT migrated (fresh on first run).
///  - EF internal tables (__EFMigrationsHistory / __EFMigrationsLock) are never touched.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var opts = MigrationArgs.Parse(args);
            Console.WriteLine("Diva SQLite → SQL Server data migration");
            Console.WriteLine($"  Source (SQLite)     : {opts.SourceConnectionString}");
            Console.WriteLine($"  Target (SQL Server) : {Redact(opts.TargetConnectionString)}");
            Console.WriteLine($"  Apply migrations    : {opts.ApplyMigrations}");
            Console.WriteLine($"  Force (ignore data) : {opts.Force}");
            Console.WriteLine();

            await using var source = BuildSqliteContext(opts.SourceConnectionString);

            if (opts.ApplyMigrations)
            {
                Console.WriteLine("Applying SQL Server migrations to target ...");
                await using var migrateCtx = BuildSqlServerContext(opts.TargetConnectionString);
                await migrateCtx.Database.MigrateAsync();
                Console.WriteLine("  ✓ schema up to date");
                Console.WriteLine();
            }

            // Ordered, non-owned entity types → one destination table each.
            var entityTypes = source.Model.GetEntityTypes()
                .Where(t => !t.IsOwned())
                .Where(t => t.GetTableName() is { Length: > 0 })
                .GroupBy(t => t.GetTableName())
                .Select(g => g.First())
                .ToList();

            await using var target = new SqlConnection(opts.TargetConnectionString);
            await target.OpenAsync();

            if (!opts.Force)
                await EnsureTargetEmptyAsync(target, entityTypes);

            await using var tx = (SqlTransaction)await target.BeginTransactionAsync();
            var identityTables = new List<string>();
            try
            {
                await ToggleForeignKeysAsync(target, tx, enable: false);

                var grandTotal = 0L;
                foreach (var entityType in entityTypes)
                {
                    var (copied, isIdentity) = await CopyTableAsync(source, target, tx, entityType);
                    grandTotal += copied;
                    if (isIdentity && copied > 0)
                        identityTables.Add(entityType.GetTableName()!);
                }

                await ToggleForeignKeysAsync(target, tx, enable: true);
                await tx.CommitAsync();
                Console.WriteLine();
                Console.WriteLine($"✓ Committed {grandTotal} row(s) across {entityTypes.Count} table(s).");
            }
            catch
            {
                await tx.RollbackAsync();
                Console.Error.WriteLine();
                Console.Error.WriteLine("✗ Migration rolled back — no changes were committed.");
                throw;
            }

            // Reseed identity counters (post-commit; reads MAX(id)).
            foreach (var table in identityTables)
            {
                await using var reseed = target.CreateCommand();
                reseed.CommandText = $"DBCC CHECKIDENT ('[{table}]', RESEED)";
                await reseed.ExecuteNonQueryAsync();
            }
            if (identityTables.Count > 0)
                Console.WriteLine($"✓ Reseeded identity on {identityTables.Count} table(s).");

            Console.WriteLine();
            Console.WriteLine("Migration complete. Remember:");
            Console.WriteLine("  • Keep CREDENTIALS_MASTER_KEY identical (encrypted MCP credentials were copied verbatim).");
            Console.WriteLine("  • Keep LOCAL_AUTH_SIGNING_KEY / SCHEDULER_FEEDBACK_TOKEN_SECRET unchanged.");
            Console.WriteLine("  • Set Database__Provider=SqlServer + the connection string, then start the host.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static DivaDbContext BuildSqliteContext(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<DivaDbContext>().UseSqlite(connectionString);
        return new DivaDbContext(builder.Options, currentTenantId: 0);
    }

    private static DivaDbContext BuildSqlServerContext(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<DivaDbContext>()
            .UseSqlServer(connectionString, o => o.MigrationsAssembly(DivaDbContextFactory.SqlServerMigrationsAssembly));
        return new DivaDbContext(builder.Options, currentTenantId: 0);
    }

    private static async Task EnsureTargetEmptyAsync(SqlConnection target, IReadOnlyList<IEntityType> entityTypes)
    {
        foreach (var entityType in entityTypes)
        {
            var table = entityType.GetTableName()!;
            await using var cmd = target.CreateCommand();
            cmd.CommandText = $"SELECT COUNT_BIG(*) FROM [{table}]";
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
            if (count > 0)
                throw new InvalidOperationException(
                    $"Target table [{table}] already contains {count} row(s). " +
                    "Refusing to migrate into a non-empty database. Use --force to override.");
        }
    }

    private static async Task ToggleForeignKeysAsync(SqlConnection conn, SqlTransaction tx, bool enable)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = enable
            ? "EXEC sp_MSforeachtable 'ALTER TABLE ? WITH CHECK CHECK CONSTRAINT ALL'"
            : "EXEC sp_MSforeachtable 'ALTER TABLE ? NOCHECK CONSTRAINT ALL'";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<(long Copied, bool IsIdentity)> CopyTableAsync(
        DivaDbContext source, SqlConnection target, SqlTransaction tx, IEntityType entityType)
    {
        var table = entityType.GetTableName()!;
        var properties = entityType.GetProperties()
            .Where(p => p.GetColumnName() is { Length: > 0 })
            .ToList();

        var rows = ReadAll(source, entityType.ClrType);
        var isIdentity = IsIdentityTable(entityType);

        var dataTable = BuildDataTable(properties, rows);
        var count = dataTable.Rows.Count;

        if (count == 0)
        {
            Console.WriteLine($"  {table,-40} 0 rows");
            return (0, isIdentity);
        }

        if (isIdentity)
            await ExecAsync(target, tx, $"SET IDENTITY_INSERT [{table}] ON");

        using (var bulk = new SqlBulkCopy(target, SqlBulkCopyOptions.KeepIdentity, tx)
        {
            DestinationTableName = $"[{table}]",
            BulkCopyTimeout = 0,
            BatchSize = 2000,
        })
        {
            foreach (DataColumn col in dataTable.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(dataTable);
        }

        if (isIdentity)
            await ExecAsync(target, tx, $"SET IDENTITY_INSERT [{table}] OFF");

        // Validate row count within the transaction; mismatch triggers rollback.
        await using var verify = target.CreateCommand();
        verify.Transaction = tx;
        verify.CommandText = $"SELECT COUNT_BIG(*) FROM [{table}]";
        var target_count = Convert.ToInt64(await verify.ExecuteScalarAsync() ?? 0L);
        if (target_count != count)
            throw new InvalidOperationException(
                $"Row-count mismatch on [{table}]: source={count}, target={target_count}.");

        Console.WriteLine($"  {table,-40} {count} rows{(isIdentity ? " (identity)" : "")}");
        return (count, isIdentity);
    }

    private static DataTable BuildDataTable(IReadOnlyList<IProperty> properties, IList rows)
    {
        var table = new DataTable();
        foreach (var p in properties)
        {
            var converter = p.GetValueConverter();
            var providerType = converter?.ProviderClrType ?? p.ClrType;
            providerType = Nullable.GetUnderlyingType(providerType) ?? providerType;
            table.Columns.Add(new DataColumn(p.GetColumnName()!, providerType) { AllowDBNull = true });
        }

        foreach (var entity in rows)
        {
            var row = table.NewRow();
            foreach (var p in properties)
            {
                object? modelValue =
                    p.PropertyInfo is not null ? p.PropertyInfo.GetValue(entity)
                    : p.FieldInfo is not null ? p.FieldInfo.GetValue(entity)
                    : null;

                var converter = p.GetValueConverter();
                var providerValue = converter is not null && modelValue is not null
                    ? converter.ConvertToProvider(modelValue)
                    : modelValue;

                row[p.GetColumnName()!] = providerValue ?? DBNull.Value;
            }
            table.Rows.Add(row);
        }

        return table;
    }

    private static bool IsIdentityTable(IEntityType entityType)
    {
        var key = entityType.FindPrimaryKey();
        if (key is null) return false;
        return key.Properties.Any(p =>
            p.ValueGenerated == ValueGenerated.OnAdd &&
            (p.ClrType == typeof(int) || p.ClrType == typeof(long)));
    }

    /// <summary>Reflection wrapper for context.Set&lt;T&gt;().IgnoreQueryFilters().AsNoTracking().ToList().</summary>
    private static IList ReadAll(DivaDbContext ctx, Type clrType)
    {
        var method = typeof(Program).GetMethod(nameof(ReadAllGeneric),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (IList)method.MakeGenericMethod(clrType).Invoke(null, [ctx])!;
    }

    private static List<T> ReadAllGeneric<T>(DivaDbContext ctx) where T : class
        => ctx.Set<T>().IgnoreQueryFilters().AsNoTracking().ToList();

    private static async Task ExecAsync(SqlConnection conn, SqlTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Redact(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(b.Password)) b.Password = "***";
        return b.ConnectionString;
    }
}
