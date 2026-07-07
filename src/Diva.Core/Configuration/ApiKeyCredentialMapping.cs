namespace Diva.Core.Configuration;

/// <summary>
/// A single per-API-key credential routing rule for a shared MCP tool server.
/// When an agent is invoked using the platform API key identified by <see cref="ApiKeyId"/>,
/// the server resolves its credential from <see cref="CredentialRef"/> (a McpCredentialEntity.Name).
/// </summary>
public sealed record ApiKeyCredentialMapping(int ApiKeyId, string CredentialRef);
