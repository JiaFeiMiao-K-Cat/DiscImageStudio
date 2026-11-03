using System.Text;

namespace DiscImageStudio.Desktop;

/// <summary>Capture encoder diagnostics in the dock while preserving command-line output.</summary>
internal sealed class JobLogWriter(TextWriter original, Action<string> report) : TextWriter
{
    private readonly StringBuilder _line = new();
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value)
    {
        original.Write(value);
        lock (_line)
        {
            if (value == '\n') { if (_line.Length > 0) report(_line.ToString()); _line.Clear(); }
            else if (value != '\r') _line.Append(value);
        }
    }
    public override void Flush()
    {
        original.Flush();
        lock (_line) { if (_line.Length > 0) { report(_line.ToString()); _line.Clear(); } }
    }
}
