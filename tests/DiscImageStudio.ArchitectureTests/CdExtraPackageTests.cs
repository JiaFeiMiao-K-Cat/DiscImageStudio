using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using DiscImageStudio;
using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Services;

internal static class CdExtraPackageTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "cd-extra-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { CheckPackage(root); }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("cd-extra-package: manifest, measured ISO, README, track padding, source hashes and failed preparation passed");
    }

    private static void CheckPackage(string root)
    {
        var geometry = new CdDiscParameters(24.5, 27.5, 15_000, UsableSectors: 14_998);
        WpfTestHost.Run(() =>
        {
            var window = new CdExtraWindow(null, geometry, ToneProfile.Photo) { ShowActivated = false };
            try
            {
                window.Show();
                if (!window.IsVisible || window.Icon is null || window.FindName("OpenPackageButton") is not Button { IsEnabled: true })
                    throw new InvalidOperationException("The legacy package tool must open with an accessible import action.");
            }
            finally { window.Close(); }
        });
        string artwork = Path.Combine(root, "artwork.png");
        string wave = Path.Combine(root, "track-01.wav");
        CdExtraLayoutTests.WriteImage(artwork);
        CdExtraLayoutTests.WriteWave(wave, frames: 300 * 588, seed: 12);
        string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

        // The legacy tool writes these PascalCase fields and already padded, canonical CD PCM.
        File.WriteAllText(Path.Combine(root, "package.cdextra.json"), JsonSerializer.Serialize(new
        {
            Version = 1, Geometry = geometry, Tone = ToneProfile.Photo, ArtworkSha256 = Sha(artwork),
            Tracks = new[] { new { File = "track-01.wav", Title = "old song", Sha256 = Sha(wave), Sectors = 300 } }
        }));
        var package = CdExtraPackage.Load(root);
        var request = package.CreateBurnRequest(root, "fake", new OpticalWriteSpeed(1200, false), default, _ => { });
        long measured = request.PredictedSession2StartLba + 9;
        var data = request.CreateData(new CdExtraSessionStart(measured, 1000, 15_000, [0]));
        string session = Directory.GetDirectories(root, "data-*").Single();
        string iso = Path.Combine(session, "artwork-data.iso");
        var meta = CdExtraImageMetadata.TryLoad(iso);
        byte[] bytes = File.ReadAllBytes(iso);
        using var output = new MemoryStream();
        data.Produce(output, default);
        if (meta is not { Session2StartMeasured: true } || meta.Session2StartLba != measured
            || bytes.Length != 1000 * 2048 || !bytes.AsSpan().SequenceEqual(output.ToArray())
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16 * 2048 + 80)) != measured + 1000 - 150
            || bytes.AsSpan().IndexOf("README.TXT"u8) < 0)
            throw new InvalidOperationException("Legacy package did not stream the measured ISO with its actual data file.");

        string shorter = Path.Combine(root, "short.wav");
        CdExtraLayoutTests.WriteWave(shorter, frames: 150 * 588, seed: 8);
        string next = Path.Combine(root, "new");
        CdExtraPackage.Create(next, artwork, [shorter, wave], geometry, ToneProfile.Photo, _ => { }, default);
        if (!CdExtraPackage.Load(next).Tracks.Select(track => track.Sectors).SequenceEqual(new long[] { 300, 300 }))
            throw new InvalidOperationException("The new package must preserve track order and pad short tracks.");
        string failed = Path.Combine(root, "failed");
        try
        {
            CdExtraPackage.Create(failed, artwork, [wave, failed + ".wav"], geometry, ToneProfile.Photo, _ => { }, default);
            throw new InvalidOperationException("Preparation should have failed for missing music.");
        }
        catch (FileNotFoundException) { }
        if (Directory.GetDirectories(root, "failed*").Length != 0) throw new IOException("Partial package leaked.");
        File.AppendAllText(artwork, "changed");
        try { CdExtraPackage.Load(root); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Changed artwork was accepted.");
    }
}
