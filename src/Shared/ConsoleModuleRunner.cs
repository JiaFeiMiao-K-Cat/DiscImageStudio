using DiscImageStudio.Core;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;

namespace DiscImageStudio;

internal static class ConsoleModuleRunner
{
    internal static DiscModuleCatalog Catalog { get; } = new(
        [new CdDiscModule(), new DvdDiscModule(), new BluRayDiscModule(BluRayImageLoader.Load)]);

    internal static int Run(string[] arguments)
    {
        DiscJobRequest request = new(arguments[0], arguments.Skip(1));
        IOpticalDiscModule module = Catalog.Resolve(request.Command);
        bool streamsToStandardOutput = IsStandardOutputStream(request.Command, request.Arguments);
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            if (!module.Descriptor.SupportsCancellation) return;
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            DiscJobResult result = module.ExecuteAsync(
                    request,
                    new ConsoleProgress(streamsToStandardOutput),
                    cancellation.Token)
                .GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(result.Summary))
            {
                // A binary payload may occupy stdout; keep every log line on stderr.
                if (streamsToStandardOutput) Console.Error.WriteLine(result.Summary);
                else Console.WriteLine(result.Summary);
            }

            return result.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// True when the command writes its payload to stdout instead of a file (<c>--output -</c>).
    /// Mirrors the module-side check so progress and summaries never corrupt the byte stream.
    /// </summary>
    internal static bool IsStandardOutputStream(string command, IReadOnlyList<string> arguments)
    {
        string optionName;
        if (command.Equals("cd-generate", StringComparison.OrdinalIgnoreCase)
            || command.Equals("bd-generate", StringComparison.OrdinalIgnoreCase))
        {
            optionName = "--output";
        }
        else if (command.Equals("solve", StringComparison.OrdinalIgnoreCase))
        {
            optionName = "--iso-output";
        }
        else
        {
            return false;
        }

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument.Equals(optionName, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < arguments.Count && arguments[index + 1] == "-";
            }

            if (argument.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
            {
                return argument[(optionName.Length + 1)..] == "-";
            }
        }

        return false;
    }

    private sealed class ConsoleProgress : IProgress<DiscJobProgress>
    {
        private readonly bool _standardError;
        private double _lastFraction = -1;
        private long _lastTick;

        internal ConsoleProgress(bool standardError) => _standardError = standardError;

        public void Report(DiscJobProgress value)
        {
            if (string.IsNullOrWhiteSpace(value.Message)) return;
            if (_standardError)
            {
                // BD/DVD streaming emits one progress message per block; writing
                // every line blocks the pipe and the GUI. One line per percent or
                // per second is enough.
                long now = Environment.TickCount64;
                double fraction = value.Fraction ?? 0;
                if (fraction < _lastFraction + 0.01 && now - _lastTick < 1000) return;
                _lastFraction = fraction;
                _lastTick = now;
                Console.Error.WriteLine(value.Message);
                Console.Error.Flush();
            }
            else
            {
                Console.WriteLine(value.Message);
            }
        }
    }
}
