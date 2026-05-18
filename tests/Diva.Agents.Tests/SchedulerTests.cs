using Diva.Core.Configuration;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Scheduler;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diva.Agents.Tests;

/// <summary>
/// Tests for ScheduledTaskService (service + DB) and SchedulerHostedService helpers.
///
/// All DB tests use a real in-memory SQLite connection — no mocking of the database.
/// Pure helper tests invoke internal static methods directly.
/// </summary>
public class SchedulerTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _dbOptions;
    private readonly ScheduledTaskService _service;

    public SchedulerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<DivaDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new DivaDbContext(_dbOptions);
        db.Database.EnsureCreated();

        var factory = new TestDbFactory(_dbOptions);
        var opts = Options.Create(new TaskSchedulerOptions
        {
            MaxQueuedRunsPerTask = 3,
            MaxResponseStorageChars = 100
        });
        _service = new ScheduledTaskService(factory, opts, NullLogger<ScheduledTaskService>.Instance);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    // ── Next-run calculation — pure unit tests ────────────────────────────────

    [Fact]
    public void ComputeNextRunUtc_Once_FutureTime_ReturnsSameTime()
    {
        var future = DateTime.UtcNow.AddHours(2);
        var task = OneTimeTask(future);
        var result = ScheduledTaskService.ComputeNextRunUtc(task, DateTime.UtcNow);
        Assert.Equal(future, result);
    }

    [Fact]
    public void ComputeNextRunUtc_Once_PastTime_ReturnsNull()
    {
        var past = DateTime.UtcNow.AddHours(-1);
        var task = OneTimeTask(past);
        var result = ScheduledTaskService.ComputeNextRunUtc(task, DateTime.UtcNow);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeNextRunUtc_Daily_ReturnsNextAvailableSlot()
    {
        // Schedule daily at 23:59 UTC; from very early UTC today → next slot is today
        var task = new ScheduledTaskEntity
        {
            ScheduleType = "daily",
            RunAtTime = "23:59",
            TimeZoneId = "UTC"
        };
        var from = new DateTime(2026, 3, 23, 0, 0, 0, DateTimeKind.Utc);
        var result = ScheduledTaskService.ComputeNextRunUtc(task, from);
        Assert.NotNull(result);
        Assert.Equal(23, result!.Value.Hour);
        Assert.Equal(59, result.Value.Minute);
    }

    [Fact]
    public void ComputeNextRunUtc_Daily_AfterTodaySlot_ReturnsNextDay()
    {
        var task = new ScheduledTaskEntity
        {
            ScheduleType = "daily",
            RunAtTime = "08:00",
            TimeZoneId = "UTC"
        };
        // Current time is 09:00 — already past 08:00 today
        var from = new DateTime(2026, 3, 23, 9, 0, 0, DateTimeKind.Utc);
        var result = ScheduledTaskService.ComputeNextRunUtc(task, from);
        Assert.NotNull(result);
        Assert.Equal(2026, result!.Value.Year);
        Assert.Equal(3, result.Value.Month);
        Assert.Equal(24, result.Value.Day);   // next day
        Assert.Equal(8, result.Value.Hour);
    }

    [Fact]
    public void ComputeNextRunUtc_Weekly_ReturnsCorrectDayOfWeek()
    {
        // 2026-03-23 is Monday (DayOfWeek.Monday = 1)
        // Schedule on Friday (5) at 12:00
        var task = new ScheduledTaskEntity
        {
            ScheduleType = "weekly",
            RunAtTime = "12:00",
            DayOfWeek = (int)DayOfWeek.Friday,
            TimeZoneId = "UTC"
        };
        var from = new DateTime(2026, 3, 23, 0, 0, 0, DateTimeKind.Utc);
        var result = ScheduledTaskService.ComputeNextRunUtc(task, from);
        Assert.NotNull(result);
        Assert.Equal(DayOfWeek.Friday, result!.Value.DayOfWeek);
    }

    [Fact]
    public void ComputeNextRunUtc_Hourly_ReturnsOneHourFromNow()
    {
        var task = new ScheduledTaskEntity { ScheduleType = "hourly", TimeZoneId = "UTC" };
        var from = new DateTime(2026, 3, 24, 10, 0, 0, DateTimeKind.Utc);
        var result = ScheduledTaskService.ComputeNextRunUtc(task, from);
        Assert.Equal(from.AddHours(1), result);
    }

    [Fact]
    public void TryParseRunAtTime_ValidTime_ReturnsTrue()
    {
        bool ok = ScheduledTaskService.TryParseRunAtTime("09:30", out var ts);
        Assert.True(ok);
        Assert.Equal(9, ts.Hours);
        Assert.Equal(30, ts.Minutes);
    }

    [Fact]
    public void TryParseRunAtTime_InvalidFormats_ReturnFalse()
    {
        Assert.False(ScheduledTaskService.TryParseRunAtTime(null, out _));
        Assert.False(ScheduledTaskService.TryParseRunAtTime("", out _));
        Assert.False(ScheduledTaskService.TryParseRunAtTime("25:00", out _));
        Assert.False(ScheduledTaskService.TryParseRunAtTime("08:60", out _));
        Assert.False(ScheduledTaskService.TryParseRunAtTime("nottime", out _));
    }

    // ── Service + DB integration tests ────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_SetsNextRunUtc_WhenEnabled()
    {
        using var db = new DivaDbContext(_dbOptions);
        db.AgentDefinitions.Add(new AgentDefinitionEntity
        { Id = "a1", Name = "Agent", DisplayName = "Test", TenantId = 1 });
        await db.SaveChangesAsync();

        var result = await _service.CreateAsync(1, new CreateScheduledTaskRequest(
            AgentId: "a1", Name: "Daily Job", Description: null,
            ScheduleType: "daily", ScheduledAtUtc: null, RunAtTime: "09:00",
            DayOfWeek: null, TimeZoneId: "UTC",
            PayloadType: "prompt", PromptText: "Run the job",
            ParametersJson: null, IsEnabled: true), CancellationToken.None);

        Assert.NotNull(result.NextRunUtc);
        Assert.True(result.NextRunUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task CreateAsync_NullNextRun_WhenDisabled()
    {
        var result = await _service.CreateAsync(1, new CreateScheduledTaskRequest(
            AgentId: "a1", Name: "Disabled Job", Description: null,
            ScheduleType: "daily", ScheduledAtUtc: null, RunAtTime: "09:00",
            DayOfWeek: null, TimeZoneId: "UTC",
            PayloadType: "prompt", PromptText: "Run the job",
            ParametersJson: null, IsEnabled: false), CancellationToken.None);

        Assert.Null(result.NextRunUtc);
    }

    [Fact]
    public async Task ListAsync_TenantIsolation_ReturnsOnlyOwnTenant()
    {
        await _service.CreateAsync(1, DailyReq("Job T1"), CancellationToken.None);
        await _service.CreateAsync(2, DailyReq("Job T2"), CancellationToken.None);

        var t1 = await _service.ListAsync(1, CancellationToken.None);
        var t2 = await _service.ListAsync(2, CancellationToken.None);

        Assert.All(t1, j => Assert.Equal(1, j.TenantId));
        Assert.All(t2, j => Assert.Equal(2, j.TenantId));
        Assert.DoesNotContain(t1, j => j.Name == "Job T2");
        Assert.DoesNotContain(t2, j => j.Name == "Job T1");
    }

    [Fact]
    public async Task DeleteAsync_RemovesTask()
    {
        var created = await _service.CreateAsync(1, DailyReq("Temp Job"), CancellationToken.None);
        await _service.DeleteAsync(1, created.Id, CancellationToken.None);
        var result = await _service.GetAsync(1, created.Id, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetEnabledAsync_TogglesAndRecalculatesNextRun()
    {
        var created = await _service.CreateAsync(1, DailyReq("Toggle Job"), CancellationToken.None);
        Assert.NotNull(created.NextRunUtc);

        var disabled = await _service.SetEnabledAsync(1, created.Id, false, CancellationToken.None);
        Assert.Null(disabled.NextRunUtc);

        var enabled = await _service.SetEnabledAsync(1, created.Id, true, CancellationToken.None);
        Assert.NotNull(enabled.NextRunUtc);
    }

    [Fact]
    public async Task BeginRunAsync_CreatesRunningRecord_WhenNoOtherRunActive()
    {
        var task = await _service.CreateAsync(1, DailyReq("BeginRun Job"), CancellationToken.None);
        var run = await _service.BeginRunAsync(task.Id, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal("running", run.Status);
        Assert.NotNull(run.StartedAtUtc);
    }

    [Fact]
    public async Task BeginRunAsync_CreatesPendingRecord_WhenRunAlreadyActive()
    {
        var task = await _service.CreateAsync(1, DailyReq("Overlap Job"), CancellationToken.None);
        var run1 = await _service.BeginRunAsync(task.Id, DateTime.UtcNow, CancellationToken.None);
        var run2 = await _service.BeginRunAsync(task.Id, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal("running", run1.Status);
        Assert.Equal("pending", run2.Status);
        Assert.Null(run2.StartedAtUtc);
    }

    [Fact]
    public async Task CompleteRunAsync_MarksSuccess_AndRecordsResponse()
    {
        var task = await _service.CreateAsync(1, DailyReq("Complete Job"), CancellationToken.None);
        var run = await _service.BeginRunAsync(task.Id, DateTime.UtcNow, CancellationToken.None);

        await _service.CompleteRunAsync(run.Id, true, "All done.", null, "sess-1", 500, null, null, null, CancellationToken.None);

        var history = await _service.GetRunHistoryAsync(1, task.Id, 10, CancellationToken.None);
        var saved = history.Single(r => r.Id == run.Id);

        Assert.Equal("success", saved.Status);
        Assert.Equal("All done.", saved.ResponseText);
        Assert.Equal("sess-1", saved.SessionId);
    }

    [Fact]
    public async Task CompleteRunAsync_TruncatesLongResponse()
    {
        var task = await _service.CreateAsync(1, DailyReq("Truncate Job"), CancellationToken.None);
        var run = await _service.BeginRunAsync(task.Id, DateTime.UtcNow, CancellationToken.None);

        var longText = new string('x', 200);   // > MaxResponseStorageChars (100)
        await _service.CompleteRunAsync(run.Id, true, longText, null, null, 1, null, null, null, CancellationToken.None);

        var history = await _service.GetRunHistoryAsync(1, task.Id, 10, CancellationToken.None);
        Assert.Equal(100, history.Single(r => r.Id == run.Id).ResponseText!.Length);
    }

    [Fact]
    public async Task ActivateOldestPendingRunAsync_PromotesPendingToRunning()
    {
        var task = await _service.CreateAsync(1, DailyReq("Queue Test"), CancellationToken.None);
        var run1 = await _service.BeginRunAsync(task.Id, DateTime.UtcNow, CancellationToken.None);
        var run2 = await _service.BeginRunAsync(task.Id, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal("running", run1.Status);
        Assert.Equal("pending", run2.Status);

        // Complete run1 — then activate oldest pending
        await _service.CompleteRunAsync(run1.Id, true, null, null, null, 100, null, null, null, CancellationToken.None);
        var promoted = await _service.ActivateOldestPendingRunAsync(task.Id, CancellationToken.None);

        Assert.NotNull(promoted);
        Assert.Equal(run2.Id, promoted!.Id);
        Assert.Equal("running", promoted.Status);
        Assert.NotNull(promoted.StartedAtUtc);
    }

    [Fact]
    public async Task GetDueTasksAsync_ReturnsOnlyDueTasks()
    {
        var pastRun = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var futureRun = DateTime.UtcNow.AddHours(1);

        // Insert directly to control NextRunUtc precisely
        using var db = new DivaDbContext(_dbOptions);
        db.ScheduledTasks.Add(new ScheduledTaskEntity
        {
            Id = "due",
            TenantId = 1,
            AgentId = "a",
            Name = "Due Task",
            ScheduleType = "daily",
            IsEnabled = true,
            PromptText = "x",
            NextRunUtc = pastRun
        });
        db.ScheduledTasks.Add(new ScheduledTaskEntity
        {
            Id = "notdue",
            TenantId = 1,
            AgentId = "a",
            Name = "Future Task",
            ScheduleType = "daily",
            IsEnabled = true,
            PromptText = "x",
            NextRunUtc = futureRun
        });
        await db.SaveChangesAsync();

        var due = await _service.GetDueTasksAsync(DateTime.UtcNow, CancellationToken.None);
        Assert.Single(due);
        Assert.Equal("due", due[0].Id);
    }

    // ── RecoverStuckRunsAsync ─────────────────────────────────────────────────

    /// <summary>
    /// A "running" run whose StartedAtUtc is before the cutoff must be marked failed.
    /// </summary>
    [Fact]
    public async Task RecoverStuckRunsAsync_MarksOldRunningRunAsFailed()
    {
        var task = await _service.CreateAsync(1, DailyReq("Stuck Job"), CancellationToken.None);

        // Insert a "running" run that started 2 hours ago
        using (var db = new DivaDbContext(_dbOptions))
        {
            db.ScheduledTaskRuns.Add(new ScheduledTaskRunEntity
            {
                Id = "stuck-1",
                TenantId = 1,
                ScheduledTaskId = task.Id,
                Status = "running",
                ScheduledForUtc = DateTime.UtcNow.AddHours(-2),
                StartedAtUtc = DateTime.UtcNow.AddHours(-2)
            });
            await db.SaveChangesAsync();
        }

        // Cutoff is 1 hour ago — the run started 2 hours ago, so it qualifies
        var recovered = await _service.RecoverStuckRunsAsync(DateTime.UtcNow.AddHours(-1), CancellationToken.None);

        Assert.Equal(1, recovered);

        var history = await _service.GetRunHistoryAsync(1, task.Id, 10, CancellationToken.None);
        var run = history.Single(r => r.Id == "stuck-1");
        Assert.Equal("failed", run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.NotNull(run.ErrorMessage);
    }

    /// <summary>
    /// A "running" run whose StartedAtUtc is after the cutoff must NOT be touched.
    /// </summary>
    [Fact]
    public async Task RecoverStuckRunsAsync_DoesNotAffectRecentRun()
    {
        var task = await _service.CreateAsync(1, DailyReq("Recent Job"), CancellationToken.None);

        using (var db = new DivaDbContext(_dbOptions))
        {
            db.ScheduledTaskRuns.Add(new ScheduledTaskRunEntity
            {
                Id = "recent-1",
                TenantId = 1,
                ScheduledTaskId = task.Id,
                Status = "running",
                ScheduledForUtc = DateTime.UtcNow.AddMinutes(-5),
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
        }

        // Cutoff is 30 minutes ago — run started 5 minutes ago, so it should be untouched
        var recovered = await _service.RecoverStuckRunsAsync(DateTime.UtcNow.AddMinutes(-30), CancellationToken.None);

        Assert.Equal(0, recovered);

        var history = await _service.GetRunHistoryAsync(1, task.Id, 10, CancellationToken.None);
        Assert.Equal("running", history.Single(r => r.Id == "recent-1").Status);
    }

    /// <summary>
    /// A "running" run with null StartedAtUtc (never properly started) is always recovered
    /// regardless of the cutoff — it cannot be younger than any cutoff.
    /// </summary>
    [Fact]
    public async Task RecoverStuckRunsAsync_NullStartedAtUtc_AlwaysRecovered()
    {
        var task = await _service.CreateAsync(1, DailyReq("Null Start Job"), CancellationToken.None);

        using (var db = new DivaDbContext(_dbOptions))
        {
            db.ScheduledTaskRuns.Add(new ScheduledTaskRunEntity
            {
                Id = "null-start-1",
                TenantId = 1,
                ScheduledTaskId = task.Id,
                Status = "running",
                ScheduledForUtc = DateTime.UtcNow.AddHours(-1),
                StartedAtUtc = null  // never got a start time
            });
            await db.SaveChangesAsync();
        }

        // Even a very recent cutoff (1 second ago) should catch a null StartedAtUtc
        var recovered = await _service.RecoverStuckRunsAsync(DateTime.UtcNow.AddSeconds(-1), CancellationToken.None);

        Assert.Equal(1, recovered);

        var history = await _service.GetRunHistoryAsync(1, task.Id, 10, CancellationToken.None);
        Assert.Equal("failed", history.Single(r => r.Id == "null-start-1").Status);
    }

    /// <summary>
    /// RecoverStuckRunsAsync returns the count of rows that were recovered.
    /// </summary>
    [Fact]
    public async Task RecoverStuckRunsAsync_ReturnsCountOfRecoveredRuns()
    {
        var task = await _service.CreateAsync(1, DailyReq("Count Job"), CancellationToken.None);

        using (var db = new DivaDbContext(_dbOptions))
        {
            for (int i = 1; i <= 3; i++)
            {
                db.ScheduledTaskRuns.Add(new ScheduledTaskRunEntity
                {
                    Id = $"stuck-count-{i}",
                    TenantId = 1,
                    ScheduledTaskId = task.Id,
                    Status = "running",
                    ScheduledForUtc = DateTime.UtcNow.AddHours(-2),
                    StartedAtUtc = DateTime.UtcNow.AddHours(-2)
                });
            }
            // One recent run — must NOT be counted
            db.ScheduledTaskRuns.Add(new ScheduledTaskRunEntity
            {
                Id = "recent-ok",
                TenantId = 1,
                ScheduledTaskId = task.Id,
                Status = "running",
                ScheduledForUtc = DateTime.UtcNow.AddMinutes(-1),
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
        }

        var recovered = await _service.RecoverStuckRunsAsync(DateTime.UtcNow.AddHours(-1), CancellationToken.None);

        Assert.Equal(3, recovered);
    }

    // ── ShouldNotify — 3-state outcome tests ─────────────────────────────────

    [Theory]
    [InlineData("always", "success", true)]
    [InlineData("always", "failure", true)]
    [InlineData("always", "skipped", true)]
    [InlineData("success", "success", true)]
    [InlineData("success", "failure", false)]
    [InlineData("success", "skipped", false)]
    [InlineData("failure", "success", false)]
    [InlineData("failure", "failure", true)]
    [InlineData("failure", "skipped", true)]   // skipped is as actionable as a failure
    [InlineData("never", "success", false)]
    [InlineData("never", "failure", false)]
    [InlineData(null, "success", false)]
    public void ShouldNotify_ReturnsExpected(string? notifyOn, string outcome, bool expected)
        => Assert.Equal(expected, SchedulerHostedService.ShouldNotify(notifyOn, outcome));

    // ── BuildPrompt tests (instance method) ──────────────────────────────────

    private SchedulerHostedService CreateHostedService()
    {
        var factory = new TestDbFactory(_dbOptions);
        return new SchedulerHostedService(
            _service,
            factory,
            null!,  // runner not needed for prompt building
            Options.Create(new TaskSchedulerOptions()),
            Options.Create(new AppBrandingOptions()),
            NullLogger<SchedulerHostedService>.Instance);
    }

    [Fact]
    public void BuildPrompt_FixedPrompt_ReturnAsIs()
    {
        var sut = CreateHostedService();
        var task = new ScheduledTaskEntity { PayloadType = "prompt", PromptText = "Hello world" };
        Assert.Equal("Hello world", sut.BuildPrompt(task, "run-1"));
    }

    [Fact]
    public void BuildPrompt_Template_SubstitutesVariables()
    {
        var task = new ScheduledTaskEntity
        {
            PayloadType = "template",
            PromptText = "Hello {{name}}, today is {{day}}.",
            ParametersJson = """{"name":"Alice","day":"Monday"}"""
        };
        var result = CreateHostedService().BuildPrompt(task, "run-1");
        Assert.Equal("Hello Alice, today is Monday.", result);
    }

    [Fact]
    public void BuildPrompt_Template_MissingParam_LeavesPlaceholder()
    {
        var task = new ScheduledTaskEntity
        {
            PayloadType = "template",
            PromptText = "Hello {{name}}.",
            ParametersJson = """{"other":"value"}"""
        };
        var result = CreateHostedService().BuildPrompt(task, "run-1");
        Assert.Equal("Hello {{name}}.", result);
    }

    [Fact]
    public void BuildPrompt_Template_InvalidJson_ReturnRawPrompt()
    {
        var task = new ScheduledTaskEntity
        {
            PayloadType = "template",
            PromptText = "Hello {{name}}.",
            ParametersJson = "not-json"
        };
        var result = CreateHostedService().BuildPrompt(task, "run-1");
        Assert.Equal("Hello {{name}}.", result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ScheduledTaskEntity OneTimeTask(DateTime at) => new()
    {
        ScheduleType = "once",
        ScheduledAtUtc = at,
        TimeZoneId = "UTC"
    };

    private static CreateScheduledTaskRequest DailyReq(string name) =>
        new(AgentId: "a1", Name: name, Description: null,
            ScheduleType: "daily", ScheduledAtUtc: null, RunAtTime: "09:00",
            DayOfWeek: null, TimeZoneId: "UTC",
            PayloadType: "prompt", PromptText: "Run it",
            ParametersJson: null, IsEnabled: true);

    /// <summary>In-memory test database factory (same pattern as ContextWindowTests).</summary>
    private sealed class TestDbFactory : IDatabaseProviderFactory
    {
        private readonly DbContextOptions<DivaDbContext> _opts;
        public TestDbFactory(DbContextOptions<DivaDbContext> opts) => _opts = opts;
        public DivaDbContext CreateDbContext(Diva.Core.Models.TenantContext? tenant = null)
            => new(_opts, tenant?.TenantId ?? 0);
        public Task ApplyMigrationsAsync() => Task.CompletedTask;
    }

    // ── ShouldNotify — 3-state outcome tests ─────────────────────────────────

    [Theory]
    [InlineData("always", "success", true)]
    [InlineData("always", "failure", true)]
    [InlineData("always", "skipped", true)]
    [InlineData("success", "success", true)]
    [InlineData("success", "failure", false)]
    [InlineData("success", "skipped", false)]
    [InlineData("failure", "failure", true)]
    [InlineData("failure", "skipped", true)]   // skipped fires on "failure" policy
    [InlineData("failure", "success", false)]
    [InlineData("never", "success", false)]
    [InlineData("never", "failure", false)]
    [InlineData(null, "success", false)]
    public void ShouldNotify_ReturnsExpectedResult(string? notifyOn, string outcome, bool expected)
    {
        Assert.Equal(expected, SchedulerHostedService.ShouldNotify(notifyOn, outcome));
    }

    [Fact]
    public async Task CompleteRunAsync_SetsLastRunStatusOnParentTask()
    {
        // Arrange
        var task = await _service.CreateAsync(1, DailyReq("statusTask"), CancellationToken.None);
        var run = await _service.BeginRunAsync(task.Id, DateTime.UtcNow, CancellationToken.None);
        if (run.Status != "running")
            run = (await _service.ActivateOldestPendingRunAsync(task.Id, CancellationToken.None))!;

        // Act
        await _service.CompleteRunAsync(run.Id, success: true,
            responseText: "done", errorMessage: null,
            sessionId: null, durationMs: 100,
            inputTokens: null, outputTokens: null, iterationCount: null,
            CancellationToken.None);

        // Assert
        var updated = await _service.GetAsync(1, task.Id, CancellationToken.None);
        Assert.Equal("success", updated!.LastRunStatus);
    }

    [Fact]
    public async Task BeginRunAsync_WhenQueueFull_SetsLastRunStatusSkipped()
    {
        // Arrange — fill the queue to MaxQueuedRunsPerTask (3)
        var task = await _service.CreateAsync(1, DailyReq("skipTask"), CancellationToken.None);
        // Create 3 runs to saturate queue
        for (int i = 0; i < 3; i++)
            await _service.TriggerNowAsync(1, task.Id, CancellationToken.None);

        // Act — one more begin should produce "skipped"
        var skippedRun = await _service.BeginRunAsync(task.Id, DateTime.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal("skipped", skippedRun.Status);
        var updated = await _service.GetAsync(1, task.Id, CancellationToken.None);
        Assert.Equal("skipped", updated!.LastRunStatus);
    }
}
