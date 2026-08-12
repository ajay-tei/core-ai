using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Diva.Agents.Tests;

public class CredentialResolverTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _dbOptions;
    private readonly ICredentialEncryptor _encryptor;
    private readonly CredentialResolver _resolver;
    private readonly IMemoryCache _cache;

    private const int TenantId = 42;

    public CredentialResolverTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<DivaDbContext>()
            .UseSqlite(_connection)
            .Options;

        var db = new DivaDbContext(_dbOptions);
        db.Database.EnsureCreated();

        var factory = new TestDatabaseProviderFactory(_dbOptions);

        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        _encryptor = new AesCredentialEncryptor(Options.Create(new CredentialOptions { MasterKey = key }), NullLogger<AesCredentialEncryptor>.Instance);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _resolver = new CredentialResolver(factory, _encryptor, _cache, NullLogger<CredentialResolver>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _connection.Dispose();
    }

    private DivaDbContext CreateDb() => new(_dbOptions, 0); // tenantId=0 bypasses filter

    private async Task SeedCredentialAsync(string name, string apiKey, string scheme = "Bearer",
        bool isActive = true, DateTime? expiresAt = null, string? customHeaderName = null)
    {
        using var db = CreateDb();
        db.McpCredentials.Add(new McpCredentialEntity
        {
            TenantId = TenantId,
            Name = name,
            EncryptedApiKey = _encryptor.Encrypt(apiKey),
            AuthScheme = scheme,
            CustomHeaderName = customHeaderName,
            IsActive = isActive,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = "test-user",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ResolveAsync_ActiveCredential_ReturnsDecryptedKey()
    {
        await SeedCredentialAsync("weather-api", "sk-weather-123");

        var result = await _resolver.ResolveAsync(TenantId, "weather-api", 0, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("sk-weather-123", result.ApiKey);
        Assert.Equal("Bearer", result.AuthScheme);
    }

    [Fact]
    public async Task ResolveAsync_InactiveCredential_ReturnsNull()
    {
        await SeedCredentialAsync("disabled-key", "sk-disabled", isActive: false);

        var result = await _resolver.ResolveAsync(TenantId, "disabled-key", 0, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ExpiredCredential_ReturnsNull()
    {
        await SeedCredentialAsync("expired-key", "sk-expired", expiresAt: DateTime.UtcNow.AddHours(-1));

        var result = await _resolver.ResolveAsync(TenantId, "expired-key", 0, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_NonExpiredCredential_ReturnsKey()
    {
        await SeedCredentialAsync("future-key", "sk-future", expiresAt: DateTime.UtcNow.AddDays(30));

        var result = await _resolver.ResolveAsync(TenantId, "future-key", 0, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("sk-future", result.ApiKey);
    }

    [Fact]
    public async Task ResolveAsync_NonExistentCredential_ReturnsNull()
    {
        var result = await _resolver.ResolveAsync(TenantId, "no-such-cred", 0, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_CustomScheme_ReturnsSchemeAndHeader()
    {
        await SeedCredentialAsync("custom-key", "my-secret", "Custom", customHeaderName: "X-Custom-Auth");

        var result = await _resolver.ResolveAsync(TenantId, "custom-key", 0, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Custom", result.AuthScheme);
        Assert.Equal("X-Custom-Auth", result.CustomHeaderName);
    }

    [Fact]
    public async Task ResolveAsync_EmptyName_ReturnsNull()
    {
        var result = await _resolver.ResolveAsync(TenantId, "", 0, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_WrongTenant_ReturnsNull()
    {
        await SeedCredentialAsync("tenant-scoped", "sk-scoped");

        // Query with different tenant
        var result = await _resolver.ResolveAsync(999, "tenant-scoped", 0, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_CachedResult_DoesNotHitDbAgain()
    {
        await SeedCredentialAsync("cached-key", "sk-cached");

        // First call populates cache
        var first = await _resolver.ResolveAsync(TenantId, "cached-key", 0, CancellationToken.None);
        Assert.NotNull(first);

        // Delete the credential from DB
        using (var db = CreateDb())
        {
            var entity = await db.McpCredentials.FirstAsync(c => c.Name == "cached-key");
            db.McpCredentials.Remove(entity);
            await db.SaveChangesAsync();
        }

        // Second call should still return cached result
        var second = await _resolver.ResolveAsync(TenantId, "cached-key", 0, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal("sk-cached", second.ApiKey);
    }

    [Fact]
    public async Task InvalidateAsync_ForcesFreshResolution_AfterKeyIsUpdated()
    {
        // Pins a real reported bug: an admin rotates/edits a credential's key, but the agent kept
        // getting an "authentication error" using the OLD value for up to the cache TTL, because
        // nothing evicted the previously-resolved (now stale) entry.
        await SeedCredentialAsync("rotatable-key", "sk-old-value");

        var first = await _resolver.ResolveAsync(TenantId, "rotatable-key", 0, CancellationToken.None);
        Assert.Equal("sk-old-value", first!.ApiKey);

        using (var db = CreateDb())
        {
            var entity = await db.McpCredentials.FirstAsync(c => c.Name == "rotatable-key");
            entity.EncryptedApiKey = _encryptor.Encrypt("sk-new-value");
            await db.SaveChangesAsync();
        }

        // Without invalidation, this would still return the stale cached "sk-old-value".
        await _resolver.InvalidateAsync(TenantId, "rotatable-key", CancellationToken.None);

        var second = await _resolver.ResolveAsync(TenantId, "rotatable-key", 0, CancellationToken.None);
        Assert.Equal("sk-new-value", second!.ApiKey);
    }

    [Fact]
    public async Task InvalidateAsync_SweepsEveryTenantEnvironment_NotJustTheUnscopedSlot()
    {
        // The cache key includes the CALLING environment (not just the credential's own tag), so
        // invalidation must clear every environment this tenant has, not only the "0" wildcard slot.
        using (var db = CreateDb())
        {
            db.TenantEnvironments.Add(new TenantEnvironmentEntity
            {
                TenantId = TenantId, Slug = "prod", DisplayName = "Prod", Rank = 1, IsDefault = false,
            });
            await db.SaveChangesAsync();
        }
        var envId = await CreateDb().TenantEnvironments.Where(e => e.TenantId == TenantId).Select(e => e.Id).FirstAsync();

        await SeedCredentialAsync("env-scoped-key", "sk-old-value");

        var first = await _resolver.ResolveAsync(TenantId, "env-scoped-key", envId, CancellationToken.None);
        Assert.Equal("sk-old-value", first!.ApiKey);

        using (var db = CreateDb())
        {
            var entity = await db.McpCredentials.FirstAsync(c => c.Name == "env-scoped-key");
            entity.EncryptedApiKey = _encryptor.Encrypt("sk-new-value");
            await db.SaveChangesAsync();
        }

        await _resolver.InvalidateAsync(TenantId, "env-scoped-key", CancellationToken.None);

        var second = await _resolver.ResolveAsync(TenantId, "env-scoped-key", envId, CancellationToken.None);
        Assert.Equal("sk-new-value", second!.ApiKey);
    }
}
