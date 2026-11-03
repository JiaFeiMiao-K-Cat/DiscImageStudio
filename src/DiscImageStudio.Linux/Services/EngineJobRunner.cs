using System.Text;
using System.Text.RegularExpressions;
using DiscImageStudio.Core;

namespace DiscImageStudio.Linux.Services;

/// <summary>Runs an engine command in-process through the same module catalog as the CLI.
/// The DVD engine reports through the console, so console output is captured line by line
/// for the duration of the job and fed to the log; "block N/M" lines double as progress.</summary>
public static partial class EngineJobRunner
{
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    public static async Task<DiscJobResult> RunAsync(
        IReadOnlyList<string> arguments,
        Action<string> log,
        Action<DiscJobProgress> progress,
        CancellationToken cancellationToken)
    {
        if (arguments.Count == 0)
        {
            throw new ArgumentException("Missing command.", nameof(arguments));
        }

        await ConsoleGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                TextWriter originalOut = Console.Out;
                TextWriter originalError = Console.Error;
                using LineWriter output = new(line =>
                {
                    log(line);
                    Match block = BlockProgress().Match(line);
                    if (block.Success
                        && long.TryParse(block.Groups[1].Value, out long done)
                        && long.TryParse(block.Groups[2].Value, out long total)
                        && total > 0)
                    {
                        progress(new DiscJobProgress("dvd-data", "solve", line, (double)done / total, done, total));
                    }
                });
                using LineWriter error = new(line => log("error: " + line));
                Console.SetOut(output);
                Console.SetError(error);
                try
                {
                    DiscJobRequest request = new(arguments[0], arguments.Skip(1));
                    return ConsoleModuleRunner.Catalog
                        .ExecuteAsync(request, new InlineProgress(progress), cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                }
                finally
                {
                    output.Flush();
                    error.Flush();
                    Console.SetOut(originalOut);
                    Console.SetError(originalError);
                }
            }, CancellationToken.None);
        }
        finally
        {
            ConsoleGate.Release();
        }
    }

    public static bool SupportsCancellation(string command)
        => ConsoleModuleRunner.Catalog.TryResolve(command, out IOpticalDiscModule? module)
           && module?.Descriptor.SupportsCancellation == true;

    /// <summary>Shell-quoted command line for the log and for running the same job in a
    /// terminal with the CLI.</summary>
    public static string ToShellCommand(string executable, IEnumerable<string> arguments)
        => string.Join(' ', new[] { executable }.Concat(arguments).Select(ShellQuote));

    public static string ShellQuote(string value)
        => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || "-_./=:+,@%".Contains(c))
            ? value
            : "'" + value.Replace("'", "'\\''") + "'";

    [GeneratedRegex(@"^block (\d+)/(\d+)")]
    private static partial Regex BlockProgress();

    private sealed class InlineProgress(Action<DiscJobProgress> report) : IProgress<DiscJobProgress>
    {
        public void Report(DiscJobProgress value) => report(value);
    }

    private sealed class LineWriter(Action<string> onLine) : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private readonly object _gate = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            string? line = null;
            lock (_gate)
            {
                if (value == '\n')
                {
                    line = _buffer.ToString().TrimEnd('\r');
                    _buffer.Clear();
                }
                else
                {
                    _buffer.Append(value);
                }
            }

            if (line is not null)
            {
                onLine(line);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (char c in value)
            {
                Write(c);
            }
        }

        public override void Flush()
        {
            string? line = null;
            lock (_gate)
            {
                if (_buffer.Length > 0)
                {
                    line = _buffer.ToString();
                    _buffer.Clear();
                }
            }

            if (line is not null)
            {
                onLine(line);
            }
        }
    }
}
