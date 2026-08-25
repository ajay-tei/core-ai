using Diva.Infrastructure.LiteLLM;

namespace Diva.Agents.Tests;

public class ApiKeyPoolSelectorTests
{
    [Fact]
    public void PickRandom_SingleKeyPool_ReturnsThatKey()
    {
        var result = ApiKeyPoolSelector.PickRandom(["only-key"]);
        Assert.Equal("only-key", result);
    }

    [Fact]
    public void PickRandom_MultiKeyPool_AlwaysReturnsAPoolMember()
    {
        IReadOnlyList<string> pool = ["a", "b", "c"];
        for (var i = 0; i < 50; i++)
            Assert.Contains(ApiKeyPoolSelector.PickRandom(pool), pool);
    }

    [Fact]
    public void PickDifferent_MultiKeyPool_NeverReturnsTheExcludedKey()
    {
        IReadOnlyList<string> pool = ["a", "b", "c"];
        for (var i = 0; i < 50; i++)
            Assert.NotEqual("a", ApiKeyPoolSelector.PickDifferent(pool, "a"));
    }

    [Fact]
    public void PickDifferent_OnlyOneDistinctKey_FallsBackToTheExcludedKey()
    {
        var result = ApiKeyPoolSelector.PickDifferent(["only-key"], "only-key");
        Assert.Equal("only-key", result);
    }

    [Fact]
    public void PickDifferent_ExcludedKeyNotInPool_StillPicksFromPool()
    {
        IReadOnlyList<string> pool = ["a", "b"];
        var result = ApiKeyPoolSelector.PickDifferent(pool, "not-in-pool");
        Assert.Contains(result, pool);
    }
}
