using DiscImageStudio.Burning;

internal static class BluRayRewritableBurnTests
{
    private const uint FullCapacity = BluRayRewritableMmcCommands.SingleLayerSectors;
    private static readonly BluRayRewritableMedia Unformatted = new(false, true, false, FullCapacity, 0);
    private static readonly BluRayRewritableMedia WithSpareArea = new(true, true, false, FullCapacity - 32, 1);
    private static readonly BluRayRewritableMedia WithoutSpareArea = new(true, false, false, FullCapacity, 0);

    internal static void Run()
    {
        Require(WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.BluRayData, 0x13, [0x43])
            && !WindowsImapiBurner.IsWritableMedia(OpticalBurnMediaKind.BluRayData, 0x13, [0x41])
            && !WindowsImapiBurner.IsWritableBluRayMedia(0x13, [0x43]),
            "BD-RE has a separate route from the BD-R sequential writer");
        CheckPreparation(Unformatted, BluRayRewritablePreparation.Format, "media", "confirm", "report", "format");
        CheckPreparation(WithSpareArea, BluRayRewritablePreparation.Reformat, "media", "confirm", "report", "format");
        CheckPreparation(WithoutSpareArea, BluRayRewritablePreparation.Overwrite, "media", "layout", "confirm");

        FakeSession session = new(Unformatted);
        OpticalBurnRequest request = Request(BluRayRewritablePreparation.Format, _ => false);
        Throws<OperationCanceledException>(() => BluRayRewritableBurn.Prepare(session, request, default, _ => { }));
        Require(session.Operations.SequenceEqual(["media"]), "declining cannot format a disc");

        session = new(WithSpareArea);
        request = Request(BluRayRewritablePreparation.Overwrite, _ => throw new InvalidOperationException("stale confirmation"));
        Throws<InvalidOperationException>(() => BluRayRewritableBurn.Prepare(session, request, default, _ => { }));
        Require(session.Operations.SequenceEqual(["media"]), "changed preparation stops before confirmation and formatting");

        session = new(Unformatted with { CanFormatWithoutSpareArea = false });
        request = Request(BluRayRewritablePreparation.Format, _ => true);
        Throws<NotSupportedException>(() => BluRayRewritableBurn.Prepare(session, request, default, _ => { }));
        Require(session.Operations.SequenceEqual(["media"]), "no-spare support is mandatory for formatting");

        session = new(WithoutSpareArea with { IsWriteProtected = true });
        request = Request(BluRayRewritablePreparation.Overwrite, _ => true);
        Throws<InvalidOperationException>(() => BluRayRewritableBurn.Prepare(session, request, default, _ => { }));
        Require(session.Operations.SequenceEqual(["media"]), "write protection stops before changes");

        session = new(Unformatted);
        request = Request(BluRayRewritablePreparation.Format, _ => true) with { ContentLength = 2048 };
        Throws<InvalidOperationException>(() => BluRayRewritableBurn.Prepare(session, request, default, _ => { }));
        Require(session.Operations.SequenceEqual(["media"]), "invalid length cannot cause formatting");

        using CancellationTokenSource cancellation = new();
        session = new(Unformatted);
        request = Request(BluRayRewritablePreparation.Format, _ =>
        {
            cancellation.Cancel();
            return true;
        });
        Throws<OperationCanceledException>(() => BluRayRewritableBurn.Prepare(session, request, cancellation.Token, _ => { }));
        Require(session.Operations.SequenceEqual(["media"]), "cancellation after consent prevents formatting");
        Console.WriteLine("blu-ray-rewritable-burn: passed (separate profile, distinct consent, verified mapping and no fallback)");
    }

    private static void CheckPreparation(BluRayRewritableMedia media, BluRayRewritablePreparation preparation,
        params string[] expected)
    {
        FakeSession session = new(media);
        OpticalBurnRequest request = Request(preparation, operation =>
        {
            Require(operation == preparation, "consent indicates the actual operation");
            session.Operations.Add("confirm");
            return true;
        });
        BluRayRewritableLayout layout = BluRayRewritableBurn.Prepare(
            session, request, default, _ => session.Operations.Add("report"));
        Require(layout == session.Layout && session.Operations.SequenceEqual(expected),
            "the preparation result is the layout reported by the device");
    }

    private static OpticalBurnRequest Request(BluRayRewritablePreparation preparation, Func<BluRayRewritablePreparation, bool> confirm)
        => new("device", OpticalBurnMediaKind.BluRayData, 32 * 2048, (_, _) => { })
        {
            BluRayRewritablePreparation = preparation,
            ConfirmBluRayRewritable = confirm,
            ProduceMappedBluRayContent = (_, _, _, _) => { },
        };

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeSession(BluRayRewritableMedia media) : IBluRayRewritablePreparationSession
    {
        public List<string> Operations { get; } = [];
        public BluRayRewritableLayout Layout { get; } = new(0x100020, FullCapacity - 32);

        public BluRayRewritableMedia ReadRewritableMedia()
        {
            Operations.Add("media");
            return media;
        }

        public BluRayRewritableLayout ReadRewritableLayout()
        {
            Operations.Add("layout");
            return Layout;
        }

        public BluRayRewritableLayout FormatRewritableWithoutSpareArea(CancellationToken cancellationToken)
        {
            Operations.Add("format");
            return Layout;
        }
    }
}
