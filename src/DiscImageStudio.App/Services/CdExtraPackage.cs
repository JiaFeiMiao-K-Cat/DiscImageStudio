using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using NAudio.Wave;

namespace DiscImageStudio.Services;

internal sealed record CdExtraProgress(string Message, double? Fraction = null);
internal sealed record CdExtraPackageTrack(string File, string Title, string Sha256, long Sectors);

/// <summary>CD-TEXT of a burn package: album fields and one entry per track, in track order.
/// Absent in packages without CD-TEXT (and in all packages made before CD-TEXT support), which then
/// burn exactly as before.</summary>
internal sealed record CdExtraPackageCdText(string? AlbumTitle, string? AlbumPerformer,
    IReadOnlyList<CdTextTrack> Tracks)
{
    internal CdTextAlbum ToAlbum() => new CdTextAlbum(AlbumTitle, AlbumPerformer, Tracks).Normalized();
}
internal sealed record CdExtraPackage(int Version, CdDiscParameters Geometry, ToneProfile Tone,
    string ArtworkSha256, IReadOnlyList<CdExtraPackageTrack> Tracks,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    CdExtraPackageCdText? CdText = null)
{
    internal const string ManifestName = "package.cdextra.json";
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal long EstimatedStart => CdExtraLayout.Plan(Geometry, Tracks.Select(t => t.Sectors).ToArray()).Session2StartLba;

    internal static CdExtraPackage Create(string directory, string image, IReadOnlyList<string> music,
        CdDiscParameters geometry, ToneProfile tone, Action<string> progress, CancellationToken token,
        IProgress<CdExtraProgress>? status = null, CdExtraPackageCdText? cdText = null)
    {
        if (cdText is not null)
        {
            if (cdText.Tracks.Count != music.Count) throw new ArgumentException("CD-TEXT 曲目数与音乐数不一致。");
            _ = CdTextPacks.Encode(cdText.ToAlbum());
        }
        geometry.Validate(); tone.Validate();
        if (music.Count is < 1 or > 98) throw new ArgumentException("请选择 1–98 首音乐。");
        token.ThrowIfCancellationRequested();
        string staging = directory + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            CdExtraPackage created = WritePackage(staging, image, music, geometry, tone, progress, token, status, cdText);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, directory);
            return created;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static CdExtraPackage WritePackage(string directory, string image, IReadOnlyList<string> music,
        CdDiscParameters geometry, ToneProfile tone, Action<string> progress, CancellationToken token,
        IProgress<CdExtraProgress>? status, CdExtraPackageCdText? cdText)
    {
        File.Copy(image, Path.Combine(directory, "artwork.png"), overwrite: false);
        List<CdExtraPackageTrack> tracks = [];
        for (int i = 0; i < music.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            progress($"转换音乐 {i + 1}/{music.Count}：{Path.GetFileName(music[i])}");
            string name = $"track-{i + 1:D2}.wav";
            string path = Path.Combine(directory, name);
            long sectors = ConvertAudio(music[i], path, geometry.TotalBytes, token);
            tracks.Add(new(name, Path.GetFileName(music[i]), Hash(path), sectors));
            _ = CdExtraLayout.Plan(geometry, tracks.Select(t => t.Sectors).ToArray());
            status?.Report(new($"已转换音乐 {i + 1}/{music.Count}", (i + 1.0) / music.Count));
        }
        CdExtraPackage package = new(1, geometry, tone, Hash(Path.Combine(directory, "artwork.png")), tracks, cdText);
        File.WriteAllText(Path.Combine(directory, ManifestName), JsonSerializer.Serialize(package, Json));
        File.WriteAllText(Path.Combine(directory, "README.txt"),
            "CD Extra 刻录包\r\n在 DiscImageStudio 导出页打开 CD Extra，选择“打开刻录包”。\r\n"
            + "第一会话是 track-XX.wav 音乐，第二会话是图案数据。不要把图案另刻为 AUDIO 轨。\r\n"
            + "图案数据镜像在音乐刻完、读取实际第二会话起点后生成；不要手工拼接 WAV 与 ISO。\r\n"
            + "音乐越长，外圈可绘图范围越小。每轨不足 4 秒时补静音至 4 秒。\r\n"
            + "第二会话采用 Mode 1 数据轨，镜像按刻录机报告的实际起点生成。\r\n");
        return package;
    }

    internal static CdExtraPackage Load(string directory)
    {
        CdExtraPackage package = JsonSerializer.Deserialize<CdExtraPackage>(
            File.ReadAllText(Path.Combine(directory, ManifestName)), Json)
            ?? throw new InvalidDataException("刻录包为空。");
        if (package.Version != 1 || package.Geometry is null || package.Tracks is null || package.Tracks.Count is < 1 or > 98)
            throw new InvalidDataException("刻录包格式不受支持。");
        package.Geometry.Validate(); package.Tone.Validate();
        if (Hash(Path.Combine(directory, "artwork.png")) != package.ArtworkSha256)
            throw new InvalidDataException("图案文件已变化，请重新准备刻录包。");
        foreach (var track in package.Tracks)
        {
            if (track is null || string.IsNullOrWhiteSpace(track.File) || track.File != Path.GetFileName(track.File) || !track.File.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("刻录包曲目路径无效。");
            string path = Path.Combine(directory, track.File);
            ValidateWave(path, track.Sectors);
            if (Hash(path) != track.Sha256) throw new InvalidDataException("音乐文件已变化：" + track.File);
        }
        if (package.CdText is { } text)
        {
            if (text.Tracks is null || text.Tracks.Count != package.Tracks.Count)
                throw new InvalidDataException("刻录包的 CD-TEXT 曲目数与音乐数不一致。");
            try { _ = CdTextPacks.Encode(text.ToAlbum()); }
            catch (CdTextTooLongException) { throw new InvalidDataException("刻录包的 CD-TEXT 超出一张盘的容量。"); }
        }
        _ = package.EstimatedStart;
        return package;
    }

    internal CdExtraBurnRequest CreateBurnRequest(string directory, string deviceId, OpticalWriteSpeed speed,
        CancellationToken token, Action<string> log)
    {
        token.ThrowIfCancellationRequested();
        string session = Path.Combine(directory, "data-" + Guid.NewGuid().ToString("N"));
        string files = Path.Combine(session, "files");
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "README.TXT"),
            "DiscImageStudio artwork data session. Audio is in the first session.\r\n");
        string[] tracks = Tracks.Select(t => Path.Combine(directory, t.File)).ToArray();
        CdExtraPlan plan = CdExtraPlanner.Plan(Geometry, tracks, files, "CD_EXTRA", false, cancellationToken: token);
        CdExtraBurnRequest request = CdExtraPlanner.CreateBurnRequest(Path.Combine(directory, "artwork.png"), Geometry, plan, files,
            "CD_EXTRA", false, Tone, deviceId, speed, prepareImage: SaveMeasuredImage);
        CdTextAlbum? album = CdText?.ToAlbum();
        return album is null || album.IsEmpty ? request
            : request with { CdText = new CdTextLeadIn(CdTextPacks.Encode(album).Packs) };

        CdExtraDataContent SaveMeasuredImage(CdExtraLayout layout, CdDataLayoutPlan dataPlan, CdExtraDataContent data)
        {
            token.ThrowIfCancellationRequested();
            string iso = Path.Combine(session, "artwork-data.iso");
            string partial = iso + ".partial";
            log($"第二会话从 LBA {layout.Session2StartLba} 开始，生成 {data.Sectors} 扇区的图案镜像。");
            try
            {
                using (FileStream output = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    data.Produce(output, token);
                    if (output.Length != data.ContentLength) throw new IOException("图案镜像长度与布局不符。");
                    output.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(partial, iso);
            }
            finally { File.Delete(partial); }

            CdExtraImageMetadata metadata = CdExtraImageMetadata.Create(layout, dataPlan, Tone);
            CdExtraImageMetadata.Save(iso, metadata);
            File.WriteAllText(Path.Combine(session, "layout.json"), JsonSerializer.Serialize(new
            {
                format = metadata.Format, sessionStart = layout.Session2StartLba, drawingStart = dataPlan.DrawingStartLba,
                endExclusive = layout.DataEndLbaExclusive, geometry = Geometry,
            }, Json));
            log("已保存本次刻录的图案镜像：" + iso);
            return new CdExtraDataContent(data.Sectors, (output, cancellation) =>
            {
                using FileStream input = File.OpenRead(iso);
                byte[] buffer = new byte[128 * 1024];
                int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
            }) { SavedImagePath = iso };
        }
    }

    internal static long ConvertAudio(string input, string output, long maximumBytes, CancellationToken token)
    {
        using WaveStream source = Path.GetExtension(input).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(input) : new MediaFoundationReader(input);
        WaveFormat targetFormat = new(44100, 16, 2);
        bool exact = source.WaveFormat.Encoding == WaveFormatEncoding.Pcm
            && source.WaveFormat.SampleRate == 44100 && source.WaveFormat.BitsPerSample == 16
            && source.WaveFormat.Channels == 2;
        using MediaFoundationResampler? converter = exact ? null : new(source, targetFormat) { ResamplerQuality = 60 };
        IWaveProvider provider = converter is null ? source : converter;
        using FileStream target = new(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        target.Write(new byte[44]);
        byte[] buffer = new byte[2352 * 64];
        long count = 0;
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            count = checked(count + read);
            if (count > maximumBytes) throw new ArgumentException("音乐超出盘片容量。");
            target.Write(buffer, 0, read);
        }
        if (count == 0 || count % 4 != 0) throw new InvalidDataException("音乐解码后没有完整的立体声样本。");
        long padded = Math.Max(300 * 2352L, checked((count + 2351) / 2352 * 2352));
        if (padded > maximumBytes) throw new ArgumentException("音乐对齐后的长度超出盘片容量。");
        target.SetLength(44 + padded);
        target.Position = 0;
        Span<byte> header = stackalloc byte[44];
        header.Clear();
        "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)padded + 36));
        "WAVEfmt "u8.CopyTo(header[8..]); BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1); BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 44100); BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 176400);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 4); BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]); BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)padded));
        target.Write(header); target.Flush(true);
        return padded / 2352;
    }

    private static void ValidateWave(string path, long sectors)
    {
        if (sectors < 300 || sectors > 450000) throw new InvalidDataException("曲目长度无效。");
        using FileStream file = File.OpenRead(path);
        Span<byte> h = stackalloc byte[44]; file.ReadExactly(h);
        if (!h[..4].SequenceEqual("RIFF"u8) || !h.Slice(8, 8).SequenceEqual("WAVEfmt "u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(h[16..]) != 16
            || BinaryPrimitives.ReadUInt16LittleEndian(h[20..]) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(h[22..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(h[24..]) != 44100
            || BinaryPrimitives.ReadUInt16LittleEndian(h[34..]) != 16
            || !h.Slice(36, 4).SequenceEqual("data"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(h[40..]) != sectors * 2352
            || file.Length != 44 + sectors * 2352)
            throw new InvalidDataException("曲目不是刻录包的标准 CD PCM：" + path);
    }

    private static string Hash(string path)
    {
        using FileStream file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
