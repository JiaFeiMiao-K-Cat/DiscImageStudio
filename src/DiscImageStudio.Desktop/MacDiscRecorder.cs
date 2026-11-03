using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DiscImageStudio.Desktop;

public sealed record MacRecorder(string Id, string Name);
public sealed record MacMedia(bool Blank, string MediaClass, long FreeSectors, string Description);

/// <summary>macOS DiscRecording via the system drutil. Never erases, appends or changes an image's filesystem.</summary>
public static class MacDiscRecorder
{
    public static async Task<string> Run(string executable, IEnumerable<string> arguments, Action<string>? log = null, CancellationToken token = default)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.Start();
        var output = new StringBuilder();
        async Task Read(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line) { lock (output) output.AppendLine(line); log?.Invoke(line); }
        }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        using var registration = token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} ({process.ExitCode}): {output}");
        return output.ToString();
    }
    public static XDocument ReadSystemXml(string xml)
    {
        // drutil list emits an anonymous <!DOCTYPE [ ... ]>, which is not a valid XML doctype.
        // Its document is UTF-8 and self-contained; ignore declarations and never resolve entities.
        int declaration = xml.IndexOf("<!DOCTYPE", StringComparison.Ordinal);
        if (declaration >= 0)
        {
            int end = xml.IndexOf("]>", declaration, StringComparison.Ordinal);
            if (end < 0) throw new InvalidDataException("无法读取光驱信息。");
            xml = xml.Remove(declaration, end + 2 - declaration);
        }
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }
    public static async Task<IReadOnlyList<MacRecorder>> ListAsync()
    {
        if (!OperatingSystem.IsMacOS()) return [];
        var doc = ReadSystemXml(await Run("/usr/bin/drutil", ["list", "-xml"]));
        return doc.Descendants("device").Select(d => new MacRecorder((string?)d.Attribute("index") ?? "",
            $"{(string?)d.Element("vendor")?.Attribute("name")} {(string?)d.Element("product")?.Attribute("name")}".Trim()))
            .Where(d => int.TryParse(d.Id, out int id) && id > 0).ToArray();
    }
    public static MacMedia ParseMedia(string xml, string deviceId)
    {
        var doc = ReadSystemXml(xml);
        var status = doc.Descendants("statusfordevice").SingleOrDefault(s => (string?)s.Element("device")?.Attribute("index") == deviceId);
        var media = status?.Element("deviceStatus")?.Element("mediaInfo");
        if (media is null) throw new InvalidOperationException("未检测到盘片，请插入空白盘后重试。");
        string msf = (string?)media.Element("freeSpace")?.Attribute("msf") ?? "";
        string[] parts = msf.Split(':');
        long free = parts.Length == 3 && parts.All(p => long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            ? long.Parse(parts[0], CultureInfo.InvariantCulture) * 60 * 75 + long.Parse(parts[1], CultureInfo.InvariantCulture) * 75 + long.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
        return new(media.Element("blank") is not null, (string?)media.Element("mediaClass")?.Attribute("value") ?? "Unknown", free,
            (string?)media.Element("mediaType")?.Attribute("value") ?? "Unknown");
    }
    public static async Task<MacMedia> InspectAsync(string id)
        => ParseMedia(await Run("/usr/bin/drutil", ["-drive", id, "status", "-xml"]), id);

    public static void ValidateMedia(MacMedia media, bool audio, long sectors)
    {
        if (!media.Blank) throw new InvalidOperationException("只允许写入空白盘片；不会擦除或追加已有内容。");
        if (audio && media.MediaClass != "CD") throw new InvalidOperationException("CD 音轨必须使用空白 CD-R。");
        if (media.MediaClass is not ("CD" or "DVD")) throw new InvalidOperationException("此系统刻录入口仅验证 CD/DVD。蓝光请使用支持 BD-R 的外部刻录软件写入 ISO。");
        if (sectors <= 0 || media.FreeSectors < sectors) throw new InvalidOperationException("盘片可用容量不足或无法确认容量。请检查扇区数，不会自动缩短图案。");
    }

    /// <summary>Lossless PCM byte-order conversion. AIFF avoids QuickTime compressed-audio transcoding.</summary>
    public static long WriteAudioAiff(string wavePath, string aiffPath)
    {
        using var input = File.OpenRead(wavePath); using var reader = new BinaryReader(input, Encoding.ASCII, leaveOpen: true);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("只支持 PCM WAV。");
        _ = reader.ReadUInt32(); if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("无效 WAV。");
        bool validFormat = false; long dataOffset = 0; uint dataLength = 0;
        while (input.Position + 8 <= input.Length)
        {
            string chunk = new(reader.ReadChars(4)); uint length = reader.ReadUInt32(); long start = input.Position;
            if (start + length > input.Length) throw new InvalidDataException("WAV 数据被截断。");
            if (chunk == "fmt " && length >= 16)
            {
                ushort format = reader.ReadUInt16(), channels = reader.ReadUInt16(); uint rate = reader.ReadUInt32();
                uint byteRate = reader.ReadUInt32(); ushort alignment = reader.ReadUInt16(), bits = reader.ReadUInt16();
                validFormat = format == 1 && channels == 2 && rate == 44100 && bits == 16 && alignment == 4 && byteRate == 176400;
            }
            if (chunk == "data") { dataOffset = start; dataLength = length; }
            input.Position = start + length + (length & 1);
        }
        if (!validFormat || dataOffset == 0 || dataLength == 0 || dataLength % 2352 != 0)
            throw new InvalidDataException("需要扇区对齐的 44.1 kHz、16 位、双声道 PCM WAV。");
        using var output = File.Create(aiffPath);
        void Tag(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        void U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); output.Write(b); }
        void U16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); output.Write(b); }
        Tag("FORM"); U32(checked(dataLength + 46)); Tag("AIFF"); Tag("COMM"); U32(18); U16(2); U32(dataLength / 4); U16(16);
        output.Write(new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 }); // Extended-precision 44100 Hz.
        Tag("SSND"); U32(checked(dataLength + 8)); U32(0); U32(0);
        input.Position = dataOffset; byte[] buffer = new byte[2352 * 32]; uint remaining = dataLength;
        while (remaining > 0)
        {
            int count = (int)Math.Min((uint)buffer.Length, remaining); input.ReadExactly(buffer.AsSpan(0, count));
            for (int i = 0; i < count; i += 2) (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
            output.Write(buffer, 0, count); remaining -= (uint)count;
        }
        return dataLength / 2352;
    }
}
