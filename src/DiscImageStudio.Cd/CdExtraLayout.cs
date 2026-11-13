namespace DiscImageStudio.Cd;

/// <summary>Raised when the music leaves no room for the picture session.</summary>
public sealed class CdExtraCapacityException(string message) : ArgumentException(message);

/// <summary>One audio track of the first session, in absolute LBAs (LBA 0 = 00:02:00).</summary>
public sealed record CdExtraAudioTrackLayout(int Number, long StartLba, long Sectors)
{
    public long EndLbaExclusive => StartLba + Sectors;
}

/// <summary>
/// Enhanced CD (Blue Book, "CD-Extra") layout written in Track-at-Once mode: session 1
/// holds the audio tracks, session 2 one Mode 1 data track that carries the picture.
/// Audio CD players read only the first session's table of contents and stop at its
/// lead-out, so the picture session is never played as audio.
/// </summary>
/// <remarks>
/// Addresses follow MMC TAO recording: the first track starts at LBA 0 behind its drive
/// written pre-gap; every later track follows two run-out blocks and a 150-block
/// pause; the first session's lead-out starts two run-out blocks after the last track.
/// The next session's program area begins 11,400 blocks later (6,750 lead-out +
/// 4,500 lead-in + 150 pre-gap, see cdrtools README.multi and ECMA-394). The drive
/// reports the real next writable address once session 1 is closed; the picture must
/// be generated from that measured address, because one sector of error rotates it by
/// 16–37°. A prediction is used only for design previews.
/// </remarks>
public sealed record CdExtraLayout(
    CdDiscParameters Parameters,
    IReadOnlyList<CdExtraAudioTrackLayout> Tracks,
    long MusicEndLbaExclusive,
    long Session1LeadoutLba,
    long Session2StartLba,
    bool Session2StartMeasured,
    long DataEndLbaExclusive)
{
    public const int MinimumTrackSectors = 300;
    public const int MaximumAudioTracks = 98;
    public const int TrackPauseSectors = 150;
    public const int RunOutSectors = 2;
    public const int FirstLeadoutSectors = 6750;
    public const int NextLeadinSectors = 4500;
    public const int DataPregapSectors = 150;
    public const int SessionGapSectors = FirstLeadoutSectors + NextLeadinSectors + DataPregapSectors;
    public const int DataPostgapSectors = 150;

    /// <summary>The data track must hold the ISO volume and its post-gap.</summary>
    public const int MinimumDataTrackSectors = MinimumTrackSectors + DataPostgapSectors;

    /// <summary>No session 2 can start earlier: one four-second track, the first session's
    /// lead-out and the next session's lead-in come first, whatever mode wrote them.</summary>
    public const int MinimumSession2StartLba = MinimumTrackSectors + FirstLeadoutSectors + NextLeadinSectors;

    public long DataTrackSectors => DataEndLbaExclusive - Session2StartLba;

    /// <summary>Sectors recorded for the music, including track padding and pauses.</summary>
    public long MusicSectors => MusicEndLbaExclusive;

    public double MusicInnerRadiusMm => RadiusAtLba(0);
    public double MusicOuterRadiusMm => RadiusAtLba(MusicEndLbaExclusive);
    public double PictureInnerRadiusMm => RadiusAtLba(Session2StartLba);
    public double PictureOuterRadiusMm => RadiusAtLba(DataEndLbaExclusive);

    /// <summary>Area shares of the program area from LBA 0 to the end of the data track.
    /// Under constant linear velocity they equal the sector shares for a constant pitch.</summary>
    public double MusicAreaFraction => AreaFraction(0, MusicEndLbaExclusive);
    public double SessionGapAreaFraction => AreaFraction(MusicEndLbaExclusive, Session2StartLba);
    public double PictureAreaFraction => AreaFraction(Session2StartLba, DataEndLbaExclusive);

    public double RadiusAtLba(long lba)
        => Parameters.RadiusFromByte(checked(Math.Max(0, lba) * CdDiscParameters.BytesPerSector));

    /// <summary>Reads a measured session 2 start: the LBA itself ("34506" or, as the app
    /// prints it, "34,506"), or the whole "first,second" output of cdrecord -msinfo, whose
    /// second number is that start. A session 2 start is never below
    /// <see cref="MinimumSession2StartLba"/>, so it is never a single three-digit group and the
    /// two forms cannot be confused; a smaller result is a typing error and is refused.</summary>
    public static bool TryParseSession2Start(string? text, out long lba)
    {
        lba = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{1,3}(,\d{3})+$"))
        {
            int comma = value.LastIndexOf(',');
            if (comma >= 0) value = value[(comma + 1)..].Trim();
        }
        return long.TryParse(value, System.Globalization.NumberStyles.AllowThousands,
                System.Globalization.CultureInfo.InvariantCulture, out lba)
            && lba >= MinimumSession2StartLba;
    }

    /// <summary>Predicted address of session 2 for a list of recorded track lengths.</summary>
    public static long PredictSession2StartLba(IReadOnlyList<long> trackSectors)
    {
        ValidateTracks(trackSectors);
        long end = MusicEndFor(trackSectors);
        return checked(end + RunOutSectors + SessionGapSectors);
    }

    /// <param name="measuredSession2StartLba">The next writable address the drive reported after
    /// session 1 was closed; null predicts it from the TAO layout.</param>
    /// <param name="dataTrackSectorLimit">Free blocks the drive reported for the data track.</param>
    public static CdExtraLayout Plan(CdDiscParameters parameters, IReadOnlyList<long> trackSectors,
        long? measuredSession2StartLba = null, long? dataTrackSectorLimit = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        ValidateTracks(trackSectors);
        List<CdExtraAudioTrackLayout> tracks = new(trackSectors.Count);
        long start = 0;
        for (int index = 0; index < trackSectors.Count; index++)
        {
            tracks.Add(new CdExtraAudioTrackLayout(index + 1, start, trackSectors[index]));
            start = checked(start + trackSectors[index] + RunOutSectors + TrackPauseSectors);
        }

        long musicEnd = tracks[^1].EndLbaExclusive;
        long leadout = checked(musicEnd + RunOutSectors);
        long predicted = checked(leadout + SessionGapSectors);
        long session2 = measuredSession2StartLba ?? predicted;
        if (session2 <= leadout)
            throw new ArgumentOutOfRangeException(nameof(measuredSession2StartLba),
                $"Session 2 cannot start at LBA {session2}, inside the first session ending at {leadout}.");
        // Whatever mode wrote session 1 (TAO, SAO), its program area holds every track before
        // the lead-out and the next lead-in; an earlier measured start is a typing error.
        long earliest = checked(trackSectors.Sum() + FirstLeadoutSectors + NextLeadinSectors);
        if (session2 < earliest)
            throw new ArgumentOutOfRangeException(nameof(measuredSession2StartLba),
                $"Session 2 cannot start at LBA {session2}: the music session needs at least LBA {earliest}.");

        long dataEnd = Math.Min(parameters.OutputSectors, parameters.Sectors - RunOutSectors);
        if (dataTrackSectorLimit is { } limit)
        {
            if (limit < 0) throw new ArgumentOutOfRangeException(nameof(dataTrackSectorLimit));
            dataEnd = Math.Min(dataEnd, checked(session2 + limit));
        }

        long available = dataEnd - session2;
        if (available < MinimumDataTrackSectors)
        {
            throw new CdExtraCapacityException(
                $"The music needs {musicEnd} sectors and session 2 starts at LBA {session2}, leaving "
                + $"{Math.Max(0, available)} sectors for the picture track; at least {MinimumDataTrackSectors} are required.");
        }

        return new CdExtraLayout(parameters, tracks.AsReadOnly(), musicEnd, leadout, session2,
            measuredSession2StartLba.HasValue, dataEnd);
    }

    private static long MusicEndFor(IReadOnlyList<long> trackSectors)
    {
        long end = 0;
        for (int index = 0; index < trackSectors.Count; index++)
            end = checked(end + trackSectors[index] + (index == 0 ? 0 : RunOutSectors + TrackPauseSectors));
        return end;
    }

    private static void ValidateTracks(IReadOnlyList<long> trackSectors)
    {
        ArgumentNullException.ThrowIfNull(trackSectors);
        if (trackSectors.Count is 0 or > MaximumAudioTracks)
            throw new ArgumentOutOfRangeException(nameof(trackSectors),
                $"A CD-Extra disc needs 1..{MaximumAudioTracks} audio tracks.");
        foreach (long sectors in trackSectors)
        {
            if (sectors < MinimumTrackSectors)
                throw new ArgumentOutOfRangeException(nameof(trackSectors),
                    $"Each audio track needs at least {MinimumTrackSectors} sectors (4 s).");
        }
    }

    private double AreaFraction(long startLba, long endLbaExclusive)
    {
        double inner = RadiusAtLba(0);
        double outer = RadiusAtLba(DataEndLbaExclusive);
        double total = outer * outer - inner * inner;
        if (total <= 0) return 0;
        double a = RadiusAtLba(startLba);
        double b = RadiusAtLba(endLbaExclusive);
        return Math.Clamp((b * b - a * a) / total, 0, 1);
    }
}
