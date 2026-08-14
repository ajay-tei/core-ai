using System.Globalization;
using System.Text;

namespace Diva.LoadTest;

public static class StatsReporter
{
    public static void PrintSummary(string title, IReadOnlyList<RequestResult> results, DateTime testStartUtc, DateTime testEndUtc)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(title);
        Console.WriteLine(new string('=', 78));

        if (results.Count == 0)
        {
            Console.WriteLine("No requests completed.");
            return;
        }

        var wallSeconds = Math.Max(0.001, (testEndUtc - testStartUtc).TotalSeconds);
        var success = results.Where(r => r.Success).ToList();
        var failed = results.Where(r => !r.Success).ToList();

        Console.WriteLine($"Total requests   : {results.Count}");
        Console.WriteLine($"Succeeded        : {success.Count} ({Pct(success.Count, results.Count)}%)");
        Console.WriteLine($"Failed           : {failed.Count} ({Pct(failed.Count, results.Count)}%)");
        Console.WriteLine($"Wall clock       : {wallSeconds:F1}s");
        Console.WriteLine($"Throughput       : {success.Count / wallSeconds:F2} successful req/s");
        Console.WriteLine();

        PrintLatencyTable("End-to-end latency (ms)", success.Select(r => r.TotalMs).ToList());

        if (success.Any(r => r.TimeToFirstByteMs.HasValue))
        {
            Console.WriteLine();
            PrintLatencyTable("Time to first SSE byte (ms)", success.Where(r => r.TimeToFirstByteMs.HasValue).Select(r => r.TimeToFirstByteMs!.Value).ToList());
            PrintLatencyTable("Time to first token (ms)", success.Where(r => r.TimeToFirstTokenMs.HasValue).Select(r => r.TimeToFirstTokenMs!.Value).ToList());
            PrintLatencyTable("Time to first tool call (ms)", success.Where(r => r.TimeToFirstToolCallMs.HasValue).Select(r => r.TimeToFirstToolCallMs!.Value).ToList());

            var withTools = success.Where(r => r.ToolCallCount > 0).ToList();
            Console.WriteLine();
            Console.WriteLine($"Requests that used tools : {withTools.Count}/{success.Count}");
            if (withTools.Count > 0)
                Console.WriteLine($"Avg tool calls/request   : {withTools.Average(r => r.ToolCallCount):F1}  (max {withTools.Max(r => r.ToolCallCount)})");
            Console.WriteLine($"Avg iterations/request   : {success.Average(r => r.IterationCount):F1}  (max {(success.Count > 0 ? success.Max(r => r.IterationCount) : 0)})");
            var blocked = success.Count(r => r.VerificationBlocked);
            if (blocked > 0)
                Console.WriteLine($"Verification-blocked     : {blocked}");
        }

        if (failed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Error breakdown:");
            foreach (var g in failed.GroupBy(r => r.Error ?? "unknown").OrderByDescending(g => g.Count()))
                Console.WriteLine($"  [{g.Count(),4}] {g.Key}");
        }
    }

    /// <summary>Per-time-bucket table for ramp/soak scenarios — shows how latency/errors evolve as concurrency changes.</summary>
    public static void PrintTimeSeries(IReadOnlyList<RequestResult> results)
    {
        if (results.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine("Time series (10s buckets):");
        Console.WriteLine($"{"Bucket(s)",10} {"Users",7} {"Reqs",6} {"Errs",5} {"p50 ms",8} {"p95 ms",8} {"p99 ms",8}");

        foreach (var bucket in results.GroupBy(r => r.BucketSecond / 10 * 10).OrderBy(g => g.Key))
        {
            var items = bucket.ToList();
            var ok = items.Where(r => r.Success).Select(r => r.TotalMs).ToList();
            var users = items.Max(r => r.ActiveUsersAtStart);
            var errs = items.Count(r => !r.Success);
            Console.WriteLine($"{bucket.Key,10} {users,7} {items.Count,6} {errs,5} " +
                               $"{(ok.Count > 0 ? Percentile(ok, 50).ToString("F0") : "-"),8} " +
                               $"{(ok.Count > 0 ? Percentile(ok, 95).ToString("F0") : "-"),8} " +
                               $"{(ok.Count > 0 ? Percentile(ok, 99).ToString("F0") : "-"),8}");
        }
    }

    private static void PrintLatencyTable(string label, List<double> values)
    {
        Console.WriteLine($"{label}:");
        if (values.Count == 0) { Console.WriteLine("  (no data)"); return; }
        Console.WriteLine($"  min={values.Min():F0}  p50={Percentile(values, 50):F0}  p90={Percentile(values, 90):F0}  " +
                           $"p95={Percentile(values, 95):F0}  p99={Percentile(values, 99):F0}  max={values.Max():F0}  avg={values.Average():F0}");
    }

    public static double Percentile(List<double> sortedOrNot, double percentile)
    {
        if (sortedOrNot.Count == 0) return 0;
        var sorted = sortedOrNot.OrderBy(v => v).ToList();
        var rank = percentile / 100.0 * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    private static double Pct(int part, int whole) => whole == 0 ? 0 : Math.Round(part * 100.0 / whole, 1);

    public static void WriteCsv(string path, IReadOnlyList<RequestResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("StartedAtUtc,TotalMs,Success,HttpStatus,Error,TimeToFirstByteMs,TimeToFirstTokenMs,TimeToFirstToolCallMs,TimeToDoneMs,ToolCallCount,IterationCount,VerificationBlocked,BucketSecond,ActiveUsersAtStart");
        foreach (var r in results)
        {
            sb.Append(r.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TotalMs.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Success).Append(',')
              .Append(r.HttpStatus).Append(',')
              .Append('"').Append((r.Error ?? "").Replace("\"", "'")).Append('"').Append(',')
              .Append(r.TimeToFirstByteMs?.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TimeToFirstTokenMs?.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TimeToFirstToolCallMs?.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TimeToDoneMs?.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.ToolCallCount).Append(',')
              .Append(r.IterationCount).Append(',')
              .Append(r.VerificationBlocked).Append(',')
              .Append(r.BucketSecond).Append(',')
              .Append(r.ActiveUsersAtStart)
              .AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"\nRaw results written to {path}");
    }
}
