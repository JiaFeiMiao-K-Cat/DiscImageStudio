using DiscImageStudio.Controls;

/// <summary>The saved-project trail is code-drawn, so its timing lives in the control rather
/// than in a storyboard. These checks pin the two visible properties: the head runs the lap
/// once in order, and the solid segment still stretches with the Bezier speed instead of
/// staying a fixed dash.</summary>
internal static class SaveTrailTests
{
    internal static void Run()
    {
        Equal(0, ProjectSaveTrail.EaseProgress(0), "trail starts at the beginning of the lap");
        Equal(1, ProjectSaveTrail.EaseProgress(1), "trail finishes at the end of the lap");

        double previous = -1;
        for (int step = 0; step <= 100; step++)
        {
            double progress = ProjectSaveTrail.EaseProgress(step / 100.0);
            True(progress >= previous, $"trail progress is monotonic at {step}%");
            True(progress is >= 0 and <= 1, $"trail progress stays inside the lap at {step}%");
            previous = progress;
        }

        double slowStart = ProjectSaveTrail.TrailLengthAt(0, 60);
        double fastMiddle = ProjectSaveTrail.TrailLengthAt(0.5, 60);
        double slowEnd = ProjectSaveTrail.TrailLengthAt(1, 60);
        True(fastMiddle > slowStart * 2, "the segment stretches through the fast middle of the lap");
        True(fastMiddle > slowEnd * 2, "the segment contracts again as the head eases out");
        True(fastMiddle <= 60.001, "the segment never exceeds the requested trail length");
        True(slowStart >= 20.99 && slowEnd >= 20.99, "the segment keeps a readable minimum length");
    }

    private static void Equal(double expected, double actual, string name)
    {
        if (Math.Abs(expected - actual) > 1e-6)
        {
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}.");
        }
    }

    private static void True(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{name}: condition was false.");
        }
    }
}
