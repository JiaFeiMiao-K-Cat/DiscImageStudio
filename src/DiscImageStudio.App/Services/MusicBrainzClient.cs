using System.Net;
using System.Text.Json;

namespace DiscImageStudio.Services;

/// <summary>
/// MusicBrainz (musicbrainz.org/ws/2, CC0 core data). A search lists release groups; reading
/// one picks its Latin-script edition when there is one (an official romanized release or a
/// community "pseudo-release" with a transliterated or translated tracklist) and replaces
/// artist names with their primary English alias. MusicBrainz allows one request per
/// second: calls are spaced and a busy reply (503) is retried a few times.
/// </summary>
public sealed class MusicBrainzClient
{
    internal const string Root = "https://musicbrainz.org/ws/2/";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private readonly Dictionary<string, string> _englishNames = new(StringComparer.Ordinal);

    /// <summary>Test seam: the pause between requests and before a retry.</summary>
    internal static TimeSpan Spacing { get; set; } = TimeSpan.FromMilliseconds(1100);

    internal static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public async Task<IReadOnlyList<AlbumCandidate>> SearchAsync(string? album, string? artist,
        CancellationToken cancellationToken = default)
    {
        string query = BuildQuery(album, artist);
        using JsonDocument document = await GetAsync("release-group/?" + OnlineHttp.Query(
            ("query", query), ("limit", "12"), ("fmt", "json")), cancellationToken).ConfigureAwait(false);
        List<AlbumCandidate> candidates = [];
        foreach (JsonElement group in OnlineHttp.Array(document.RootElement, "release-groups"))
        {
            string? id = OnlineHttp.Text(group, "id");
            string? title = OnlineHttp.Text(group, "title");
            if (id is null || title is null) continue;
            string type = OnlineHttp.Text(group, "primary-type") ?? "";
            int releases = OnlineHttp.Number(group, "count") ?? OnlineHttp.Array(group, "releases").Count();
            candidates.Add(new AlbumCandidate(AlbumSource.MusicBrainz, id, title, Credit(group),
                OnlineHttp.Year(OnlineHttp.Text(group, "first-release-date")), null,
                string.Join(" · ", new[] { type, releases > 0 ? $"{releases} 个版本" : "" }.Where(part => part.Length > 0)),
                new Uri($"https://coverartarchive.org/release-group/{id}/front-250")));
        }
        return candidates;
    }

    /// <summary>The tracklist of the group's Latin-script edition when it has one, otherwise
    /// of its first official release, with English artist aliases.</summary>
    public async Task<AlbumDetails> ReadAsync(AlbumCandidate candidate, CancellationToken cancellationToken = default)
    {
        using JsonDocument releases = await GetAsync("release?" + OnlineHttp.Query(
            ("release-group", candidate.Id), ("limit", "100"), ("fmt", "json")), cancellationToken).ConfigureAwait(false);
        JsonElement[] all = OnlineHttp.Array(releases.RootElement, "releases").ToArray();
        if (all.Length == 0) throw new AlbumLookupException("这个专辑在 MusicBrainz 上没有发行版本。");
        JsonElement chosen = PickRelease(all, out bool latin);
        string releaseId = OnlineHttp.Text(chosen, "id")!;

        using JsonDocument release = await GetAsync($"release/{releaseId}?" + OnlineHttp.Query(
            ("inc", "recordings+artist-credits"), ("fmt", "json")), cancellationToken).ConfigureAwait(false);
        JsonElement root = release.RootElement;
        string albumArtist = await EnglishCreditAsync(root, cancellationToken).ConfigureAwait(false);
        List<AlbumTrackInfo> tracks = [];
        int number = 1;
        foreach (JsonElement medium in OnlineHttp.Array(root, "media"))
        {
            foreach (JsonElement track in OnlineHttp.Array(medium, "tracks"))
            {
                string title = OnlineHttp.Text(track, "title") ?? $"Track {number}";
                string? artist = OnlineHttp.Array(track, "artist-credit").Any()
                    ? await EnglishCreditAsync(track, cancellationToken).ConfigureAwait(false)
                    : null;
                tracks.Add(new AlbumTrackInfo(number++, title, artist ?? albumArtist));
            }
        }
        string note = latin ? "使用拉丁字母版曲目表（" + (OnlineHttp.Text(chosen, "status") ?? "Official") + "）"
            : "MusicBrainz 上没有拉丁字母版曲目表";
        return new AlbumDetails(AlbumSource.MusicBrainz, OnlineHttp.Text(root, "title") ?? candidate.Title,
            albumArtist, tracks, note);
    }

