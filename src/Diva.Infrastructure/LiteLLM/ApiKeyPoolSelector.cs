namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Stateless key-pool selection for multi-key LLM config rotation. No round-robin cursor or
/// per-key cooldown tracking (v1) — random proactive spread across concurrent requests plus
/// reactive "try a different key" on rate-limit already meaningfully multiplies the effective
/// rate-limit budget without needing any shared mutable state.
/// </summary>
internal static class ApiKeyPoolSelector
{
    /// <summary>Picks a random key from the pool. Returns the single key unchanged when the pool has ≤ 1 entry.</summary>
    public static string PickRandom(IReadOnlyList<string> pool)
        => pool.Count <= 1 ? pool[0] : pool[Random.Shared.Next(pool.Count)];

    /// <summary>
    /// Picks a random key from the pool that is NOT <paramref name="exclude"/> — used when retrying
    /// after a rate-limit so the same already-limited key isn't immediately reused. Falls back to
    /// <paramref name="exclude"/> itself when the pool has only one distinct entry.
    /// </summary>
    public static string PickDifferent(IReadOnlyList<string> pool, string exclude)
    {
        var candidates = pool.Where(k => k != exclude).ToList();
        return candidates.Count == 0 ? exclude : candidates[Random.Shared.Next(candidates.Count)];
    }
}
