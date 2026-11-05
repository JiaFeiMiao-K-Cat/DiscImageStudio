using System.Collections;
using System.Globalization;
using System.Text;

namespace DiscImageStudio.Burning;

/// <summary>Formats cached burn context without making calls to a failing recorder.</summary>
public static class OpticalBurnDiagnostics
{
    internal static void CaptureFailure(Exception exception, string operation, string? progress = null)
    {
        // Capture at the failing call, before recovery flushes or releases can
        // change the live operation/progress. Keep the original exception object.
        try
        {
            if (!exception.Data.Contains("Burn.Operation"))
            {
                exception.Data["Burn.Operation"] = operation;
                exception.Data["Burn.FailedAt"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            }
            if (progress is not null && !exception.Data.Contains("Burn.ProgressAtFailure"))
                exception.Data["Burn.ProgressAtFailure"] = progress;
        }
        catch { /* Optional diagnostics must preserve the original failure. */ }
    }

    /// <summary>Includes the exception tree, stack traces, per-exception HRESULTs and attached context.</summary>
    public static string FormatException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        StringBuilder text = new();
        text.AppendLine(exception.ToString());
        HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
        Append(exception, "Exception");
        return text.ToString().TrimEnd();

        void Append(Exception current, string path)
        {
            if (!visited.Add(current)) return;
            text.AppendLine($"[{path}] {current.GetType().FullName}; HRESULT=0x{current.HResult:X8}; Source={current.Source ?? "(null)"}");
            foreach (DictionaryEntry entry in current.Data)
                text.AppendLine($"  {entry.Key}: {entry.Value}");
            if (current is AggregateException aggregate)
            {
                for (int i = 0; i < aggregate.InnerExceptions.Count; i++)
                    Append(aggregate.InnerExceptions[i], $"{path}.InnerExceptions[{i}]");
            }
            else if (current.InnerException is { } inner)
                Append(inner, path + ".InnerException");
        }
    }
}
