using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Controls;
using DiscImageStudio.Core.Calibration;

internal static class CalibrationPhotoCanvasTests
{
    internal static void Run()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                TestPhotoBoundaryEditing();
                TestRectifiedCurveEditing();
                TestBoundaryPolylineEditing();
                TestManualRingEditing();
                TestThreePointRingEditing();
                TestRadialArcLengthEditing();
                TestPreviewAndOpenPrediction();
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        Console.WriteLine("calibration-photo-canvas: passed (photo pixels, corrected mm, boundary drag, ring and curve editing, read-only previews)");
    }

    private static void TestPhotoBoundaryEditing()
    {
        var points = new ObservableCollection<CalibrationPhotoBoundaryPoint>();
        foreach (var boundary in new[] { CalibrationPhotoBoundaryKind.OuterEdge, CalibrationPhotoBoundaryKind.CenterHole })
        for (int index = 0; index < 8; index++)
        {
            double angle = index * Math.PI / 4;
            double radius = boundary == CalibrationPhotoBoundaryKind.OuterEdge ? 240 : 30;
            points.Add(new(boundary, index, 400 + radius * Math.Cos(angle), 300 + radius * .8 * Math.Sin(angle)));
        }
        var edits = new List<CalibrationBoundaryEdit>();
        var canvas = CreateCanvas();
        canvas.Image = Bitmap(800, 600);
        Near(new Point(450, 350), canvas.WorldToView(new Point(399.5, 299.5)),
            "source pixel centres align with the photo service's integer sampling convention");
        canvas.BoundaryPoints = points;
        canvas.BoundaryEditedCommand = new EditCommand<CalibrationBoundaryEdit>(edit =>
        {
            edits.Add(edit);
            int index = points.ToList().FindIndex(p => p.Boundary == edit.Boundary && p.Index == edit.Index);
            points[index] = new(edit.Boundary, edit.Index, edit.X, edit.Y);
        });
        var snapshot = points.ToArray();
        foreach (var zoom in new[] { 1.5, 2.0, .7 })
        {
            canvas.ZoomBy(zoom);
            canvas.PanBy(new Vector(21, -13));
            Near(new Point(714, 113), canvas.ViewToWorld(canvas.WorldToView(new Point(714, 113))), "raw photo uses invertible original-pixel coordinates");
        }
        Require(snapshot.SequenceEqual(points), "photo zoom and pan do not edit fitted boundary inputs");
        Require(!canvas.AddTraceAt(canvas.WorldToView(new Point(500, 300))), "the photo-boundary step cannot add curve observations");

        foreach (var boundary in new[] { CalibrationPhotoBoundaryKind.OuterEdge, CalibrationPhotoBoundaryKind.CenterHole })
        {
            var original = points.First(p => p.Boundary == boundary && p.Index == 1);
            var start = new Point(original.X, original.Y);
            var destination = start + new Vector(12, -8);
            Require(canvas.BeginDragAt(canvas.WorldToView(start)), "either boundary can be dragged directly without choosing its kind first");
            canvas.DragTo(canvas.WorldToView(destination));
            Require(points.Contains(original), "boundary preview does not commit intermediate samples");
            canvas.CompleteDrag();
            var updated = points.Single(p => p.Boundary == boundary && p.Index == 1);
            Near(destination, new Point(updated.X, updated.Y), "boundary edit commits original-photo pixel coordinates");
        }
        Require(edits.Count == 2 && points.Count == 16, "each boundary drag changes exactly one of the sixteen fixed controls");
        var preserved = points.ToArray();
        var probe = points[0];
        canvas.BeginDragAt(canvas.WorldToView(new Point(probe.X, probe.Y)));
        canvas.DragTo(canvas.WorldToView(new Point(probe.X + 20, probe.Y - 10)));
        canvas.CompleteDrag(false);
        canvas.RemoveSelectedTracePoint();
        Require(preserved.SequenceEqual(points) && edits.Count == 2, "cancel and Delete do not remove physical-circle boundary controls");

        canvas.BeginDragAt(canvas.WorldToView(new Point(probe.X, probe.Y)));
        canvas.DragTo(canvas.WorldToView(new Point(probe.X + 20, probe.Y - 10)));
        canvas.IsRectificationPreview = true;
        canvas.Image = Bitmap(840, 840);
        Require(edits.Count == 2, "changing to a corrected preview cancels an unfinished raw-pixel gesture");
        Require(!canvas.BeginDragAt(canvas.WorldToView(new Point(probe.X, probe.Y))) && !canvas.AddTraceAt(canvas.WorldToView(new Point(25, 0))),
            "corrected preview in step one is read-only");
        Near(new Point(-24, 45), canvas.ViewToWorld(canvas.WorldToView(new Point(-24, 45))), "corrected preview uses disc mm rather than source-image pixels");
    }

    private static void TestRectifiedCurveEditing()
    {
        var points = new ObservableCollection<CalibrationTracePoint>();
        var edits = new List<CalibrationTraceEdit>();
        var canvas = CreateCanvas();
        canvas.IsPhotoStep = false;
        canvas.Image = Bitmap(840, 840);
        canvas.TracePoints = points;
        canvas.TraceEditedCommand = new EditCommand<CalibrationTraceEdit>(edit =>
        {
            edits.Add(edit);
            var values = points.ToList();
            if (edit.Index >= 0) values.RemoveAll(p => p.Arm == edit.Arm && p.Index == edit.Index);
            if (!edit.Remove) values.Add(new(edit.Arm, edit.Index, edit.XMm, edit.YMm));
            points.Clear();
            foreach (var group in values.GroupBy(p => p.Arm))
            {
                int index = 0;
                foreach (var point in group.OrderBy(p => p.XMm * p.XMm + p.YMm * p.YMm))
                    points.Add(point with { Index = index++ });
            }
        });
        foreach (var location in new[] { new Point(18, -3), new Point(42, 9), new Point(29, 1) })
            Require(canvas.AddTraceAt(canvas.WorldToView(location)), "clicking corrected photo appends a curve sample without a fixed count");
        Require(points.Count == 3 && points.All(p => p.Arm == 0), "single curve records all samples in one curve identity");
        Require(edits.All(edit => edit.Index == -1), "new samples use the append sentinel even when the VM sorts them by radius");
        Require(!canvas.AddTraceAt(canvas.WorldToView(new Point(80, 0))), "clicks beyond the corrected image do not become observations");

        var snapshot = points.ToArray();
        canvas.ZoomBy(3);
        canvas.PanBy(new Vector(-63, 41));
        Near(new Point(29, 1), canvas.ViewToWorld(canvas.WorldToView(new Point(29, 1))), "trace zoom and pan preserve disc-centred mm");
        Require(snapshot.SequenceEqual(points), "view navigation never rewrites trace observations");
        var moved = new Point(48, 7);
        Require(canvas.BeginDragAt(canvas.WorldToView(new Point(29, 1))), "any existing curve point starts a drag on one pointer press");
        canvas.PredictionContours = [[new(-10, 0), new(0, 10), new(10, 0)]];
        canvas.DragTo(canvas.WorldToView(moved));
        Require(snapshot.SequenceEqual(points), "prediction refresh and moving preview preserve the prior observations until release");
        canvas.CompleteDrag();
        Near(moved, Position(points[^1]), "drag edits use mm and survive the VM's reordered sample indices");
        canvas.RemoveSelectedTracePoint();
        Require(points.Count == 2 && points.All(p => (Position(p) - moved).Length > 1e-8), "Delete follows the moved sample after radius-based reindexing");
        Require(points.Any(p => (Position(p) - new Point(42, 9)).Length < 1e-8), "Delete does not remove the point that inherited the old index");

        var prior = points.ToArray();
        canvas.BeginDragAt(canvas.WorldToView(Position(points[0])));
        canvas.DragTo(canvas.WorldToView(new Point(20, -4)));
        canvas.CompleteDrag(false);
        Require(prior.SequenceEqual(points), "Escape cancels a trace move");
        canvas.BeginDragAt(canvas.WorldToView(Position(points[0])));
        canvas.DragTo(canvas.WorldToView(new Point(20, -4)));
        canvas.IsPhotoStep = true;
        Require(prior.SequenceEqual(points), "returning to photo correction cancels an unfinished mm gesture");
        canvas.IsPhotoStep = false;
        canvas.IsTargetPreview = true;
        Require(!canvas.BeginDragAt(canvas.WorldToView(Position(points[0]))) && !canvas.AddTraceAt(canvas.WorldToView(new Point(25, 3))),
            "the engraved-target preview cannot accidentally receive photo observations");
        canvas.IsTargetPreview = false;
        canvas.Image = null;
        Require(!canvas.AddTraceAt(canvas.WorldToView(new Point(25, 3))), "a missing photo never accepts guessed curve observations");
    }

    private static void TestBoundaryPolylineEditing()
    {
        var canvas = CreateCanvas();
        canvas.IsPhotoStep = false;
        canvas.Image = Bitmap(840, 840);
        canvas.IsBoundarySegmentMode = true;
        var points = new List<CalibrationTracePoint>();
        var edits = new List<CalibrationTraceEdit>();
        int nextId = 0;
        void Publish() => canvas.TracePoints = points.ToArray();
        canvas.TraceEditedCommand = new EditCommand<CalibrationTraceEdit>(edit =>
        {
            edits.Add(edit);
            int index = points.FindIndex(p => p.Arm == edit.Arm && p.Index == edit.Index);
            if (edit.Remove) points.RemoveAt(index);
            else if (index >= 0) points[index] = new(edit.Arm, edit.Index, edit.XMm, edit.YMm);
            else
            {
                int before = edit.InsertBeforeIndex is int id ? points.FindIndex(p => p.Arm == edit.Arm && p.Index == id) : points.Count;
                points.Insert(before, new(edit.Arm, nextId++, edit.XMm, edit.YMm));
            }
            Publish();
        });
        Publish();
        void Add(double x, double y) => Require(canvas.AddTraceAt(canvas.WorldToView(new(x, y))), "boundary addition is accepted");
        void Click(CalibrationTracePoint point)
        {
            Require(canvas.BeginDragAt(canvas.WorldToView(Position(point))), "an endpoint can be selected");
            canvas.CompleteDrag();
        }

        Add(30, 0); Add(28, -4); Add(26, -8);
        Require(points.Select(p => p.Index).SequenceEqual(new[] { 0, 1, 2 }),
            "sequential tracing preserves clicked neighbours even while radius decreases");
        Add(32, -4); Add(34, -8);
        Require(points.Select(p => p.Index).SequenceEqual(new[] { 4, 3, 0, 1, 2 }),
            "starting at a tip supports extending both sides without crossing the completed side");
        Add(33, -6);
        Require(points.Select(p => p.Index).SequenceEqual(new[] { 4, 5, 3, 0, 1, 2 })
            && edits[^1].InsertBeforeIndex == 3,
            "a click on an existing line inserts between its actual neighbours, not by radius or creation ID");
        canvas.ZoomBy(2); canvas.PanBy(new(13, -19));
        Add(29, -2);
        Require(points.Select(p => p.Index).SequenceEqual(new[] { 4, 5, 3, 0, 6, 1, 2 }),
            "line insertion works at another zoom and pan without reordering prior points");

        // A tight U has two nearby endpoints. Explicitly selecting one must win over
        // distance and line-insertion heuristics, and subsequent clicks keep that end.
        canvas.ResetView();
        points = [new(0, 0, 30, 0), new(0, 1, 26, -6), new(0, 2, 33, -6), new(0, 3, 31, 0)];
        nextId = 4; Publish();
        Click(points[0]);
        Add(32, 2); Add(33, 4);
        Require(points.Select(p => p.Index).SequenceEqual(new[] { 5, 4, 0, 1, 2, 3 }),
            "an explicitly selected U endpoint stays active even when the opposite endpoint is closer");
        canvas.ClearTraceContinuation();
        Add(35, -1);
        Require(points[^1].Index == 6 && edits[^1].InsertBeforeIndex is null,
            "releasing the fixed endpoint restores extension from the nearer opposite end");

        int[] order = points.Select(p => p.Index).ToArray();
        var moved = points.Single(p => p.Index == 1);
        Require(canvas.BeginDragAt(canvas.WorldToView(Position(moved))), "a middle boundary point can be dragged");
        canvas.DragTo(canvas.WorldToView(new(37, -1))); canvas.CompleteDrag();
        Require(points.Select(p => p.Index).SequenceEqual(order),
            "moving across other radii never changes the point's two neighbours");
        Near(new(37, -1), Position(points.Single(p => p.Index == 1)),
            "moving across other radii commits the transformed coordinates within floating-point precision");
        canvas.RemoveSelectedTracePoint();
        Require(points.Select(p => p.Index).SequenceEqual(order.Where(i => i != 1)),
            "deleting after drag removes the same stable point and leaves the remaining polyline intact");

        Click(points[0]);
        canvas.SelectedTraceArm = 1; canvas.SelectedTraceArm = 0;
        Add(39, -2);
        Require(points[^1].Index == 7, "changing segment clears a previously fixed extension endpoint");
        Click(points[0]); canvas.RemoveSelectedTracePoint();
        Add(43, -3);
        Require(points[^1].Index == 8, "deleting the fixed endpoint removes its continuation anchor");

        // Compare with the unchanged drawing: the selected point's label may share
        // pixels with a preview probe, but must not count as a leftover preview.
        canvas.UpdateLayout();
        var baseline = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32); baseline.Render(canvas);
        static byte[] Pixels(BitmapSource bitmap)
        {
            var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
            return bytes;
        }
        canvas.PreviewTraceAt(canvas.WorldToView(new(53, -5)));
        canvas.UpdateLayout();
        var preview = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32); preview.Render(canvas);
        Require(!Pixels(preview).SequenceEqual(Pixels(baseline))
            && HasColorNear(preview, canvas.WorldToView(new(48, -4)), cyan: true),
            "hover previews the same extension that will be used by the next click");
        canvas.PreviewTraceAt(null); canvas.UpdateLayout();
        var cleared = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32); cleared.Render(canvas);
        Require(Pixels(cleared).SequenceEqual(Pixels(baseline)),
            "leaving the canvas removes the uncommitted preview without changing observations");

        // Use the actual v16 source U, whose other side is closer than the next
        // point along its current side. Distance alone used to bridge these sides.
        canvas.ResetView(); canvas.ZoomBy(4); canvas.ClearTraceContinuation();
        points = []; nextId = 0; Publish();
        var target = CalibrationTarget.Create(new(CalibrationDiscKind.Dvd, 24, 58, 2297888, 133.3),
            artwork: CalibrationArtwork.WavyBoundary);
        var map = CalibrationCrossPrediction.CreateMapping(target, target.Parameters, .37);
        double tipRadius = 24 + 34d / 12;
        CalibrationPoint TipPoint(double offset) => map(tipRadius + offset);
        var tipPoint = TipPoint(0);
        canvas.PanBy(new Point(450, 180) - canvas.WorldToView(new(tipPoint.X, tipPoint.Y)));
        foreach (double offset in new[] { 0d, -.6, -1.2, .6, 1.2, 1.8 })
        {
            var point = TipPoint(offset); Add(point.X, point.Y);
        }
        Require(points.Select(p => p.Index).SequenceEqual(new[] { 5, 4, 3, 0, 1, 2 }),
            "the actual v16 tip extends along both branches instead of bridging the closer opposite side");
    }

    private static void TestManualRingEditing()
    {
        var edits = new List<CalibrationRingEdit>();
        var canvas = CreateCanvas();
        canvas.Image = Bitmap(840, 840);
        canvas.IsPhotoStep = false;
        canvas.IsCoarseMode = true;
        canvas.PhysicalRadiusMm = 40;
        canvas.RingArcs = [new(0, 30, false), new(1, 35, false)];
        canvas.RingEditedCommand = new EditCommand<CalibrationRingEdit>(edits.Add);
        canvas.TraceEditedCommand = new EditCommand<CalibrationTraceEdit>(_ => throw new InvalidOperationException("Unexpected cross edit"));
        canvas.ZoomBy(1.2);
        canvas.PanBy(new Vector(19, -4));
        canvas.RingAngleDegrees = 90;
        Point At(double x, double y) => canvas.WorldToView(new Point(x, y));
        Require(edits.Count == 0 && !canvas.BeginDragAt(At(30, 0)) && !canvas.AddTraceAt(At(0, -30)),
            "guide placement and rotation never measure a ring or add a cross point");

        Require(canvas.BeginDragAt(At(0, -30.2)), "the opposite arc can be grabbed slightly off its stroke");
        canvas.DragTo(At(0, -31));
        Require(edits.Count == 0, "drag previews do not commit measurements until release");
        canvas.CompleteDrag();
        Require(edits.Count == 1 && edits[0] is { RingIndex: 0, RadiusMm: double radius } && Math.Abs(radius - 30.8) < 1e-8,
            "a radial drag uses millimetres and preserves its grab offset under zoom and pan");
        canvas.RingArcs = [new(0, 30.8, true), new(1, 35, false)];
        canvas.RemoveSelectedRing();
        Require(edits[^1] == new CalibrationRingEdit(0, null), "clearing a measured guide addresses its stable ring index");

        Require(canvas.BeginDragAt(At(0, 35)), "a nominal guide can be confirmed without moving it");
        canvas.CompleteDrag();
        Require(edits[^1] == new CalibrationRingEdit(1, 35), "an explicit click records the nominal radius");
        canvas.RingArcs = [new(0, 30, false), new(1, 35, true)];
        canvas.BeginDragAt(At(0, 35));
        canvas.DragTo(At(0, 49));
        canvas.CompleteDrag();
        Require(edits[^1] == new CalibrationRingEdit(1, 40), "drag cannot exceed the physical mini disc radius");

        int count = edits.Count;
        canvas.BeginDragAt(At(0, 30)); canvas.DragTo(At(0, 33)); canvas.CompleteDrag(false);
        canvas.BeginDragAt(At(0, 30)); canvas.DragTo(At(0, 33)); canvas.IsTargetPreview = true;
        Require(edits.Count == count && !canvas.BeginDragAt(At(0, 30)), "Escape and target preview discard pending ring edits");
        canvas.IsTargetPreview = false;
        canvas.BeginDragAt(At(0, 30)); canvas.DragTo(At(0, 33)); canvas.IsCoarseMode = false;
        Require(edits.Count == count && !canvas.BeginDragAt(At(0, 30)), "returning to cross tracing cancels a ring drag and hides its hit target");
    }

    private static void TestPreviewAndOpenPrediction()
    {
        var canvas = CreateCanvas();
        canvas.IsPhotoStep = false;
        canvas.Image = Bitmap(840, 840);
        canvas.PredictionContours = [[new(-30, 0), new(-10, -20), new(10, 0)]];
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        Require(HasPredictionNear(bitmap, canvas.WorldToView(new Point(-20, -10))), "full predicted curve is drawn over the corrected photo");
        Require(!HasPredictionNear(bitmap, canvas.WorldToView(new Point(-10, 0))), "predicted curve is open and never gains an invented closing segment");
        canvas.ShowPrediction = false;
        canvas.UpdateLayout();
        var hidden = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
        hidden.Render(canvas);
        Require(!HasPredictionNear(hidden, canvas.WorldToView(new Point(-20, -10))), "prediction overlay can be hidden to inspect the real photo");
        canvas.TracePoints = [new(0, 0, -30, 0), new(0, 2, -10, -20), new(0, 1, 10, 0)];
        canvas.UpdateLayout();
        var inserted = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
        inserted.Render(canvas);
        Require(HasColorNear(inserted, canvas.WorldToView(new Point(-20, -10)), cyan: true),
            "inserted samples connect in the supplied curve order, independent of stable editing IDs");
        Require(!HasColorNear(inserted, canvas.WorldToView(new Point(-10, 0)), cyan: true),
            "stable IDs do not reconnect an inserted sample in its original creation order");
        canvas.Image = null;
        canvas.UpdateLayout();
        new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32).Render(canvas);
        Require(!canvas.BeginDragAt(new Point(450, 350)), "the no-photo concentric-cross placeholder is a read-only preview");
    }

    private static void TestThreePointRingEditing()
    {
        var canvas = CreateCanvas();
        canvas.Image = Bitmap(840, 840);
        canvas.IsPhotoStep = false;
        canvas.IsCoarseMode = true;
        canvas.PhysicalRadiusMm = 40;
        canvas.RingArcs = [new CalibrationRingArc(0, 30, false)];
        var samples = new List<CalibrationPoint>();
        canvas.RingPointCommand = new EditCommand<CalibrationPoint>(samples.Add);
        canvas.RingEditedCommand = new EditCommand<CalibrationRingEdit>(_ => throw new Exception("A three-point click entered drag mode"));
        var a = new CalibrationPoint(-30 * Math.Cos(.2), 30 * Math.Sin(.2));
        var b = new CalibrationPoint(-30, 0);
        var c = new CalibrationPoint(a.X, -a.Y);
        Point At(CalibrationPoint p) => canvas.WorldToView(new Point(p.X, p.Y));
        Require(!canvas.AddRingPointAt(At(b)), "drag mode does not stamp points");
        canvas.ThreePointMode = true;
        Require(!canvas.AddRingPointAt(At(b)), "ring sampling requires a selected ring");
        canvas.SelectedRingIndex = 0;
        Require(!canvas.AddRingPointAt(At(new(41, 0))) && !canvas.AddRingPointAt(At(new(7, 0))),
            "clicks outside a mini disc or within the centre hole never become samples");
        Require(!canvas.BeginDragAt(At(b)) && !canvas.AddTraceAt(At(b)), "an inner-edge click is independent of guides and crosses");
        canvas.ZoomBy(1.2); canvas.PanBy(new Vector(18, 2));
        foreach (var sample in new[] { a, b, c })
        {
            Require(canvas.AddRingPointAt(At(sample)), "same-side marks in the corrected image are accepted");
            Near(new Point(sample.X, sample.Y), new Point(samples[^1].X, samples[^1].Y), "points preserve rectified millimetres");
        }
        canvas.RingPointMarkers = samples;
        canvas.RingArcs = [new CalibrationRingArc(0, 30, true, samples)];
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var insideArc = At(new(-30 * Math.Cos(.1), 30 * Math.Sin(.1)));
        Require(HasColorNear(bitmap, insideArc, cyan: true) && !HasColorNear(bitmap, At(new(30, 0)), cyan: true),
            "measured inner edge is drawn across the marked arc rather than the opposite side of the disc");
        canvas.IsTargetPreview = true;
        Require(!canvas.AddRingPointAt(At(b)), "source preview cannot receive inner-edge points");
        canvas.IsTargetPreview = false; canvas.IsPhotoStep = true; canvas.IsRectificationPreview = true;
        Require(!canvas.AddRingPointAt(At(b)), "unconfirmed rectification cannot receive inner-edge points");
    }

    private static void TestRadialArcLengthEditing()
    {
        var canvas = CreateCanvas();
        canvas.Image = Bitmap(840, 840);
        canvas.IsPhotoStep = false;
        canvas.IsCoarseMode = true;
        canvas.HasRadialArcTarget = true;
        var points = new List<CalibrationPoint>();
        canvas.ArcLengthPointCommand = new EditCommand<CalibrationPoint>(points.Add);
        canvas.RingArcs = [new(0, 30, false)];
        canvas.RingEditedCommand = new EditCommand<CalibrationRingEdit>(_ => throw new Exception("Unexpected ring edit"));
        canvas.TraceEditedCommand = new EditCommand<CalibrationTraceEdit>(_ => throw new Exception("Unexpected cross edit"));

        CalibrationPoint AtAngle(double degrees)
        {
            double angle = degrees * Math.PI / 180;
            return new(30 * Math.Cos(angle), 30 * Math.Sin(angle));
        }
        Point OnCanvas(CalibrationPoint p) => canvas.WorldToView(new Point(p.X, p.Y));
        Require(!canvas.AddTraceAt(OnCanvas(AtAngle(0))) && !canvas.BeginDragAt(OnCanvas(AtAngle(0))),
            "arc target does not accept a cross or draggable full-ring guide");
        canvas.ZoomBy(1.5);
        canvas.PanBy(new Vector(20, -9));
        foreach (var point in new[] { AtAngle(350), AtAngle(70), AtAngle(220) })
        {
            Require(canvas.AddArcLengthPointAt(OnCanvas(point)), "each endpoint and intermediate arc click is accepted");
            Near(new Point(point.X, point.Y), new Point(points[^1].X, points[^1].Y), "arc points use rectified millimetres");
        }
        Require(!canvas.AddArcLengthPointAt(canvas.WorldToView(new Point(61, 0)))
            && !canvas.AddArcLengthPointAt(canvas.WorldToView(new Point(7, 0))),
            "arc points outside the disc or inside the hole are rejected");
        canvas.ArcLengthPoints = points;
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        Require(HasColorNear(bitmap, OnCanvas(AtAngle(120)), cyan: true)
            && !HasColorNear(bitmap, OnCanvas(AtAngle(290)), cyan: true),
            "the intermediate point selects the major arc across zero rather than the shorter opposite arc");
        canvas.IsTargetPreview = true;
        Require(!canvas.AddArcLengthPointAt(OnCanvas(AtAngle(0))), "the target preview cannot receive arc marks");
        canvas.IsTargetPreview = false;
        canvas.IsCoarseMode = false;
        Require(!canvas.AddArcLengthPointAt(OnCanvas(AtAngle(0))) && !canvas.AddTraceAt(OnCanvas(AtAngle(0))),
            "a coarse-only arc target does not receive marks in fine mode");
        canvas.IsCoarseMode = true;
        canvas.IsPhotoStep = true;
        Require(!canvas.AddArcLengthPointAt(OnCanvas(AtAngle(0))), "photo rectification cannot receive arc marks");
    }

    private static CalibrationPhotoCanvas CreateCanvas()
    {
        var canvas = new CalibrationPhotoCanvas { CanvasRadiusMm = 63 };
        canvas.Measure(new Size(900, 700));
        canvas.Arrange(new Rect(0, 0, 900, 700));
        canvas.UpdateLayout();
        return canvas;
    }
    private static BitmapSource Bitmap(int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, new byte[width * height], width);
        bitmap.Freeze();
        return bitmap;
    }
    private static Point Position(CalibrationTracePoint point) => new(point.XMm, point.YMm);
    private static bool HasPredictionNear(BitmapSource bitmap, Point point) => HasColorNear(bitmap, point, cyan: false);
    private static bool HasColorNear(BitmapSource bitmap, Point point, bool cyan)
    {
        int x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y);
        for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
        {
            if (x + dx < 0 || y + dy < 0 || x + dx >= bitmap.PixelWidth || y + dy >= bitmap.PixelHeight) continue;
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x + dx, y + dy, 1, 1), pixel, 4, 0);
            if (cyan ? pixel[0] > 180 && pixel[1] > 180 && pixel[2] < 100 : pixel[0] < 90 && pixel[1] > 80 && pixel[1] < 210 && pixel[2] > 180) return true;
        }
        return false;
    }
    private static void Near(Point expected, Point actual, string message) => Require((expected - actual).Length < 1e-8, message);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Calibration photo canvas: {message}");
    }
    private sealed class EditCommand<T>(Action<T> edit) : ICommand
    {
        public bool CanExecute(object? parameter) => parameter is T;
        public void Execute(object? parameter) => edit((T)parameter!);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
