using System.IO;
using System.Text;
using DiscImageStudio.Core;
using DiscImageStudio.Iso;
using DiscImageStudio.Localization;
using DiscImageStudio.ViewModels;

internal static class VolumeLabelTests
{
    private const string SixteenCharacterLabel = "MEMORY_MANAGER16";
    private const string SeventeenCharacterLabel = "MEMORY_MANAGER16Z";
    private const string UnicodeSixteenCharacterLabel = "记忆管理局FalseMemoXY";

    internal static void Run()
    {
        VerifyWriterLimit();
        VerifyResolver();
        Console.WriteLine("volume-label: passed (Joliet 16/17 boundary, descriptor bytes, fallbacks, BD pass-through, localized message)");
    }

    private static void VerifyWriterLimit()
    {
        Require(Iso9660ImageWriter.MaximumJolietVolumeLabelCharacters == 16, "the published Joliet label limit is 16 characters");

        byte[] image = WriteMinimalIso(SixteenCharacterLabel);
        Require(ReadPrimaryVolumeLabel(image) == SixteenCharacterLabel,
            "a 16-character label fits the primary descriptor unchanged");
        Require(ReadJolietVolumeLabel(image) == SixteenCharacterLabel,
            "a 16-character label fits the Joliet descriptor unchanged");

        byte[] unicodeImage = WriteMinimalIso(UnicodeSixteenCharacterLabel);
        Require(ReadJolietVolumeLabel(unicodeImage) == UnicodeSixteenCharacterLabel,
            "16 non-ASCII UTF-16 units fit the Joliet field");

        Throws<ArgumentException>(() => Iso9660ImageWriter.Plan(null, 32, SeventeenCharacterLabel),
            "a 17-character label is rejected instead of silently truncated");
        ArgumentException writerError = CaptureArgumentException(
            () => Iso9660ImageWriter.Plan(null, 32, SeventeenCharacterLabel));
        Require(writerError.Message.Contains("16", StringComparison.Ordinal),
            "the writer error names the Joliet limit");
    }

    private static void VerifyResolver()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"volume-label-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string originalLanguage = L.CurrentLanguage;
        try
        {
            L.Initialize(L.Chinese);
            DiscParametersState state = new(Path.Combine(directory, "presets.json"))
            {
                CdVolumeLabel = "  " + SixteenCharacterLabel + "  ",
                DvdVolumeLabel = SixteenCharacterLabel,
                BdVolumeLabel = SeventeenCharacterLabel,
            };
            Require(state.ResolveVolumeLabel(OpticalDiscFamily.CompactDisc) == SixteenCharacterLabel,
                "the CD label is trimmed at the limit");
            Require(state.ResolveVolumeLabel(OpticalDiscFamily.Dvd) == SixteenCharacterLabel,
                "a DVD label at the limit is accepted");
            Require(state.ResolveVolumeLabel(OpticalDiscFamily.BluRay) == SeventeenCharacterLabel,
                "Blu-ray keeps the wider UDF label");

            state.CdVolumeLabel = SeventeenCharacterLabel;
            state.DvdVolumeLabel = SeventeenCharacterLabel;
            Throws<ArgumentException>(() => state.ResolveVolumeLabel(OpticalDiscFamily.CompactDisc),
                "a 17-character CD label is rejected before generation");
            ArgumentException chineseError = CaptureArgumentException(
                () => state.ResolveVolumeLabel(OpticalDiscFamily.Dvd));
            Require(chineseError.Message == "卷标最长 16 个字符（Joliet 标准）。",
                "the resolver error explains the Joliet limit");

            L.Initialize(L.English);
            ArgumentException englishError = CaptureArgumentException(
                () => state.ResolveVolumeLabel(OpticalDiscFamily.Dvd));
            Require(englishError.Message == "Volume labels are limited to 16 characters (Joliet).",
                "the resolver error follows the UI language");

            state.CdVolumeLabel = " ";
            state.DvdVolumeLabel = string.Empty;
            state.BdVolumeLabel = "  ";
            Require(state.ResolveVolumeLabel(OpticalDiscFamily.CompactDisc) == "CD_IMAGE",
                "a blank CD label falls back to CD_IMAGE");
            Require(state.ResolveVolumeLabel(OpticalDiscFamily.Dvd) == "DISC_IMAGE",
                "a blank DVD label falls back to DISC_IMAGE");
            Require(state.ResolveVolumeLabel(OpticalDiscFamily.BluRay) == "BD_IMAGE",
                "a blank Blu-ray label falls back to BD_IMAGE");
        }
        finally
        {
            L.Initialize(originalLanguage);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] WriteMinimalIso(string label)
    {
        Iso9660ImageWriter.LayoutPlan plan = Iso9660ImageWriter.Plan(null, 32, label);
        using MemoryStream output = new();
        Iso9660ImageWriter.WriteSequentialPrefix(plan, output, 32);
        return output.ToArray();
    }

    private static string ReadPrimaryVolumeLabel(byte[] image)
        => Encoding.ASCII.GetString(image, 16 * Iso9660ImageWriter.LogicalSectorBytes + 40, 32).TrimEnd(' ');

    private static string ReadJolietVolumeLabel(byte[] image)
        => Encoding.BigEndianUnicode.GetString(image, 17 * Iso9660ImageWriter.LogicalSectorBytes + 40, 32);

    private static ArgumentException CaptureArgumentException(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ArgumentException: " + action);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Volume label: " + message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
    }
}
