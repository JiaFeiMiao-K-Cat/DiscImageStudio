using System.Windows;
using System.Windows.Threading;

/// <summary>All control tests share the one Application allowed in this process.</summary>
internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> HostDispatcher = new(CreateDispatcher);

    internal static void Run(Action action)
    {
        Dispatcher dispatcher = HostDispatcher.Value;
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.InvokeAsync(action).Task.GetAwaiter().GetResult();
    }

    private static Dispatcher CreateDispatcher()
    {
        TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (string name in new[] { "Tokens", "Controls" })
                {
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/DiscImageStudio;component/Themes/{name}.xaml"),
                    });
                }

                ready.SetResult(application.Dispatcher);
            }
            catch (Exception exception)
            {
                ready.SetException(exception);
                return;
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
