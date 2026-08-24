namespace Diva.LoadTest;

/// <summary>
/// Thread-safe counters + a periodic console ticker so long-running scenarios (each request can take
/// 20-70+ seconds against a real LLM) show visible progress instead of going silent until the end.
/// </summary>
public sealed class ProgressTracker
{
    private int _completed;
    private int _succeeded;
    private int _failed;
    private int _inFlight;
    private readonly int? _totalPlanned;
    private readonly DateTime _startUtc;

    public ProgressTracker(int? totalPlanned, DateTime startUtc)
    {
        _totalPlanned = totalPlanned;
        _startUtc = startUtc;
    }

    public void RequestStarting() => Interlocked.Increment(ref _inFlight);

    public void RequestFinished(bool success)
    {
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Increment(ref _completed);
        if (success) Interlocked.Increment(ref _succeeded);
        else Interlocked.Increment(ref _failed);
    }

    private string Snapshot(int activeUsers)
    {
        var elapsed = (DateTime.UtcNow - _startUtc).TotalSeconds;
        var completed = Volatile.Read(ref _completed);
        var progress = _totalPlanned is { } total ? $"{completed}/{total} done" : $"{completed} done";
        return $"[t={elapsed,5:F0}s] {progress}  ok={Volatile.Read(ref _succeeded)}  failed={Volatile.Read(ref _failed)}  " +
               $"in-flight={Volatile.Read(ref _inFlight)}  users={activeUsers}";
    }

    /// <summary>Prints a status line every <paramref name="interval"/> until <paramref name="until"/> completes.</summary>
    public async Task RunTickerAsync(Func<int> activeUsersFn, Task until, TimeSpan interval)
    {
        while (!until.IsCompleted)
        {
            await Task.WhenAny(until, Task.Delay(interval));
            if (!until.IsCompleted)
                Console.WriteLine(Snapshot(activeUsersFn()));
        }
    }
}
