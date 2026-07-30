namespace Diva.Infrastructure.Promotion;

using System.Text.Json;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Snapshot serializer for tenant MCP server definitions. Matched by Name within the tenant on
/// materialize (see <see cref="IPromotableSnapshotSerializer"/> for the environment-scoping caveat).
/// </summary>
public sealed class McpServerSnapshotSerializer : IPromotableSnapshotSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IDatabaseProviderFactory _db;

    public string ObjectType => "McpServer";

    public McpServerSnapshotSerializer(IDatabaseProviderFactory db)
    {
        _db = db;
    }

    public async Task<SerializedSnapshot?> SerializeAsync(int tenantId, Guid logicalId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var server = await db.TenantMcpServers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.LogicalId == logicalId, ct);
        if (server is null)
        {
            return null;
        }

        var snapshot = new McpServerSnapshot
        {
            Name = server.Name,
            Description = server.Description,
            Transport = server.Transport,
            Command = server.Command,
            ArgsJson = server.ArgsJson,
            EnvJson = server.EnvJson,
            Endpoint = server.Endpoint,
            PassSsoToken = server.PassSsoToken,
            PassTenantHeaders = server.PassTenantHeaders,
            DefaultCredentialRef = server.DefaultCredentialRef,
        };
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        return new SerializedSnapshot { SnapshotJson = json, Name = server.Name };
    }

    public async Task MaterializeAsync(int tenantId, int environmentId, Guid logicalId, string snapshotJson, CancellationToken ct)
    {
        var snapshot = JsonSerializer.Deserialize<McpServerSnapshot>(snapshotJson, JsonOptions)
            ?? throw new InvalidOperationException("Invalid MCP server snapshot JSON.");

        using var db = _db.CreateDbContext();
        var server = await db.TenantMcpServers.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Name == snapshot.Name, ct);
        if (server is null)
        {
            server = new TenantMcpServerEntity { TenantId = tenantId, Name = snapshot.Name, CreatedAt = DateTime.UtcNow };
            db.TenantMcpServers.Add(server);
        }

        server.Description = snapshot.Description;
        server.Transport = snapshot.Transport;
        server.Command = snapshot.Command;
        server.ArgsJson = snapshot.ArgsJson;
        server.EnvJson = snapshot.EnvJson;
        server.Endpoint = snapshot.Endpoint;
        server.PassSsoToken = snapshot.PassSsoToken;
        server.PassTenantHeaders = snapshot.PassTenantHeaders;
        server.DefaultCredentialRef = snapshot.DefaultCredentialRef;
        server.UpdatedAt = DateTime.UtcNow;
        server.LogicalId = logicalId;
        server.EnvironmentId = environmentId;

        await db.SaveChangesAsync(ct);
    }
}
