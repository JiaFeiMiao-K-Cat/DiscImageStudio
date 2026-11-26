using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace DiscImageStudio.Services;

/// <summary>Where album information comes from. MusicBrainz is asked first; Apple's public
/// iTunes Search catalog is the alternative the user can switch to.</summary>
public enum AlbumSource
{
    MusicBrainz,
    Apple,
}

/// <summary>An Apple storefront. The China mainland store is not offered: its public search
/// returns no albums or songs, and its lookups return no tracks.</summary>
public enum AppleStore
{
    HongKong,
    Japan,
    UnitedStates,
}

/// <summary>One album in a search result list; its tracks are read only once it is picked.</summary>
public sealed record AlbumCandidate(
    AlbumSource Source,
    string Id,
    string Title,
    string Artist,
    string? Year,
    int? TrackCount,
    string? Note,
    Uri? Cover);

public sealed record AlbumTrackInfo(int Number, string Title, string? Artist);

/// <summary>An album with its track list, already in the most English/Latin form the source
/// has: a Latin-script edition's tracklist and English artist aliases on MusicBrainz, the
/// storefront's text on Apple.</summary>
public sealed record AlbumDetails(
    AlbumSource Source,
    string Title,
    string Artist,
    IReadOnlyList<AlbumTrackInfo> Tracks,
    string? Note);

/// <summary>A failure worded for the window; <see cref="Retryable"/> marks busy services
/// and network trouble.</summary>
public sealed class AlbumLookupException(string message, bool retryable = false, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>A plain HTTP response for the lookups. Tests replace <see cref="OnlineHttp.Send"/>
/// with canned responses, so no test touches the network.</summary>
public sealed record HttpReply(HttpStatusCode Status, string Body);

/// <summary>The one HTTP path for album lookup. Every call here is the result of a search
/// the user confirmed in the CD-Extra window.</summary>
public static class OnlineHttp
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>"DiscImageStudio/&lt;version&gt; (+project page)": MusicBrainz asks every client to
    /// name itself and give a contact URL; no user information is sent.</summary>
    public static string UserAgent { get; } =
        $"DiscImageStudio/{typeof(OnlineHttp).Assembly.GetName().Version?.ToString(3) ?? "0"} "
        + "( https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio )";

    /// <summary>Test seam.</summary>
    public static Func<HttpRequestMessage, CancellationToken, Task<HttpReply>> Send { get; set; } = SendAsync;

    private static async Task<HttpReply> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new HttpReply(response.StatusCode, body);
    }

    internal static async Task<HttpReply> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            return await Send(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new AlbumLookupException("无法连接网络：" + exception.Message, retryable: true, exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AlbumLookupException("网络请求超时。", retryable: true, exception);
        }
    }

    internal static string Query(params (string Key, string? Value)[] parts)
        => string.Join("&", parts.Where(part => part.Value is not null)
            .Select(part => part.Key + "=" + Uri.EscapeDataString(part.Value!)));

    internal static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim() : null;

    internal static int? Number(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;

    internal static IEnumerable<JsonElement> Array(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    internal static string? Year(string? date)
        => date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? date[..4] : null;
}
