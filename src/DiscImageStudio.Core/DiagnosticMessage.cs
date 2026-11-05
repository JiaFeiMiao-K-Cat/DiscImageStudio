using System.Globalization;
using System.Runtime.CompilerServices;

namespace DiscImageStudio.Core;

/// <summary>Preserves an application-owned diagnostic template until a UI can
/// translate it. The exception type, parameter name and original message remain
/// available to callers and diagnostic logs.</summary>
public static class DiagnosticMessage
{
    private static readonly ConditionalWeakTable<Exception, FormattableString> Templates = new();

    public static ArgumentException Argument(FormattableString message, string? parameterName = null)
        => Attach(new ArgumentException(Render(message), parameterName), message);

    public static InvalidDataException InvalidData(FormattableString message, Exception? innerException = null)
        => Attach(new InvalidDataException(Render(message), innerException), message);

    public static FormattableString? Template(Exception exception)
        => Templates.TryGetValue(exception, out FormattableString? template) ? template : null;

    // Only marked templates are translated recursively. Names, paths and other
    // ordinary string arguments are user data and must remain untouched.
    public static ExceptionDetail Detail(Exception exception) => new(exception);

    public sealed record ExceptionDetail(Exception Exception)
    {
        public override string ToString() => Exception.Message;
    }

    private static T Attach<T>(T exception, FormattableString message) where T : Exception
    {
        Templates.Add(exception, message);
        return exception;
    }

    private static string Render(FormattableString message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.ToString(CultureInfo.CurrentCulture);
    }
}
