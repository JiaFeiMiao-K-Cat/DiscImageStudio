using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Burning;

public sealed record CdExtraCheckpoint(int Version, string DeviceId, int Profile, long AtipLeadout,
    long[] AudioStarts, uint? DiscId, string? AudioFingerprint, long StartLba, long Sectors, string ImageSha256)
{
    public const string FileName = "picture.cdresume.json";
    internal const string ImageName = "artwork-data.iso";
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "DiscImageStudio", "CDExtra", "Recovery");

    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal CdExtraWriteWindow? UncertainCommand { get; init; }

    internal int TrackNumber => AudioStarts.Length + 1;

    internal static CdExtraCheckpoint Load(string path)
    {
        using FileStream input = File.OpenRead(path);
        if (input.Length > 64 * 1024) throw new InvalidDataException("图案断点文件过大。");
        CdExtraCheckpoint? job;
        try { job = JsonSerializer.Deserialize<CdExtraCheckpoint>(input); }
        catch (JsonException e) { throw new InvalidDataException("图案断点文件不完整或格式错误。", e); }
        if (job is null || job.Version != 1 || string.IsNullOrWhiteSpace(job.DeviceId)
            || !DynamicCdExtraSession.IsRecordableCdProfile(job.Profile)
            || job.AudioStarts is not { Length: >= 1 and <= CdExtraBurnRequest.MaximumAudioTracks }
            || job.StartLba <= 0 || job.AtipLeadout is <= 0 or > int.MaxValue
            || job.Sectors < CdExtraAudioTrack.MinimumSectors || job.Sectors > int.MaxValue
            || job.StartLba > job.AtipLeadout - job.Sectors
            || job.UncertainCommand is { } window && !window.Fits(job)
            || job.ImageSha256 is not { Length: 64 } || !job.ImageSha256.All(Uri.IsHexDigit)
            || job.AudioFingerprint is { } signature && (signature.Length != 64 || !signature.All(Uri.IsHexDigit)))
            throw new InvalidDataException("无效或不受支持的 CD Extra 图案断点。");
        long previous = -1;
        foreach (long lba in job.AudioStarts)
        {
            if (lba <= previous || lba >= job.StartLba)
                throw new InvalidDataException("断点中的音轨位置无效。");
            previous = lba;
        }
        return job;
    }

    internal static (string Path, FileStream Image, CdExtraCheckpoint Job) Prepare(CdExtraDataContent data,
        CdExtraBurnRequest request, CdExtraMediaInfo media, CdExtraSessionStart start,
        (uint? DiscId, string? Audio) identity, CancellationToken token)
    {
        bool createImage = data.SavedImagePath is null;
        string folder = createImage
            ? System.IO.Path.Combine(request.RecoveryRoot ?? DefaultRoot, Guid.NewGuid().ToString("N"))
            : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(data.SavedImagePath!))!;
        string imagePath = System.IO.Path.Combine(folder, ImageName);
        string record = System.IO.Path.Combine(folder, FileName);
        if (!createImage && !string.Equals(System.IO.Path.GetFullPath(data.SavedImagePath!), imagePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("刻录包图案镜像名称无效。");
        Directory.CreateDirectory(folder);
        try
        {
            if (createImage)
            {
                using FileStream output = new(imagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                data.Produce(output, token);
                output.Flush(true);
            }
            FileStream input = File.OpenRead(imagePath);
            try
            {
                if (input.Length != data.ContentLength) throw new InvalidDataException("图案镜像长度与计划不符。");
                var job = new CdExtraCheckpoint(1, request.DeviceId, media.Profile, media.AtipLeadoutStart,
                    start.AudioTrackStarts.ToArray(), identity.DiscId, identity.Audio, start.NextWritableAddress,
                    data.Sectors, Hash(input, token));
                job.Save(record, replace: false);
                return (record, input, job);
            }
            catch { input.Dispose(); throw; }
        }
        catch
        {
            if (createImage) RemoveScratch(record);
            throw;
        }
    }

    internal CdExtraCheckpoint WithUncertainCommand(string path, CdExtraWriteWindow? window)
    {
        if (window == UncertainCommand) return this;
        if (window is not null && !window.Fits(this))
            throw new InvalidDataException("出错命令超出图案轨范围，无法记录安全的续写位置。");
        var updated = this with { UncertainCommand = window };
        updated.Save(path, replace: true);
        return updated;
    }

    private void Save(string path, bool replace)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, this);
                output.Flush(true);
            }
            File.Move(temporary, path, replace);
        }
        finally { File.Delete(temporary); }
    }

    internal FileStream OpenReference(string path, CancellationToken token)
    {
        string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        FileStream image = File.OpenRead(System.IO.Path.Combine(folder, ImageName));
        try
        {
            if (image.Length != Sectors * CdTaoWriter.DataBlockBytes
                || !string.Equals(Hash(image, token), ImageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("图案源镜像的长度或 SHA-256 与断点不符。");
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        stream.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void CopyImage(Stream input, Stream output, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
    }

    // Only app-owned scratch files are removed. Package images and interrupted jobs are retained.
    internal static void RemoveScratch(string record)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(record))!;
        if (!string.Equals(Path.GetDirectoryName(folder), DefaultRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) return;
        try
        {
            File.Delete(Path.Combine(folder, FileName));
            File.Delete(Path.Combine(folder, ImageName));
            Directory.Delete(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
