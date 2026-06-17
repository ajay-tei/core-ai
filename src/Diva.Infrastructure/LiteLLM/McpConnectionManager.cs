using System.Text.Json;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Context;
using Diva.Infrastructure.Data.Entities;
using Diva.Sso;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Handles all MCP client lifecycle concerns: connecting to configured tool servers,
/// listing available tools, and routing tool names to their originating client.
/// Extracted from <see cref="AnthropicAgentRunner"/> to keep MCP transport details isolated.
/// </summary>
public sealed class McpConnectionManager : IMcpConnectionManager
{
    private readonly IHttpContextAccessor _httpCtx;
    private readonly ICredentialResolver? _credentialResolver;
    private readonly ILogger<McpConnectionManager> _logger;

    public McpConnectionManager(
        IHttpContextAccessor httpCtx,
        ICredentialResolver? credentialResolver,
        ILogger<McpConnectionManager> logger)
    {
        _httpCtx             = httpCtx;
        _credentialResolver  = credentialResolver;
        _logger              = logger;
    }

    /// <summary>
    /// Connects to all valid MCP server bindings defined on <paramref name="definition"/> in parallel.
    /// A binding is valid when it has a non-empty name and either a command or an endpoint.
    /// Failed connections are logged and skipped — the runner receives a partial set of clients.
    /// </summary>
    public async Task<Dictionary<string, McpClient>> ConnectAsync(
        AgentDefinitionEntity definition, CancellationToken ct,
        TenantContext? fallbackTenant = null, bool forcePassSsoToken = false)
    {
        var result = new Dictionary<string, McpClient>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(definition.ToolBindings)) return result;

        List<McpToolBinding>? bindings;
        try { bindings = JsonSerializer.Deserialize<List<McpToolBinding>>(definition.ToolBindings, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Agent {AgentId}: ToolBindings JSON is malformed — no MCP tools will be available for this run",
                definition.Id);
            return result;
        }
        if (bindings is null) return result;

        var validBindings = bindings
            .Where(b => !string.IsNullOrEmpty(b.Name) &&
                        (!string.IsNullOrEmpty(b.Command) || !string.IsNullOrEmpty(b.Endpoint)))
            .ToList();

        var connectTasks = validBindings.Select(async b =>
        {
            try
            {
                var client = await CreateClientAsync(b, definition.TenantId, ct, fallbackTenant, forcePassSsoToken);
                _logger.LogInformation("Connected to MCP server {Name} ({Transport})", b.Name, b.Transport);
                return (b.Name, Client: (McpClient?)client);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to MCP server {Name}, skipping", b.Name);
                return (b.Name, Client: null);
            }
        });

        foreach (var (name, client) in await Task.WhenAll(connectTasks))
            if (client is not null) result[name] = client;

