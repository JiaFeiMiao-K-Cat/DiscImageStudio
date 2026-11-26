using System.Net;
using System.Text.Json;

namespace DiscImageStudio.Services;

/// <summary>
/// Apple's public iTunes Search catalog (the Apple Music catalog, no key needed), the
/// alternative to MusicBrainz. It returns each storefront's own text: the US store gives some
/// artists their English names, but Apple does not translate song titles.
/// </summary>
public sealed class AppleMusicClient
{
    internal const string Root = "https://itunes.apple.com/";

    public static string StoreCode(AppleStore store) => store switch
    {
        AppleStore.HongKong => "hk",
        AppleStore.Japan => "jp",
        _ => "us",
    };

    public static string StoreName(AppleStore store) => store switch
    {
        AppleStore.HongKong => "香港区",
        AppleStore.Japan => "日本区",
        _ => "美国区",
    };

    public async Task<IReadOnlyList<AlbumCandidate>> SearchAsync(string? album, string? artist, AppleStore store,
        CancellationToken cancellationToken = default)
    {
        string term = string.Join(" ", new[] { artist, album }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        if (term.Length == 0) throw new AlbumLookupException("请填写专辑名或艺术家。");
        using JsonDocument document = await GetAsync("search?" + OnlineHttp.Query(("term", term), ("country", StoreCode(store)),
            ("media", "music"), ("entity", "album"), ("limit", "15")), cancellationToken).ConfigureAwait(false);
        List<AlbumCandidate> candidates = [];
        foreach (JsonElement album1 in OnlineHttp.Array(document.RootElement, "results"))
        {
            int? id = OnlineHttp.Number(album1, "collectionId");
            string? title = OnlineHttp.Text(album1, "collectionName");
            if (id is null || title is null) continue;
            string? art = OnlineHttp.Text(album1, "artworkUrl100");
            candidates.Add(new AlbumCandidate(AlbumSource.Apple, id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                title, OnlineHttp.Text(album1, "artistName") ?? "", OnlineHttp.Year(OnlineHttp.Text(album1, "releaseDate")),
                OnlineHttp.Number(album1, "trackCount"), "Apple " + StoreName(store),
                art is null ? null : new Uri(art)));
        }
        return candidates;
    }

    public async Task<AlbumDetails> ReadAsync(AlbumCandidate candidate, AppleStore store, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await GetAsync("lookup?" + OnlineHttp.Query(("id", candidate.Id), ("entity", "song"),
            ("country", StoreCode(store)), ("limit", "200")), cancellationToken).ConfigureAwait(false);
        JsonElement[] results = OnlineHttp.Array(document.RootElement, "results").ToArray();
        JsonElement? collection = results.Where(result => OnlineHttp.Text(result, "wrapperType") == "collection")
            .Select(result => (JsonElement?)result).FirstOrDefault();
        var songs = results.Where(result => OnlineHttp.Text(result, "wrapperType") == "track")
            .OrderBy(song => OnlineHttp.Number(song, "discNumber") ?? 1)
            .ThenBy(song => OnlineHttp.Number(song, "trackNumber") ?? 0)
            .ToArray();
        if (songs.Length == 0)
            throw new AlbumLookupException($"Apple {StoreName(store)}没有返回这张专辑的曲目，可换个区域再试。");
        string albumArtist = collection is { } c ? OnlineHttp.Text(c, "artistName") ?? candidate.Artist : candidate.Artist;
        List<AlbumTrackInfo> tracks = songs.Select((song, index) => new AlbumTrackInfo(index + 1,
            OnlineHttp.Text(song, "trackName") ?? $"Track {index + 1}", OnlineHttp.Text(song, "artistName") ?? albumArtist)).ToList();
        return new AlbumDetails(AlbumSource.Apple, collection is { } album ? OnlineHttp.Text(album, "collectionName") ?? candidate.Title : candidate.Title,
            albumArtist, tracks, "Apple " + StoreName(store) + "（Apple 不翻译曲名）");
    }

    private static async Task<JsonDocument> GetAsync(string relative, CancellationToken cancellationToken)
    {
        HttpReply reply = await OnlineHttp.GetAsync(new Uri(Root + relative), cancellationToken).ConfigureAwait(false);
        if (reply.Status != HttpStatusCode.OK)
        {
            throw new AlbumLookupException((int)reply.Status is 403 or 429
                ? "Apple 暂时限制了查询次数，请稍后再试。" : $"Apple 返回错误 {(int)reply.Status}。",
                retryable: (int)reply.Status is 403 or 429 or 503);
        }
        try { return JsonDocument.Parse(reply.Body); }
        catch (JsonException exception) { throw new AlbumLookupException("Apple 返回的数据无法识别。", false, exception); }
    }
}
