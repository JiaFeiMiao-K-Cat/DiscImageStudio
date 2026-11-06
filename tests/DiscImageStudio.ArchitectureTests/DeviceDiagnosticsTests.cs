using System.Dynamic;
using System.Runtime.InteropServices;
using DiscImageStudio.Burning;

public static class DeviceDiagnosticsTests
{
    internal static void Run()
    {
        TestMediaTypeLabels();
        TestIndependentOptionalMediaProperties();
        TestDvdSettingsAndObserverFailures();
        TestEnvironmentForAllMedia();
        Console.WriteLine("device-diagnostics: passed (CD/DVD/BD type labels, independent optional fields, DVD settings and shared environment; no hardware)");
    }

    private static void TestMediaTypeLabels()
    {
        // Expected values follow the published IMAPI enum, including dual-layer
        // and rewritable distinctions needed when diagnosing the wrong medium.
        foreach ((int type, string label) in new (int, string)[]
        {
            (0, "Unknown"), (1, "CD-ROM/CD-R/CD-RW (unspecified)"),
            (2, "CD-R"), (3, "CD-RW"), (4, "DVD-ROM"), (5, "DVD-RAM"),
            (6, "DVD+R"), (7, "DVD+RW"), (8, "DVD+R DL"), (9, "DVD-R"),
            (10, "DVD-RW"), (11, "DVD-R DL"), (12, "Random-access disk"),
            (13, "DVD+RW DL"), (14, "HD DVD-ROM"), (15, "HD DVD-R"),
            (16, "HD DVD-RAM"), (17, "BD-ROM"), (18, "BD-R"), (19, "BD-RE"),
        })
        {
            dynamic medium = new ExpandoObject();
            medium.CurrentPhysicalMediaType = type;
            medium.MediaPhysicallyBlank = true;
            medium.MediaHeuristicallyBlank = false;
            string summary = BurnDeviceDiagnostics.DescribePhysicalMedia((object)medium);
            Require(summary.Contains($"PhysicalMediaType={label} ({type})；", StringComparison.Ordinal)
                && summary.Contains("MediaPhysicallyBlank=True", StringComparison.Ordinal)
                && summary.Contains("MediaHeuristicallyBlank=False", StringComparison.Ordinal),
                $"reported media value {type} retains its label, numeric evidence and independent blank flags");
        }
        Require(BurnDeviceDiagnostics.DescribeMediaType(65536) == "Unknown (65536)"
            && BurnDeviceDiagnostics.DescribeMediaType(-1) == "Unknown (-1)",
            "unknown and vendor-defined types retain their original values");
    }

    private static void TestIndependentOptionalMediaProperties()
    {
        PartialMedia partial = new();
        string summary = BurnDeviceDiagnostics.DescribePhysicalMedia(partial);
        Require(summary.Contains("PhysicalMediaType=BD-R (18)", StringComparison.Ordinal)
            && summary.Contains("MediaPhysicallyBlank=unavailable (0x80004001)", StringComparison.Ordinal)
            && summary.Contains("MediaHeuristicallyBlank=unavailable", StringComparison.Ordinal),
            "missing or failing blank properties do not erase a valid physical type");
        Require(partial.TypeReads == 1 && partial.BlankReads == 1,
            "the optional snapshot reads each supported property once without retries");

        string unknownType = BurnDeviceDiagnostics.DescribePhysicalMedia(new UnavailableTypeMedia());
        Require(unknownType.Contains("PhysicalMediaType=unavailable (0x80004005)", StringComparison.Ordinal)
            && unknownType.Contains("MediaPhysicallyBlank=False", StringComparison.Ordinal)
            && unknownType.Contains("MediaHeuristicallyBlank=True", StringComparison.Ordinal),
            "a failing physical-type getter does not hide either blank-state field");

        dynamic nullType = new ExpandoObject();
        nullType.CurrentPhysicalMediaType = null;
        nullType.MediaPhysicallyBlank = null;
        nullType.MediaHeuristicallyBlank = true;
        string absent = BurnDeviceDiagnostics.DescribePhysicalMedia((object)nullType);
        Require(absent.Contains("PhysicalMediaType=unknown；", StringComparison.Ordinal)
            && absent.Contains("MediaPhysicallyBlank=unknown；", StringComparison.Ordinal),
            "null properties remain unknown rather than fabricated zeros or false");
    }

    private static void TestDvdSettingsAndObserverFailures()
    {
        string summary = BurnDeviceDiagnostics.DescribeDvdSettings(new PartialDvdSettings());
        Require(summary.Contains("BufferUnderrunFreeDisabled=False", StringComparison.Ordinal)
            && summary.Contains("ForceMediaToBeClosed=unavailable (0x80004001)", StringComparison.Ordinal)
            && summary.Contains("ForceOverwrite=False", StringComparison.Ordinal),
            "DVD settings are independently readable and no property is changed");
        bool described = false;
        BurnDeviceDiagnostics.Report(null, () => { described = true; return "unused"; });
        Require(!described, "an absent observer does not trigger optional device reads");
        BurnDeviceDiagnostics.Report(_ => throw new InvalidOperationException("log unavailable"), () => summary);
        BurnDeviceDiagnostics.Report(_ => throw new InvalidOperationException("must not be called"),
            () => throw new COMException("device property unavailable"));
    }

    private static void TestEnvironmentForAllMedia()
    {
        foreach (OpticalBurnMediaKind kind in Enum.GetValues<OpticalBurnMediaKind>())
        {
            int bytes = kind == OpticalBurnMediaKind.CdAudio ? 2352 : 2048;
            OpticalBurnRequest request = new("fake", kind, 32L * bytes, (_, _) => { });
            string summary = BurnDeviceDiagnostics.DescribeEnvironment(request);
            Require(summary.StartsWith("刻录环境：", StringComparison.Ordinal)
                && summary.Contains($"MediaKind={kind}", StringComparison.Ordinal)
                && summary.Contains($"SectorBytes={bytes}", StringComparison.Ordinal)
                && summary.Contains("TrackSectors=32", StringComparison.Ordinal)
                && summary.Contains("Build=", StringComparison.Ordinal),
                "shared environment telemetry uses the selected media's sector size");
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Device diagnostics: " + message);
    }

    // Public test types are visible to dynamic access from the production assembly.
    public sealed class PartialMedia
    {
        public int TypeReads { get; private set; }
        public int BlankReads { get; private set; }
        public int CurrentPhysicalMediaType { get { TypeReads++; return 18; } }
        public bool MediaPhysicallyBlank
        {
            get { BlankReads++; throw new COMException("blank state unavailable", unchecked((int)0x80004001)); }
        }
    }

    public sealed class UnavailableTypeMedia
    {
        public int CurrentPhysicalMediaType => throw new COMException("type unavailable", unchecked((int)0x80004005));
        public bool MediaPhysicallyBlank => false;
        public bool MediaHeuristicallyBlank => true;
    }

    public sealed class PartialDvdSettings
    {
        public bool BufferUnderrunFreeDisabled => false;
        public bool ForceMediaToBeClosed => throw new COMException("close setting unavailable", unchecked((int)0x80004001));
        public bool ForceOverwrite => false;
    }
}
