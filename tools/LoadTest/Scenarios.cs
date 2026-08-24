using System.Collections.Concurrent;

namespace Diva.LoadTest;

public static class Scenarios
{
    private static async Task<(RequestResult Result, string? SessionId)> RunOneAsync(
        DivaApiClient client, LoadTestOptions opts, string agentId, string? sessionId, int bucketSecond, int activeUsers) =>
        opts.Mode == RequestMode.Stream
            ? await client.InvokeStreamAsync(agentId, opts.Query, sessionId, TimeSpan.FromSeconds(opts.RequestTimeoutSeconds), bucketSecond, activeUsers)
            : await client.InvokeAsync(agentId, opts.Query, sessionId, TimeSpan.FromSeconds(opts.RequestTimeoutSeconds), bucketSecond, activeUsers);

    /// <summary>Fixed concurrency, each virtual user fires --requests-per-user requests back-to-back, then stops.</summary>
    public static async Task<List<RequestResult>> RunBurstAsync(LoadTestOptions opts, DivaApiClient client, List<string> agentIds, DateTime testStart)
    {
        var results = new ConcurrentBag<RequestResult>();
        var tracker = new ProgressTracker(opts.Users * opts.RequestsPerUser, testStart);
        var tasks = new List<Task>();

        for (var u = 0; u < opts.Users; u++)
        {
            var agentId = agentIds[u % agentIds.Count];
            tasks.Add(Task.Run(async () =>
            {
                string? sessionId = null;
                for (var r = 0; r < opts.RequestsPerUser; r++)
                {
                    var bucket = (int)(DateTime.UtcNow - testStart).TotalSeconds;
                    tracker.RequestStarting();
                    var (result, returnedSessionId) = await RunOneAsync(client, opts, agentId, opts.SharedSessionPerUser ? sessionId : null, bucket, opts.Users);
                    tracker.RequestFinished(result.Success);
                    if (opts.SharedSessionPerUser) sessionId = returnedSessionId ?? sessionId;
                    results.Add(result);
                }
            }));
        }

        var mainTask = Task.WhenAll(tasks);
        var tickerTask = tracker.RunTickerAsync(() => opts.Users, mainTask, TimeSpan.FromSeconds(5));
        await mainTask;
        await tickerTask;
        return [.. results];
    }

    /// <summary>Fixed concurrency, looping continuously for --duration seconds — tests sustained load.</summary>
    public static async Task<List<RequestResult>> RunSoakAsync(LoadTestOptions opts, DivaApiClient client, List<string> agentIds, DateTime testStart)
    {
        var results = new ConcurrentBag<RequestResult>();
        var tracker = new ProgressTracker(totalPlanned: null, testStart);
        using var stopAt = new CancellationTokenSource(TimeSpan.FromSeconds(opts.DurationSeconds));
        var tasks = new List<Task>();

        for (var u = 0; u < opts.Users; u++)
        {
            var agentId = agentIds[u % agentIds.Count];
            tasks.Add(Task.Run(async () =>
            {
                string? sessionId = null;
                while (!stopAt.IsCancellationRequested)
                {
                    var bucket = (int)(DateTime.UtcNow - testStart).TotalSeconds;
                    tracker.RequestStarting();
                    var (result, returnedSessionId) = await RunOneAsync(client, opts, agentId, opts.SharedSessionPerUser ? sessionId : null, bucket, opts.Users);
                    tracker.RequestFinished(result.Success);
                    if (opts.SharedSessionPerUser) sessionId = returnedSessionId ?? sessionId;
                    results.Add(result);
                }
            }));
        }

        var mainTask = Task.WhenAll(tasks);
        var tickerTask = tracker.RunTickerAsync(() => opts.Users, mainTask, TimeSpan.FromSeconds(5));
        try { await mainTask; } catch (OperationCanceledException) { /* expected at duration cutoff */ }
        await tickerTask;
        return [.. results];
    }

    /// <summary>
    /// Steps concurrency up by --ramp-step-users every --ramp-step-seconds until --ramp-max-users or
    /// --duration is reached, so the time-series report shows where latency/errors start climbing.
    /// </summary>
    public static async Task<List<RequestResult>> RunRampAsync(LoadTestOptions opts, DivaApiClient client, List<string> agentIds, DateTime testStart)
    {
        var results = new ConcurrentBag<RequestResult>();
        var tracker = new ProgressTracker(totalPlanned: null, testStart);
        var currentUsers = 0;
        using var overallStop = new CancellationTokenSource(TimeSpan.FromSeconds(opts.DurationSeconds));
        var userTasks = new List<Task>();
        var nextUserIndex = 0;

        void SpawnUsers(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var agentId = agentIds[nextUserIndex++ % agentIds.Count];
                userTasks.Add(Task.Run(async () =>
                {
                    string? sessionId = null;
                    while (!overallStop.IsCancellationRequested)
                    {
                        var bucket = (int)(DateTime.UtcNow - testStart).TotalSeconds;
                        var snapshotUsers = Volatile.Read(ref currentUsers);
                        tracker.RequestStarting();
                        var (result, returnedSessionId) = await RunOneAsync(client, opts, agentId, opts.SharedSessionPerUser ? sessionId : null, bucket, snapshotUsers);
                        tracker.RequestFinished(result.Success);
                        if (opts.SharedSessionPerUser) sessionId = returnedSessionId ?? sessionId;
                        results.Add(result);
                    }
                }));
            }
        }

        Console.WriteLine($"Ramping up: +{opts.RampStepUsers} users every {opts.RampStepSeconds}s, up to {opts.RampMaxUsers}, for {opts.DurationSeconds}s total...");
        while (!overallStop.IsCancellationRequested && currentUsers < opts.RampMaxUsers)
        {
            var step = Math.Min(opts.RampStepUsers, opts.RampMaxUsers - currentUsers);
            SpawnUsers(step);
            currentUsers += step;
            Console.WriteLine($"  t={(int)(DateTime.UtcNow - testStart).TotalSeconds,4}s  users={currentUsers}");
            try { await Task.Delay(TimeSpan.FromSeconds(opts.RampStepSeconds), overallStop.Token); }
            catch (OperationCanceledException) { break; }
        }

        // Hold at the final concurrency level for whatever duration remains, printing periodic progress.
        var mainTask = Task.WhenAll(userTasks);
        var tickerTask = tracker.RunTickerAsync(() => Volatile.Read(ref currentUsers), mainTask, TimeSpan.FromSeconds(5));
        try { await mainTask; } catch (OperationCanceledException) { /* expected at duration cutoff */ }
        await tickerTask;
        return [.. results];
    }
}
