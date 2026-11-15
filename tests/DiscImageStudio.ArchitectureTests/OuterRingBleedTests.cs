using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

/// <summary>Issue 312: the outer-ring bleed switch caps the writable capacity to 95% of the
/// medium's standard size so the poorest-quality outermost ring stays unwritten. The cap is a
/// recording preference (it must not bake into the saved usable limit) and only applies to
/// sidecar files placed at the outer ring.</summary>
internal static class OuterRingBleedTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "outer-ring-bleed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string image = Path.Combine(directory, "source.png");
            WriteImage(image);
            string files = Path.Combine(directory, "files");
            Directory.CreateDirectory(files);
            File.WriteAllText(Path.Combine(files, "marker.txt"), "outer-ring bleed marker");
            TestStateCap(files);
            TestAdoption(files);
            TestPersistence(directory, files);
            TestGeneration(directory, image, files);
            TestDesignGuide(files);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("outer-ring-bleed: passed (95% cap, gating, raw persistence, adoption, design guide, CD/DVD/BD bounded output)");
    }

    private static void TestStateCap(string files)
    {
        DiscParametersState state = new();

        // CD: default total 359849, default reserve 359847, 95% cap 341856 (alignment 1).
        state.CdDataDirectory = files;
        state.CdFilesAtOuterRing = true;
        Require(state.GetUsableSectors(OpticalDiscFamily.CompactDisc) == 359847, "CD keeps full reserve while bleed is off");
        state.CdOuterRingBleed = true;
        Require(state.GetUsableSectors(OpticalDiscFamily.CompactDisc) == 341856, "CD bleed caps to 95% of standard capacity");
        Require(state.GetUsableSectors(OpticalDiscFamily.CompactDisc, applyOuterRingBleed: false) == 359847,
            "CD raw limit ignores bleed");
        state.CdFilesAtOuterRing = false;
        Require(state.GetUsableSectors(OpticalDiscFamily.CompactDisc) == 359847, "CD inner-ring placement ignores bleed");
        state.CdFilesAtOuterRing = true;
        state.CdDataDirectory = " ";
        Require(state.GetUsableSectors(OpticalDiscFamily.CompactDisc) == 359847, "CD without files ignores a stale bleed flag");
        state.CdDataDirectory = files;
        state.CdUsableSectors = "300000";
        Require(state.GetUsableSectors(OpticalDiscFamily.CompactDisc) == 300000, "CD a smaller user limit wins over the cap");
        state.CdUsableSectors = "350000";
        Require(state.GetUsableSectors(OpticalDiscFamily.CompactDisc) == 341856, "CD the cap clips a larger user limit");
        state.CdUsableSectors = string.Empty;

        // DVD: default total 2295104, default reserve 2294496, 95% cap aligned to 16 = 2180336.
        state.DvdDataDirectory = files;
        state.DvdFilesAtOuterRing = true;
        state.DvdOuterRingBleed = true;
        Require(state.GetUsableSectors(OpticalDiscFamily.Dvd) == 2180336, "DVD bleed caps to 95% aligned to 16");
        Require(state.GetUsableSectors(OpticalDiscFamily.Dvd) % 16 == 0, "DVD cap keeps ECC-block alignment");
        Require(state.GetUsableSectors(OpticalDiscFamily.Dvd, applyOuterRingBleed: false) == 2294496, "DVD raw limit ignores bleed");
        state.DvdOuterRingBleed = false;
        Require(state.GetUsableSectors(OpticalDiscFamily.Dvd) == 2294496, "DVD turning bleed off restores full reserve");

        // BD: default total 12219392, default reserve 12219392, 95% cap aligned to 32 = 11608416.
        state.BdDataDirectory = files;
        state.BdFilesAtOuterRing = true;
        state.BdOuterRingBleed = true;
        Require(state.GetUsableSectors(OpticalDiscFamily.BluRay) == 11608416, "BD bleed caps to 95% aligned to 32");
        Require(state.GetUsableSectors(OpticalDiscFamily.BluRay) % 32 == 0, "BD cap keeps cluster alignment");
        Require(state.GetUsableSectors(OpticalDiscFamily.BluRay, applyOuterRingBleed: false) == 12219392, "BD raw limit ignores bleed");
        state.BdFilesAtOuterRing = false;
        Require(state.GetUsableSectors(OpticalDiscFamily.BluRay) == 12219392, "BD inner-ring placement ignores bleed");

        Require(DiscParametersState.OuterRingBleedCap(OpticalDiscFamily.CompactDisc, 359849) == 341856, "CD cap helper");
        Require(DiscParametersState.OuterRingBleedCap(OpticalDiscFamily.Dvd, 2295104) == 2180336, "DVD cap helper");
        Require(DiscParametersState.OuterRingBleedCap(OpticalDiscFamily.BluRay, 12219392) == 11608416, "BD cap helper");
    }

    private static void TestAdoption(string files)
    {
        // A generated artifact's sidecar records the effective output, so with bleed enabled
        // it carries the 95% cap. Adopting it must keep the raw limit, otherwise a later
        // toggle could no longer restore full capacity.
        DiscParametersState cd = new() { CdDataDirectory = files, CdFilesAtOuterRing = true, CdOuterRingBleed = true };
        long cdRaw = cd.GetUsableSectors(OpticalDiscFamily.CompactDisc, applyOuterRingBleed: false);
        long cdCap = DiscParametersState.OuterRingBleedCap(OpticalDiscFamily.CompactDisc, 359849);
        Require(cd.ResolveAdoptedUsableSectors(OpticalDiscFamily.CompactDisc, 359849, cdCap, filesAtOuterRing: true) == cdRaw,
            "CD adoption keeps the raw limit instead of the bleed cap");
        Require(cd.ResolveAdoptedUsableSectors(OpticalDiscFamily.CompactDisc, 359849, 300000, filesAtOuterRing: true) == 300000,
            "CD adoption keeps an explicit usable limit");
        Require(cd.ResolveAdoptedUsableSectors(OpticalDiscFamily.CompactDisc, 359849, null, filesAtOuterRing: true) == 359849,
            "CD adoption without a stored limit uses the total");
        const long otherTotal = 333000;
        long otherRaw = DiscParametersState.ResolveUsableSectors(OpticalDiscFamily.CompactDisc, otherTotal);
        long otherCap = DiscParametersState.OuterRingBleedCap(OpticalDiscFamily.CompactDisc, otherTotal);
        Require(otherRaw > otherCap, "CD other-total raw limit exceeds its cap");
        Require(cd.ResolveAdoptedUsableSectors(OpticalDiscFamily.CompactDisc, otherTotal, otherCap, filesAtOuterRing: true) == otherRaw,
            "CD adoption of another disc resolves that disc's raw limit");
        cd.CdOuterRingBleed = false;
        Require(cd.ResolveAdoptedUsableSectors(OpticalDiscFamily.CompactDisc, 359849, cdCap, filesAtOuterRing: true) == cdCap,
            "CD adoption without bleed keeps the sidecar limit");
        cd.CdOuterRingBleed = true;
        Require(cd.ResolveAdoptedUsableSectors(OpticalDiscFamily.CompactDisc, 359849, cdCap, filesAtOuterRing: false) == cdCap,
            "CD adoption of an inner-ring artifact keeps the sidecar limit");

        DiscParametersState dvd = new() { DvdDataDirectory = files, DvdFilesAtOuterRing = true, DvdOuterRingBleed = true };
        long dvdRaw = dvd.GetUsableSectors(OpticalDiscFamily.Dvd, applyOuterRingBleed: false);
        long dvdCap = DiscParametersState.OuterRingBleedCap(OpticalDiscFamily.Dvd, 2295104);
        Require(dvd.ResolveAdoptedUsableSectors(OpticalDiscFamily.Dvd, 2295104, dvdCap, filesAtOuterRing: true) == dvdRaw,
            "DVD adoption keeps the raw limit instead of the bleed cap");
        long dvdOtherRaw = DiscParametersState.ResolveUsableSectors(OpticalDiscFamily.Dvd, 2297888);
        long dvdOtherCap = DiscParametersState.OuterRingBleedCap(OpticalDiscFamily.Dvd, 2297888);
        Require(dvd.ResolveAdoptedUsableSectors(OpticalDiscFamily.Dvd, 2297888, dvdOtherCap, filesAtOuterRing: true) == dvdOtherRaw,
            "DVD adoption of another disc resolves that disc's raw limit");

        DiscParametersState bd = new() { BdDataDirectory = files, BdFilesAtOuterRing = true, BdOuterRingBleed = true };
        long bdRaw = bd.GetUsableSectors(OpticalDiscFamily.BluRay, applyOuterRingBleed: false);
        long bdCap = DiscParametersState.OuterRingBleedCap(OpticalDiscFamily.BluRay, 12219392);
        Require(bd.ResolveAdoptedUsableSectors(OpticalDiscFamily.BluRay, 12219392, bdCap, filesAtOuterRing: true) == bdRaw,
            "BD adoption keeps the raw limit instead of the bleed cap");
        Require(bd.ResolveAdoptedUsableSectors(OpticalDiscFamily.BluRay, 12219392, 12000000, filesAtOuterRing: true) == 12000000,
            "BD adoption keeps an explicit usable limit");
    }

    private static void TestDesignGuide(string files)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try
            {
                ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
                DiscParametersState state = shell.State;
                state.CdSectors = "359849";
                state.CdUsableSectors = "359847";
                state.CdDataDirectory = files;
                state.CdFilesAtOuterRing = true;
                state.CdOuterRingBleed = false;
                shell.Design.SetDiscFamily(OpticalDiscFamily.CompactDisc);
                double fullCanvas = shell.Design.ContentOuterRadiusMm;

                state.CdOuterRingBleed = true;
                Require(shell.Design.ContentOuterRadiusMm < fullCanvas,
                    "design canvas shrinks to the CD bleed cap");
                DataRegionInputs capped = CaptureGuideInputs(shell.Design);
                Require(capped.TotalSectors == 359849, "design guide keeps the physical total");
                Require(capped.FilesAtOuterRing, "design guide sees the outer-ring placement");
                Require(capped.UsableSectors == 341856, "design guide plans with the CD bleed cap");

                state.CdOuterRingBleed = false;
                Require(Math.Abs(shell.Design.ContentOuterRadiusMm - fullCanvas) < 1e-9,
                    "design canvas restores the full radius when bleed is off");
                DataRegionInputs full = CaptureGuideInputs(shell.Design);
                Require(full.UsableSectors == 359847, "design guide returns to the raw limit when bleed is off");
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>The design page snapshots its planner inputs in a private method; the guide and
    /// the export must be fed the same resolved capacity.</summary>
    private static DataRegionInputs CaptureGuideInputs(DesignViewModel design)
        => (DataRegionInputs)typeof(DesignViewModel)
            .GetMethod("CaptureDataRegionInputs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(design, null)!;

    private static void TestPersistence(string directory, string files)
    {
        foreach (string family in new[] { ProjectDocument.CdFamily, ProjectDocument.DvdFamily, ProjectDocument.BluRayFamily })
        {
            ProjectDocument document = new(
                ProjectJsonStore.CurrentSchemaVersion, "外缘出血", family,
                new ProjectGeometry(24, 58, 20480), [],
                new ProjectOutputs(Path.Combine(directory, family + ".iso"), false, files, "FILES", false,
                    FilesAtOuterRing: true, OuterRingBleed: true),
                null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            string path = Path.Combine(directory, family + ".discproj");
            ProjectJsonStore.Save(path, document);
            ProjectDocument loaded = ProjectJsonStore.Load(path);
            Require(loaded.Outputs.OuterRingBleed, family + " bleed survives save/load");

            DiscParametersState state = new();
            ProjectSession.ApplyToState(loaded, state);
            Require(Bleed(family, state) && OuterRing(family, state), family + " project restores bleed and placement");
            Require(ProjectSession.CaptureOutputs(loaded, state).OuterRingBleed, family + " saving captures bleed");

            // The saved usable limit stays raw: baking the cap in would stop a later toggle
            // from restoring full capacity.
            long raw = state.GetUsableSectors(Family(family), applyOuterRingBleed: false);
            long capped = state.GetUsableSectors(Family(family));
            Require(capped < raw, family + " bleed reduces the effective capacity");
            Require(ProjectSession.CaptureGeometry(loaded, state).UsableSectors == raw,
                family + " geometry persists the raw usable limit, not the cap");

            SetBleed(family, state, false);
            Require(state.GetUsableSectors(Family(family)) == raw, family + " toggling bleed off restores full capacity");
            SetBleed(family, state, true);

            // A legacy file without the field opens with bleed off and does not re-add it.
            JsonObject legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            legacy["outputs"]!.AsObject().Remove("outerRingBleed");
            File.WriteAllText(path, legacy.ToJsonString());
            ProjectDocument old = ProjectJsonStore.Load(path);
            Require(!old.Outputs.OuterRingBleed, family + " missing bleed defaults to off");
            ProjectSession.ApplyToState(old, state);
            Require(!Bleed(family, state), family + " legacy project clears a previous bleed selection");
            ProjectJsonStore.Save(path, old);
            Require(!File.ReadAllText(path).Contains("outerRingBleed", StringComparison.Ordinal),
                family + " default bleed does not add a field to existing projects");
        }
    }

    private static void TestGeneration(string directory, string image, string files)
    {
        // CD: total 256 -> 95% cap 243.
        DiscParametersState cdState = new() { CdSectors = "256", CdDataDirectory = files, CdFilesAtOuterRing = true, CdOuterRingBleed = true };
        long cdCap = cdState.GetUsableSectors(OpticalDiscFamily.CompactDisc);
        Require(cdCap == 243, "CD generation cap value");
        CdDiscParameters cd = new(24, 58, 256, UsableSectors: cdCap);
        CdDataLayoutPlan cdPlan = CdDataImageGenerator.PrepareLayout(files, cd, "FILES", filesAtOuterRing: true);
        using (MemoryStream stream = new())
        {
            CdDataImageGenerator.GenerateToStream(image, stream, cd, cdPlan);
            byte[] bytes = stream.ToArray();
            Require(cdPlan.TotalSectors == 243 && bytes.Length == 243 * 2048
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16 * 2048 + 80)) == 243,
                "CD bleed bounds the data ISO to the capped capacity");
            Require(bytes.AsSpan().IndexOf("outer-ring bleed marker"u8) >= 0, "CD outer-ring files survive the bleed cap");
        }

        // DVD: total 256 -> 95% cap aligned to 16 = 240. An explicit full usable limit keeps
        // the legacy 608-sector reserve from dominating at this small total.
        DiscParametersState dvdState = new() { DvdTotalSectors = "256", DvdUsableSectors = "256", DvdDataDirectory = files, DvdFilesAtOuterRing = true, DvdOuterRingBleed = true };
        long dvdCap = dvdState.GetUsableSectors(OpticalDiscFamily.Dvd);
        Require(dvdCap == 240, "DVD generation cap value");
        DvdStreamingOptions dvd = new(256, 24, 58, UsableSectors: checked((uint)dvdCap), FastParallelism: 1);
        DvdHybridStreamingPlan dvdPlan = DvdStreamingGenerator.PrepareHybrid(files, dvd, "FILES", filesAtOuterRing: true);
        using (MemoryStream stream = new())
        {
            DvdStreamingGenerator.GenerateHybrid(image, stream, dvd, dvdPlan);
            byte[] bytes = stream.ToArray();
            Require(bytes.Length == 240 * 2048
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16 * 2048 + 80)) == 240,
                "DVD bleed bounds the hybrid ISO to the capped capacity");
            Require(bytes.AsSpan().IndexOf("outer-ring bleed marker"u8) >= 0, "DVD outer-ring files survive the bleed cap");
        }

        // BD: total 2048 -> 95% cap aligned to 32 = 1920.
        DiscParametersState bdState = new() { BdTotalSectors = "2048", BdDataDirectory = files, BdFilesAtOuterRing = true, BdOuterRingBleed = true };
        long bdCap = bdState.GetUsableSectors(OpticalDiscFamily.BluRay);
        Require(bdCap == 1920, "BD generation cap value");
        BluRayDiscParameters bd = new(2048, 1, 24, 58, UsableSectors: checked((uint)bdCap));
        BluRayIsoPlan bdPlan = BluRayIsoGenerator.Prepare(bd, files, filesAtOuterRing: true);
        using (MemoryStream stream = new())
        {
            BluRayIsoGenerator.GenerateToStream(stream, bdPlan, new BluRayConstantByteMapping(0));
            Require(bdPlan.TotalSectors == 1920 && stream.Length == 1920 * 2048,
                "BD bleed bounds the UDF ISO to the capped capacity");
        }
    }

    private static OpticalDiscFamily Family(string family) => family switch
    {
        ProjectDocument.DvdFamily => OpticalDiscFamily.Dvd,
        ProjectDocument.BluRayFamily => OpticalDiscFamily.BluRay,
        _ => OpticalDiscFamily.CompactDisc,
    };

    private static bool Bleed(string family, DiscParametersState state) => family switch
    {
        ProjectDocument.DvdFamily => state.DvdOuterRingBleed,
        ProjectDocument.BluRayFamily => state.BdOuterRingBleed,
        _ => state.CdOuterRingBleed,
    };

    private static void SetBleed(string family, DiscParametersState state, bool value)
    {
        switch (family)
        {
            case ProjectDocument.DvdFamily: state.DvdOuterRingBleed = value; break;
            case ProjectDocument.BluRayFamily: state.BdOuterRingBleed = value; break;
            default: state.CdOuterRingBleed = value; break;
        }
    }

    private static bool OuterRing(string family, DiscParametersState state) => family switch
    {
        ProjectDocument.DvdFamily => state.DvdFilesAtOuterRing,
        ProjectDocument.BluRayFamily => state.BdFilesAtOuterRing,
        _ => state.CdFilesAtOuterRing,
    };

    private static void WriteImage(string path)
    {
        byte[] pixels = new byte[32 * 32 * 4];
        for (int i = 0; i < 32 * 32; i++)
        {
            double x = (i % 32 + .5 - 16) / 16;
            double y = (i / 32 + .5 - 16) / 16;
            byte gray = (byte)(x * x + y * y >= .85 * .85 ? 0 : 255);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = gray;
            pixels[i * 4 + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Outer ring bleed: " + message);
    }
}