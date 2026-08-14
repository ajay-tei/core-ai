namespace Diva.LoadTest;

public enum RequestMode { Invoke, Stream }
public enum ScenarioKind { Burst, Soak, Ramp }

/// <summary>All tunables for a load test run. Populated from CLI args — see README.md for the full list.</summary>
public sealed class LoadTestOptions
{
    public string BaseUrl { get; set; } = "http://localhost:6032";
    public string? ApiKey { get; set; }
    public List<string> AgentIds { get; set; } = [];
    public string Query { get; set; } = "Give me a one-sentence status update.";
    public RequestMode Mode { get; set; } = RequestMode.Invoke;
    public ScenarioKind Scenario { get; set; } = ScenarioKind.Burst;

    // Burst / Soak
    public int Users { get; set; } = 20;
    public int RequestsPerUser { get; set; } = 5;     // burst only
    public int DurationSeconds { get; set; } = 60;    // soak / ramp

    // Ramp
    public int RampStepSeconds { get; set; } = 15;
    public int RampStepUsers { get; set; } = 10;
    public int RampMaxUsers { get; set; } = 200;

    // Session behavior: "new" = every request is a brand-new session (simulates distinct users —
    // the realistic worst case for MCP cache/session-creation load); "shared" = all requests from
    // one virtual user reuse the same session (simulates one user having a multi-turn conversation).
    public bool SharedSessionPerUser { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 180;
    public bool Warmup { get; set; } = true;
    public string? OutputCsvPath { get; set; }

    public static LoadTestOptions Parse(string[] args)
    {
        var o = new LoadTestOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
            switch (args[i])
            {
                case "--base-url": o.BaseUrl = Next().TrimEnd('/'); break;
                case "--api-key": o.ApiKey = Next(); break;
                case "--agent-id": o.AgentIds.Add(Next()); break;
                case "--query": o.Query = Next(); break;
                case "--mode": o.Mode = Enum.Parse<RequestMode>(Next(), ignoreCase: true); break;
                case "--scenario": o.Scenario = Enum.Parse<ScenarioKind>(Next(), ignoreCase: true); break;
                case "--users": o.Users = int.Parse(Next()); break;
                case "--requests-per-user": o.RequestsPerUser = int.Parse(Next()); break;
                case "--duration": o.DurationSeconds = int.Parse(Next()); break;
                case "--ramp-step-seconds": o.RampStepSeconds = int.Parse(Next()); break;
                case "--ramp-step-users": o.RampStepUsers = int.Parse(Next()); break;
                case "--ramp-max-users": o.RampMaxUsers = int.Parse(Next()); break;
                case "--shared-session": o.SharedSessionPerUser = true; break;
                case "--request-timeout": o.RequestTimeoutSeconds = int.Parse(Next()); break;
                case "--no-warmup": o.Warmup = false; break;
                case "--output": o.OutputCsvPath = Next(); break;
                case "--help": case "-h": PrintUsageAndExit(); break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        return o;
    }

    private static void PrintUsageAndExit()
    {
        Console.WriteLine("""
        Usage: dotnet run --project tools/LoadTest -- [options]

          --base-url <url>            API base URL (default http://localhost:6032)
          --api-key <key>              Platform API key sent as X-API-Key (omit if OAuth:Enabled=false)
          --agent-id <id>              Agent to invoke; repeatable. Omit to auto-discover the first enabled agent.
          --query <text>               Message sent to the agent. Use a multi-step prompt to exercise MCP tool calls.
          --mode <invoke|stream>       invoke = buffered POST /invoke; stream = SSE POST /invoke/stream (default invoke)
          --scenario <burst|soak|ramp> burst = fixed concurrency, N requests/user, then stop (default)
                                       soak  = fixed concurrency for --duration seconds
                                       ramp  = step concurrency up every --ramp-step-seconds until --ramp-max-users or --duration
          --users <n>                  Concurrent virtual users (default 20)
          --requests-per-user <n>      Requests per user, burst only (default 5)
          --duration <sec>             Test duration, soak/ramp only (default 60)
          --ramp-step-seconds <sec>    Ramp: seconds between concurrency increases (default 15)
          --ramp-step-users <n>        Ramp: users added per step (default 10)
          --ramp-max-users <n>         Ramp: concurrency ceiling (default 200)
          --shared-session             Reuse one session per virtual user instead of a new session per request
          --request-timeout <sec>      Per-request client timeout (default 180)
          --no-warmup                  Skip the 1-request cache warmup before measuring
          --output <path.csv>          Write raw per-request results to a CSV file

        Examples:
          dotnet run --project tools/LoadTest -- --scenario burst --users 50 --requests-per-user 3
          dotnet run --project tools/LoadTest -- --mode stream --scenario soak --users 100 --duration 120 --query "Check tee time availability at all 5 courses and summarize."
          dotnet run --project tools/LoadTest -- --scenario ramp --ramp-max-users 300 --ramp-step-users 20 --duration 300
        """);
        Environment.Exit(0);
    }
}