    /// <summary>Latin-script pseudo-release first (a romanized or translated tracklist), then
    /// any Latin-script release, then the first official one.</summary>
    internal static JsonElement PickRelease(JsonElement[] releases, out bool latin)
    {
        JsonElement? pseudo = null, anyLatin = null, official = null;
        foreach (JsonElement release in releases)
        {
            string? script = release.TryGetProperty("text-representation", out JsonElement text) ? OnlineHttp.Text(text, "script") : null;
            string? status = OnlineHttp.Text(release, "status");
            if (script == "Latn" && status == "Pseudo-Release") pseudo ??= release;
            if (script == "Latn") anyLatin ??= release;
            if (status == "Official") official ??= release;
        }
        latin = pseudo is not null || anyLatin is not null;
        return pseudo ?? anyLatin ?? official ?? releases[0];
    }

    internal static string BuildQuery(string? album, string? artist)
    {
        static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(album)) parts.Add("releasegroup:" + Quote(album.Trim()));
        if (!string.IsNullOrWhiteSpace(artist)) parts.Add("artist:" + Quote(artist.Trim()));
        if (parts.Count == 0) throw new AlbumLookupException("请填写专辑名或艺术家。");
        return string.Join(" AND ", parts);
    }

    private static string Credit(JsonElement owner)
        => string.Concat(OnlineHttp.Array(owner, "artist-credit").Select(credit =>
            (OnlineHttp.Text(credit, "name") ?? (credit.TryGetProperty("artist", out JsonElement artist) ? OnlineHttp.Text(artist, "name") : null) ?? "")
            + (credit.TryGetProperty("joinphrase", out JsonElement join) && join.ValueKind == JsonValueKind.String ? join.GetString() : "")));

    /// <summary>The credit with each artist's primary English alias where one exists.</summary>
    private async Task<string> EnglishCreditAsync(JsonElement owner, CancellationToken cancellationToken)
    {
        System.Text.StringBuilder text = new();
        foreach (JsonElement credit in OnlineHttp.Array(owner, "artist-credit"))
        {
            string name = OnlineHttp.Text(credit, "name") ?? "";
            if (credit.TryGetProperty("artist", out JsonElement artist) && OnlineHttp.Text(artist, "id") is { } id)
                name = await EnglishNameAsync(id, OnlineHttp.Text(artist, "name") ?? name, cancellationToken).ConfigureAwait(false);
            text.Append(name);
            if (credit.TryGetProperty("joinphrase", out JsonElement join) && join.ValueKind == JsonValueKind.String)
                text.Append(join.GetString());
        }
        return text.ToString().Trim();
    }

    private async Task<string> EnglishNameAsync(string id, string name, CancellationToken cancellationToken)
    {
        if (_englishNames.TryGetValue(id, out string? known)) return known;
        string result = name;
        if (!IsLatin(name))
        {
            using JsonDocument artist = await GetAsync($"artist/{id}?" + OnlineHttp.Query(("inc", "aliases"), ("fmt", "json")),
                cancellationToken).ConfigureAwait(false);
            JsonElement[] aliases = OnlineHttp.Array(artist.RootElement, "aliases").ToArray();
            static bool English(JsonElement alias) => (OnlineHttp.Text(alias, "locale") ?? "").StartsWith("en", StringComparison.Ordinal);
            static bool Primary(JsonElement alias) => alias.TryGetProperty("primary", out JsonElement value) && value.ValueKind == JsonValueKind.True;
            result = aliases.Where(alias => English(alias) && Primary(alias)).Select(alias => OnlineHttp.Text(alias, "name")).FirstOrDefault(n => n is not null)
                ?? aliases.Where(English).Select(alias => OnlineHttp.Text(alias, "name")).FirstOrDefault(n => n is not null)
                ?? name;
        }
        _englishNames[id] = result;
        return result;
    }

    internal static bool IsLatin(string text) => text.All(c => c < 0x0250 || char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c));

    private static async Task<JsonDocument> GetAsync(string relative, CancellationToken cancellationToken)
    {
        Uri uri = new(Root + relative);
        for (int attempt = 0; ; attempt++)
        {
            HttpReply reply;
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                TimeSpan wait = _lastRequestUtc + Spacing - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Delay(wait, cancellationToken).ConfigureAwait(false);
                reply = await OnlineHttp.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                _lastRequestUtc = DateTime.UtcNow;
            }
            finally
            {
                Gate.Release();
            }

            if (reply.Status == HttpStatusCode.OK)
            {
                try { return JsonDocument.Parse(reply.Body); }
                catch (JsonException exception) { throw new AlbumLookupException("MusicBrainz 返回的数据无法识别。", false, exception); }
            }
            if (reply.Status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests && attempt < 4)
            {
                await Delay(TimeSpan.FromTicks(Spacing.Ticks * (attempt + 2)), cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (reply.Status == HttpStatusCode.NotFound) throw new AlbumLookupException("MusicBrainz 上没有找到这个条目。");
            throw new AlbumLookupException(reply.Status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests
                ? "MusicBrainz 正忙，请稍后再试。" : $"MusicBrainz 返回错误 {(int)reply.Status}。",
                retryable: reply.Status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests);
        }
    }
}