        return result;
    }

    /// <summary>
    /// Lists tools from all MCP clients in parallel, returning both the routing map
    /// (tool-name → McpClient) and the raw tool list.
    /// </summary>
    public async Task<(Dictionary<string, McpClient> Map, List<McpClientTool> Tools)> BuildToolDataAsync(
        Dictionary<string, McpClient> clients, CancellationToken ct)
    {
        var map      = new Dictionary<string, McpClient>(StringComparer.OrdinalIgnoreCase);
        var allTools = new List<McpClientTool>();
        if (clients.Count == 0) return (map, allTools);

        var listTasks = clients.Values.Select(async client =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            return (client, tools);
        });
        foreach (var (client, tools) in await Task.WhenAll(listTasks))
            foreach (var tool in tools) { map[tool.Name] = client; allTools.Add(tool); }
        return (map, allTools);
    }

    // ── Transport factory ────────────────────────────────────────────────────

    private async Task<McpClient> CreateClientAsync(McpToolBinding binding, int tenantId, CancellationToken ct, TenantContext? fallbackTenant = null, bool forcePassSsoToken = false)
    {
        // Resolve credential if referenced (for both HTTP and stdio transports)
        ResolvedCredential? credential = null;
        if (!string.IsNullOrEmpty(binding.CredentialRef) && _credentialResolver is not null)
        {
            _logger.LogDebug(
                "MCP binding '{Name}': resolving CredentialRef '{Ref}' for tenant {TenantId}",
                binding.Name, binding.CredentialRef, tenantId);

            credential = await _credentialResolver.ResolveAsync(tenantId, binding.CredentialRef, ct);
            if (credential is null)
                _logger.LogWarning(
                    "MCP binding '{Name}': CredentialRef '{Ref}' could not be resolved (not found, inactive, or expired) for tenant {TenantId}",
                    binding.Name, binding.CredentialRef, tenantId);
            else
                _logger.LogInformation(
                    "MCP binding '{Name}': credential '{Ref}' resolved (scheme={Scheme})",
                    binding.Name, binding.CredentialRef, credential.AuthScheme);
        }
        else if (!string.IsNullOrEmpty(binding.CredentialRef) && _credentialResolver is null)
        {
            _logger.LogWarning(
                "MCP binding '{Name}': CredentialRef '{Ref}' specified but no ICredentialResolver is registered",
                binding.Name, binding.CredentialRef);
        }

        if (binding.Transport.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            binding.Transport.Equals("sse", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(binding.Endpoint))
                throw new InvalidOperationException("HTTP transport requires an Endpoint URL.");

            bool effectivePassSso = binding.PassSsoToken || forcePassSsoToken;
            HttpClient httpClient;
            if (effectivePassSso || binding.PassTenantHeaders || credential is not null)
            {
                bool includeBearerToken = effectivePassSso;
                _logger.LogInformation(
                    "MCP binding '{Name}': creating SsoAwareHttpMessageHandler (PassSsoToken={PassSso}, ForcePassSso={ForcePassSso}, PassTenantHeaders={PassTenant}, HasCredential={HasCred})",
                    binding.Name, binding.PassSsoToken, forcePassSsoToken, binding.PassTenantHeaders, credential is not null);

                var handler = new SsoAwareHttpMessageHandler(_httpCtx, ctx =>
                {
                    var tcSource = ctx?.TryGetTenantContext() is not null ? "HttpContext" : (fallbackTenant is not null ? "fallbackTenant" : "none");
                    var tc = ctx?.TryGetTenantContext() ?? fallbackTenant;

                    _logger.LogDebug(
                        "MCP binding '{Name}': headers factory invoked (tenantContextSource={Source}, hasTenantContext={HasTc})",
                        binding.Name, tcSource, tc is not null);

                    // Build base headers from SSO/tenant context
                    Dictionary<string, string> headers;
                    if (tc is not null)
                    {
                        // AccessToken is only set for SSO-authenticated users (sso_token claim in JWT).
                        // Local-auth users and API-key users have AccessToken=null — their auth is
                        // handled via CredentialRef injection below (per MCP binding).
                        bool hasToken = !string.IsNullOrEmpty(tc.AccessToken);

                        // NOTE: InboundApiKey (X-API-Key used to authenticate with Diva) is intentionally
                        // NOT forwarded to MCP tool servers. Each MCP binding configures its own
                        // CredentialRef for tool server authentication via the credential vault.

                        if (includeBearerToken && hasToken)
                        {
                            _logger.LogInformation(
                                "MCP binding '{Name}': forwarding SSO Bearer token to MCP server (tenantId={TenantId}, source={Source})",
                                binding.Name, tc.TenantId, tcSource);
                        }
                        else if (includeBearerToken)
                        {
                            if (credential is not null)
                                _logger.LogDebug(
                                    "MCP binding '{Name}': no SSO token, using credential auth (tenantId={TenantId}, source={Source})",
                                    binding.Name, tc.TenantId, tcSource);
                            else
                                _logger.LogWarning(
                                    "MCP binding '{Name}': PassSsoToken=true but no token available and no credential configured — MCP calls will be unauthenticated (tenantId={TenantId})",
                                    binding.Name, tc.TenantId);
                        }
                        else
                        {
                            _logger.LogDebug(
                                "MCP binding '{Name}': SSO token not requested, injecting tenant headers only (tenantId={TenantId}, siteId={SiteId})",
                                binding.Name, tc.TenantId, tc.CurrentSiteId);
                        }

                        var mcpCtx = includeBearerToken && hasToken
                            ? McpRequestContext.FromTenant(tc)
                            : new McpRequestContext
                            {
                                TenantId      = tc.TenantId,
                                SiteId        = tc.CurrentSiteId,
                                CorrelationId = tc.CorrelationId,
                                CustomHeaders = tc.CustomHeaders
                            };
                        headers = mcpCtx.ToHeaders();

                        // Inject SSO-configured forward headers (only when actively forwarding SSO token).
                        // Authorization is reserved — skip silently to avoid clobbering the SSO bearer.
                        if (includeBearerToken && hasToken && tc.SsoForwardHeaders.Count > 0)
                        {
                            foreach (var (hKey, hValue) in tc.SsoForwardHeaders)
                            {
                                if (hKey.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                                {
                                    _logger.LogWarning(
                                        "MCP binding '{Name}': SSO forward header 'Authorization' is reserved — skipping",
                                        binding.Name);
                                    continue;
                                }
                                headers[hKey] = hValue;
                            }
                            _logger.LogDebug(
                                "MCP binding '{Name}': injected {Count} SSO forward header(s) from SSO config",
                                binding.Name, tc.SsoForwardHeaders.Count);
                        }
                    }
                    else
                    {
                        headers = [];
                        if (credential is null)
                            _logger.LogWarning(
                                "MCP binding '{Name}': no TenantContext available and no credential — no auth headers will be injected",
                                binding.Name);
                        else
                            _logger.LogDebug(
                                "MCP binding '{Name}': no TenantContext available, will use credential auth only",
                                binding.Name);
                    }

                    // Inject credential.
                    // Bearer/default: only when Authorization isn't already set (don't override SSO token).
                    // ApiKey / Custom: always inject — they use separate headers and coexist with Bearer.
                    if (credential is not null)
                    {
                        var scheme = credential.AuthScheme.ToLowerInvariant();
                        switch (scheme)
                        {
                            case "bearer":
                                if (!headers.ContainsKey("Authorization"))
                                {
                                    headers["Authorization"] = $"Bearer {credential.ApiKey}";
                                    _logger.LogInformation(
                                        "MCP binding '{Name}': injecting credential as Bearer token (credentialRef={Ref})",
                                        binding.Name, binding.CredentialRef);
                                }
                                else
                                {
                                    _logger.LogDebug(
                                        "MCP binding '{Name}': SSO Bearer already present, skipping Bearer credential (credentialRef={Ref})",
                                        binding.Name, binding.CredentialRef);
                                }
                                break;
                            case "apikey":
                                headers["X-API-Key"] = credential.ApiKey;
                                _logger.LogInformation(
                                    "MCP binding '{Name}': injecting credential as X-API-Key header (credentialRef={Ref})",
                                    binding.Name, binding.CredentialRef);
                                break;
                            case "custom" when !string.IsNullOrEmpty(credential.CustomHeaderName):
                                headers[credential.CustomHeaderName] = credential.ApiKey;
                                _logger.LogInformation(
                                    "MCP binding '{Name}': injecting credential as custom header '{Header}' (credentialRef={Ref})",
                                    binding.Name, credential.CustomHeaderName, binding.CredentialRef);
                                break;
                            default:
                                if (!headers.ContainsKey("Authorization"))
                                {
                                    headers["Authorization"] = $"Bearer {credential.ApiKey}";
                                    _logger.LogWarning(
                                        "MCP binding '{Name}': unknown auth scheme '{Scheme}', defaulting to Bearer (credentialRef={Ref})",
                                        binding.Name, credential.AuthScheme, binding.CredentialRef);
                                }
                                break;
                        }
                    }

                    return headers;
                }, _logger);
                httpClient = new HttpClient(handler);
            }
            else
            {
                httpClient = new HttpClient();
            }

            var transport = new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(binding.Endpoint), Name = binding.Name },
                httpClient);
            return await McpClient.CreateAsync(transport, cancellationToken: ct);
        }

        // stdio: command + args array + env vars (matches Claude Desktop config format)
        _logger.LogDebug(
            "MCP binding '{Name}': creating stdio transport (command={Command}, args={Args})",
            binding.Name, binding.Command, string.Join(' ', binding.Args));

        var envVars = binding.Env.Count > 0
            ? binding.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value)
            : new Dictionary<string, string?>();

        // Inject credential API key as MCP_API_KEY env var for stdio transports
        if (credential is not null)
        {
            envVars["MCP_API_KEY"] = credential.ApiKey;
            _logger.LogInformation(
                "MCP binding '{Name}': injecting MCP_API_KEY env var for stdio transport (credentialRef={Ref}, scheme={Scheme})",
                binding.Name, binding.CredentialRef, credential.AuthScheme);
        }
        else
        {
            _logger.LogDebug(
                "MCP binding '{Name}': no credential configured for stdio transport",
                binding.Name);
        }

        var stdioTransport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name                 = binding.Name,
            Command              = binding.Command,
            Arguments            = binding.Args,
            EnvironmentVariables = envVars.Count > 0 ? envVars : null
        });
        return await McpClient.CreateAsync(stdioTransport, cancellationToken: ct);
    }
}
