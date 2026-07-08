namespace Diva.Infrastructure.Scheduler;

/// <summary>
/// Allows a manual "Run now" (Trigger Now) request to be dispatched immediately on the
/// local host instance, independent of the automatic polling loop. This is what makes
/// manual runs execute on whichever instance received the request even when that instance
/// has <c>TaskScheduler:AutoPollEnabled = false</c> (non-leader).
/// </summary>
public interface ISchedulerManualDispatch
{
    /// <summary>
    /// Immediately activates and dispatches the oldest pending run for the given task on this
    /// instance. Fire-and-forget: returns as soon as the dispatch has been scheduled. No-op when
    /// the scheduler master switch (<c>TaskScheduler:IsEnabled</c>) is false or the task is already
    /// running locally.
    /// </summary>
    void RequestManualDispatch(string taskId);
}
