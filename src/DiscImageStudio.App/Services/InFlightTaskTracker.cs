namespace DiscImageStudio.Services;

/// <summary>Tracks fire-and-forget asynchronous work so window shutdown or test teardown can
/// wait for active background file I/O to unwind before deleting cache directories.</summary>
internal sealed class InFlightTaskTracker
{
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _tasks = [];

    internal T Track<T>(T task) where T : Task
    {
        if (task.IsCompleted)
        {
            return task;
        }

        lock (_gate)
        {
            _tasks.Add(task);
        }

        _ = task.ContinueWith(
            static (completed, state) => ((InFlightTaskTracker)state!).Remove(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    internal async Task WaitForAllAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (_gate)
            {
                if (_tasks.Count == 0)
                {
                    return;
                }

                snapshot = [.. _tasks];
            }

            try
            {
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }
            catch
            {
                // Teardown callers only need background I/O and finally blocks to complete.
            }
        }
    }

    private void Remove(Task task)
    {
        lock (_gate)
        {
            _tasks.Remove(task);
        }
    }
}
