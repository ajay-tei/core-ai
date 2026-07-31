using Diva.Agents.Tests.Helpers;
using Diva.Agents.Workers;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.A2A;
using Diva.Infrastructure.Data.Entities;
using NSubstitute;

namespace Diva.Agents.Tests;

/// <summary>
/// Unit tests for <see cref="RemoteA2AAgent"/>.
/// </summary>
public class RemoteA2AAgentTests
{
    private readonly IA2AAgentClient _client = Substitute.For<IA2AAgentClient>();
    private readonly ICredentialResolver _creds = Substitute.For<ICredentialResolver>();

    private AgentDefinitionEntity MakeAgent(string? a2aEndpoint = "https://remote.example.com",
        string? secretRef = null, string? authScheme = null) => new()
    {
        Id = "remote-1",
        Name = "remote",
        DisplayName = "Remote Agent",
        Description = "A remote A2A agent",
        AgentType = "remote",
        Capabilities = """["chat"]""",
        A2AEndpoint = a2aEndpoint,
        A2ASecretRef = secretRef,
        A2AAuthScheme = authScheme,
    };

    private static async IAsyncEnumerable<AgentStreamChunk> FakeStream(params AgentStreamChunk[] chunks)
    {
        foreach (var c in chunks)
        {
            await Task.CompletedTask;
            yield return c;
        }
    }

    [Fact]
    public void GetCapability_ReturnsCorrectAgentInfo()
    {
        var agent = MakeAgent();
        var sut = new RemoteA2AAgent(agent, _client, _creds);

        var cap = sut.GetCapability();

        Assert.Equal("remote-1", cap.AgentId);
        Assert.Contains("chat", cap.Capabilities);
    }

    [Fact]
    public async Task ExecuteAsync_CollectsStreamAndReturnsFinalResponse()
    {
        var agent = MakeAgent();
        var sut = new RemoteA2AAgent(agent, _client, _creds);
        var tenant = AgentTestFixtures.BasicTenant();
        var request = AgentTestFixtures.BasicRequest("test query");

        _client.SendTaskAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(FakeStream(
                new AgentStreamChunk { Type = "thinking", Content = "Planning..." },
                new AgentStreamChunk { Type = "final_response", Content = "The answer is 42" },
                new AgentStreamChunk { Type = "done" }
            ));

        var result = await sut.ExecuteAsync(request, tenant, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("The answer is 42", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFalse_WhenNoFinalResponse()
    {
        var agent = MakeAgent();
        var sut = new RemoteA2AAgent(agent, _client, _creds);

        _client.SendTaskAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(FakeStream(new AgentStreamChunk { Type = "done" }));

        var result = await sut.ExecuteAsync(AgentTestFixtures.BasicRequest(), AgentTestFixtures.BasicTenant(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no response", result.Content);
    }

    [Fact]
    public async Task InvokeStreamAsync_ResolvesCredentialViaResolver()
    {
        var agent = MakeAgent(secretRef: "my-cred", authScheme: "Bearer");
        var sut = new RemoteA2AAgent(agent, _client, _creds);

        _creds.ResolveAsync(1, "my-cred", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedCredential("resolved-token", "Bearer", null));

        _client.SendTaskAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(FakeStream(new AgentStreamChunk { Type = "done" }));

        await foreach (var _ in sut.InvokeStreamAsync(AgentTestFixtures.BasicRequest(), AgentTestFixtures.BasicTenant(), CancellationToken.None))
        { }

        _client.Received(1).SendTaskAsync(
            "https://remote.example.com",
            "resolved-token",
            Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(),
            "Bearer",
            null);
    }

    [Fact]
    public async Task InvokeStreamAsync_UsesApiKeyScheme()
    {
        var agent = MakeAgent(secretRef: "api-cred", authScheme: "ApiKey");
        var sut = new RemoteA2AAgent(agent, _client, _creds);

        _creds.ResolveAsync(1, "api-cred", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedCredential("my-api-key", "ApiKey", null));

        _client.SendTaskAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(FakeStream(new AgentStreamChunk { Type = "done" }));

        await foreach (var _ in sut.InvokeStreamAsync(AgentTestFixtures.BasicRequest(), AgentTestFixtures.BasicTenant(), CancellationToken.None))
        { }

        _client.Received(1).SendTaskAsync(
            Arg.Any<string>(), "my-api-key", Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(), "ApiKey", null);
    }

    [Fact]
    public async Task InvokeStreamAsync_UsesCustomHeaderScheme()
    {
        var agent = MakeAgent(secretRef: "custom-cred", authScheme: "Custom");
        var sut = new RemoteA2AAgent(agent, _client, _creds);

        _creds.ResolveAsync(1, "custom-cred", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedCredential("custom-token", "Custom", "X-My-Auth"));

        _client.SendTaskAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(FakeStream(new AgentStreamChunk { Type = "done" }));

        await foreach (var _ in sut.InvokeStreamAsync(AgentTestFixtures.BasicRequest(), AgentTestFixtures.BasicTenant(), CancellationToken.None))
        { }

        _client.Received(1).SendTaskAsync(
            Arg.Any<string>(), "custom-token", Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(), "Custom", "X-My-Auth");
    }

    [Fact]
    public async Task InvokeStreamAsync_FallsBackToRawSecretRef_WhenNoResolver()
    {
        var agent = MakeAgent(secretRef: "raw-token-value");
        var sut = new RemoteA2AAgent(agent, _client, credentialResolver: null);

        _client.SendTaskAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(FakeStream(new AgentStreamChunk { Type = "done" }));

        await foreach (var _ in sut.InvokeStreamAsync(AgentTestFixtures.BasicRequest(), AgentTestFixtures.BasicTenant(), CancellationToken.None))
        { }

        _client.Received(1).SendTaskAsync(
            Arg.Any<string>(), "raw-token-value", Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task InvokeStreamAsync_SendsNoAuth_WhenNoSecretRef()
    {
        var agent = MakeAgent(secretRef: null);
        var sut = new RemoteA2AAgent(agent, _client, _creds);

        _client.SendTaskAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(FakeStream(new AgentStreamChunk { Type = "done" }));

        await foreach (var _ in sut.InvokeStreamAsync(AgentTestFixtures.BasicRequest(), AgentTestFixtures.BasicTenant(), CancellationToken.None))
        { }

        _client.Received(1).SendTaskAsync(
            Arg.Any<string>(), Arg.Is<string?>(s => s == null), Arg.Any<AgentRequest>(),
            Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string?>());
    }
}
