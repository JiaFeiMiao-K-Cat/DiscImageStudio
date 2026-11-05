using System.Collections;
using System.Text;

namespace DiscImageStudio.Services;

/// <summary>Task diagnostics retain exception evidence separately from concise UI status text.</summary>
internal static class JobDiagnostics
{
    internal static string FormatLog(string context, Exception exception, bool warning = false)
        => $"[{DateTime.Now:HH:mm:ss}] {context} [{(warning ? "WARNING" : "ERROR")}]\n"
            + FormatException(exception) + "\n";

    internal static string FormatException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        StringBuilder text = new();
        try { text.AppendLine(exception.ToString()); }
        catch { text.AppendLine(exception.GetType().FullName); }
        HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
        Append(exception, "Exception");
        return text.ToString().TrimEnd();

        void Append(Exception current, string path)
        {
            if (!visited.Add(current)) return;
            text.AppendLine($"[{path}] {current.GetType().FullName}; HRESULT=0x{current.HResult:X8}; Source={current.Source ?? "(null)"}");
            try
            {
                foreach (DictionaryEntry entry in current.Data)
                    text.AppendLine($"  {SafeText(entry.Key)}: {SafeText(entry.Value)}");
            }
            catch (Exception dataFailure)
            {
                text.AppendLine($"  Data could not be read: {dataFailure.GetType().FullName}");
            }
            if (current is AggregateException aggregate)
            {
                for (int i = 0; i < aggregate.InnerExceptions.Count; i++)
                    Append(aggregate.InnerExceptions[i], $"{path}.InnerExceptions[{i}]");
            }
            else if (current.InnerException is { } inner)
                Append(inner, path + ".InnerException");
        }
    }

    /// <summary>Only for best-effort cleanup; a cleanup failure must not replace the original
    /// task exception (including cancellation) or invalidate an already published result.</summary>
    internal static void TryCleanup(Action cleanup, Action<string> appendLog, string context)
    {
        try { cleanup(); }
        catch (Exception exception)
        {
            try { appendLog(FormatLog(context, exception, warning: true)); }
            catch { /* A broken diagnostic observer must not replace the task outcome. */ }
        }
    }

    private static string SafeText(object? value)
    {
        try { return value?.ToString() ?? "(null)"; }
        catch { return $"<{value!.GetType().FullName}: ToString failed>"; }
    }
}
