using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DiscImageStudio;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

/// <summary>
/// 11.1 CD-Extra window CD-TEXT: the window's own per-track table (source choice, edits,
/// package text, per-field encoding warnings), MusicBrainz-first lookup with Apple as the
/// alternative, the in-app confirmations, and the window wiring. Every HTTP exchange is canned through
/// <see cref="OnlineHttp.Send"/>: no test touches the network.
/// </summary>
internal static class AlbumInfoTests
{
    private static readonly List<HttpRequestMessage> Requests = [];
    private static readonly List<string?> Bodies = [];

    internal static void Run()
    {
        var originalSend = OnlineHttp.Send;
        TimeSpan originalSpacing = MusicBrainzClient.Spacing;
        var originalDelay = MusicBrainzClient.Delay;
        string scratch = Path.Combine(Path.GetTempPath(), "dis-album-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        MusicBrainzClient.Spacing = TimeSpan.Zero;
        MusicBrainzClient.Delay = (_, _) => Task.CompletedTask;
        try
        {
            TestModel(scratch);
            TestMusicBrainz();
            TestApple();
            TestEncodingIssues();
            TestNoSystemMessageBox();
            WpfTestHost.Run(() => TestWindow(scratch));
        }
        finally
        {
            OnlineHttp.Send = originalSend;
            MusicBrainzClient.Spacing = originalSpacing;
            MusicBrainzClient.Delay = originalDelay;
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
        Console.WriteLine("album-info: window CD-TEXT table and source choice, per-field encoding warnings, MusicBrainz Latin edition and English aliases, 503 retry, Apple regions, in-app confirmations (no system MessageBox in the app), lookup dialog passed");
    }

    private static AudioTags FakeTags(string path) => Path.GetFileNameWithoutExtension(path) switch
    {
        "a" => new AudioTags("晴天", "周杰伦", "叶惠美", "周杰伦"),
        "b" => new AudioTags("東京", "椎名林檎", null, null),
        _ => AudioTags.None,
    };

    private static void TestModel(string scratch)
    {
        string a = Path.Combine(scratch, "a.wav"), b = Path.Combine(scratch, "b.wav"), c = Path.Combine(scratch, "c.wav");
        ProjectCdText design = new(true, "Design Album", "Design Artist");
        Dictionary<string, CdTextTrack> designTracks = new() { [b] = new CdTextTrack("Design B", "Design Performer") };
        CdExtraCdTextModel model = new(design, designTracks, FakeTags);
        int changes = 0;
        model.Changed += () => changes++;

        model.SyncTracks([a, b, c]);
        Require(model.Source == CdExtraTextSource.Window && !model.IsEdited, "the window's own music is the default source");
        Require(model.AlbumTitle == "叶惠美" && model.AlbumPerformer == "周杰伦", "album fields come from the first tags");
        Require(model.Rows.Select(row => row.Title).SequenceEqual(["晴天", "東京", "c"]), "titles come from tags, else the file name");
        Require(model.Rows[0].Origin == CdExtraCdTextModel.OriginTags && model.Rows[2].Origin == CdExtraCdTextModel.OriginFileName,
            "each row says where its text came from");
        Require(model.HasDesignText, "the design page has text to offer");

        model.Rows[0].Title = "Sunny Day";
        Require(model.IsEdited && model.Rows[0].Origin == CdExtraCdTextModel.OriginManual && changes > 0, "typing marks the row");

        // Reordering keeps each file's text; a new file is filled, a removed one goes.
        string d = Path.Combine(scratch, "d.wav");
        model.SyncTracks([c, a, d]);
        Require(model.Rows.Select(row => row.Title).SequenceEqual(["c", "Sunny Day", "d"])
            && model.Rows.Select(row => row.Number).SequenceEqual([1, 2, 3]), "rows follow the music list");

        model.SyncTracks([a, b]);
        model.FillFrom(CdExtraTextSource.Design);
        Require(model.Source == CdExtraTextSource.Design && !model.IsEdited, "switching source refills and clears the edit flag");
        Require(model.AlbumTitle == "Design Album" && model.AlbumPerformer == "Design Artist", "design album fields");
        Require(model.Rows[0].Title == "晴天" && model.Rows[0].Origin == CdExtraCdTextModel.OriginTags,
            "a file the design page does not have keeps its tags");
        Require(model.Rows[1].Title == "Design B" && model.Rows[1].Performer == "Design Performer"
            && model.Rows[1].Origin == CdExtraCdTextModel.OriginDesign, "a design-page track is matched by path");
        Require(design.AlbumTitle == "Design Album" && designTracks[b].Title == "Design B", "the design page is only read");

        model.FillFrom(CdExtraTextSource.Window);
        Require(model.AlbumTitle == "叶惠美" && model.Rows[1].Title == "東京", "switching back restores the window's tags");

        model.ApplyLookup("Ye Hui Mei", null, [(0, "Sunny Day", "Jay Chou"), (5, "ignored", null)], "MusicBrainz");
        Require(model.AlbumTitle == "Ye Hui Mei" && model.AlbumPerformer == "周杰伦" && model.Rows[0].Performer == "Jay Chou"
            && model.Rows[0].Origin == "MusicBrainz" && model.IsEdited, "lookup fills only what it was given");

        Require(model.Rows[1].HasTitleIssue && model.Rows[1].HasPerformerIssue && !model.Rows[0].HasTitleIssue
            && model.HasAlbumPerformerIssue && !model.HasAlbumTitleIssue && model.IssueCount == 3,
            "each non-Latin field is marked");
        Require(model.WarningText.Contains("3 个字段", StringComparison.Ordinal) && model.IssueLines().Count == 3,
            "the warning counts the marked fields");
        model.ApplyLookup(null, "Jay Chou", [(1, "Tokyo", "Ringo Sheena")], "MusicBrainz");
        Require(model.IssueCount == 0 && model.WarningText.Length == 0 && model.StatusText.StartsWith("ISO-8859-1", StringComparison.Ordinal),
            "all-Latin text is written as ISO-8859-1 with no warning");

        model.Rows[1].Title = "  ";
        CdExtraPackageCdText text = model.Collect();
        Require(text.AlbumTitle == "Ye Hui Mei" && text.Tracks[1].Title == "b" && text.Tracks.Count == 2,
            "the package text uses the table, a blank title becomes the file name");
        Require(model.StatusText.StartsWith("ISO-8859-1", StringComparison.Ordinal), "the encoding is shown");

        model.LoadPackage(new CdExtraPackageCdText("Pkg", "PkgArtist", [new CdTextTrack("One", null), new CdTextTrack("Two", "X")]));
        Require(model.AlbumTitle == "Pkg" && model.Rows[1].Title == "Two" && model.Rows[1].Origin == CdExtraCdTextModel.OriginPackage,
            "an opened package's text is shown");

        CdExtraCdTextModel empty = new(new ProjectCdText(), new Dictionary<string, CdTextTrack>(), FakeTags);
        Require(!empty.HasDesignText, "an empty design page offers nothing");
        Require(CdExtraCdTextModel.Clip(new string('x', 300)).Length == ProjectCdText.MaximumFieldLength, "fields are clipped");
    }

    private static void Reply(Func<HttpRequestMessage, string?, HttpReply> handler)
    {
        Requests.Clear();
        Bodies.Clear();
        OnlineHttp.Send = async (request, _) =>
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            Requests.Add(request);
            Bodies.Add(body);
            return handler(request, body);
        };
    }

    private static HttpReply Ok(string json) => new(HttpStatusCode.OK, json);

    private static void TestMusicBrainz()
    {
        Require(MusicBrainzClient.BuildQuery("A \"B\"", " C ") == "releasegroup:\"A \\\"B\\\"\" AND artist:\"C\"", "query fields are quoted");
        Throws<AlbumLookupException>(() => MusicBrainzClient.BuildQuery(" ", null), "an empty search");

        int busy = 0;
        Reply((request, _) =>
        {
            string url = request.RequestUri!.ToString();
            if (url.Contains("release-group/?", StringComparison.Ordinal))
            {
                if (busy++ < 2) return new HttpReply(HttpStatusCode.ServiceUnavailable, "");
                return Ok("""{"release-groups":[{"id":"rg1","title":"無罪モラトリアム","primary-type":"Album","first-release-date":"1999-02-24","count":3,"artist-credit":[{"name":"椎名林檎","artist":{"id":"ar1","name":"椎名林檎"}}]}]}""");
            }
            if (url.Contains("release?release-group=rg1", StringComparison.Ordinal))
                return Ok("""{"releases":[{"id":"r-jp","status":"Official","text-representation":{"script":"Jpan"}},{"id":"r-latn","status":"Pseudo-Release","text-representation":{"script":"Latn"}}]}""");
            if (url.Contains("release/r-latn?", StringComparison.Ordinal))
                return Ok("""{"title":"Muzai Moratorium","artist-credit":[{"name":"椎名林檎","artist":{"id":"ar1","name":"椎名林檎"}}],"media":[{"tracks":[{"title":"Tadashii Machi"},{"title":"Kabukicho no Joou","artist-credit":[{"name":"椎名林檎","artist":{"id":"ar1","name":"椎名林檎"},"joinphrase":" & "},{"name":"Guest","artist":{"id":"ar2","name":"Guest"}}]}]}]}""");
            if (url.Contains("artist/ar1?", StringComparison.Ordinal))
                return Ok("""{"aliases":[{"name":"Shiina Ringo","locale":"ja-Latn","primary":true},{"name":"Ringo Sheena","locale":"en","primary":true}]}""");
            return new HttpReply(HttpStatusCode.NotFound, "");
        });
        MusicBrainzClient client = new();
        IReadOnlyList<AlbumCandidate> found = client.SearchAsync("無罪モラトリアム", "椎名林檎").GetAwaiter().GetResult();
        Require(busy == 3 && found.Count == 1 && found[0].Title == "無罪モラトリアム" && found[0].Year == "1999"
            && found[0].Note!.Contains("3 个版本", StringComparison.Ordinal), "a busy reply is retried, then the search lists the group");
        Require(Requests.All(request => request.RequestUri!.Host == "musicbrainz.org"
            && request.Headers.Accept.ToString().Contains("json", StringComparison.Ordinal)), "only musicbrainz.org is asked, for JSON");
        Require(Uri.UnescapeDataString(Requests[^1].RequestUri!.Query).Contains("releasegroup:\"無罪モラトリアム\" AND artist:\"椎名林檎\"", StringComparison.Ordinal),
            "the search sends exactly the two fields");

        AlbumDetails details = client.ReadAsync(found[0]).GetAwaiter().GetResult();
        Require(details.Title == "Muzai Moratorium" && details.Artist == "Ringo Sheena", "the Latin edition and English alias are used");
        Require(details.Tracks.Select(track => track.Title).SequenceEqual(["Tadashii Machi", "Kabukicho no Joou"])
            && details.Tracks[0].Artist == "Ringo Sheena" && details.Tracks[1].Artist == "Ringo Sheena & Guest",
            "tracks keep their own credits with English aliases");
        Require(Requests.Count(request => request.RequestUri!.AbsolutePath.Contains("artist/ar1", StringComparison.Ordinal)) == 1,
            "an artist's alias is read once");
        Require(details.Note!.Contains("拉丁字母", StringComparison.Ordinal), "the note says a Latin tracklist was used");

        Reply((_, _) => new HttpReply(HttpStatusCode.ServiceUnavailable, ""));
        AlbumLookupException busyError = Throws<AlbumLookupException>(() => client.SearchAsync("x", null).GetAwaiter().GetResult(), "a busy service");
        Require(busyError.Retryable && Requests.Count == 5 && busyError.Message.Contains("正忙", StringComparison.Ordinal), "retries are limited");

        Reply((_, _) => throw new HttpRequestException("offline"));
        Require(Throws<AlbumLookupException>(() => client.SearchAsync("x", null).GetAwaiter().GetResult(), "no network").Retryable,
            "network trouble is reported");
    }

    private static void TestApple()
    {
        Reply((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/search" => Ok("""{"results":[{"collectionId":42,"collectionName":"叶惠美","artistName":"Jay Chou","releaseDate":"2003-07-31T07:00:00Z","trackCount":2,"artworkUrl100":"https://example.invalid/a.jpg"}]}"""),
            "/lookup" => Ok("""{"results":[{"wrapperType":"collection","collectionName":"叶惠美","artistName":"Jay Chou"},{"wrapperType":"track","trackName":"晴天","artistName":"Jay Chou","trackNumber":2,"discNumber":1},{"wrapperType":"track","trackName":"以父之名","artistName":"Jay Chou","trackNumber":1,"discNumber":1}]}"""),
            _ => new HttpReply(HttpStatusCode.NotFound, ""),
        });
        AppleMusicClient apple = new();
        Require(AppleMusicClient.StoreCode(AppleStore.HongKong) == "hk" && AppleMusicClient.StoreCode(AppleStore.Japan) == "jp"
            && AppleMusicClient.StoreCode(AppleStore.UnitedStates) == "us" && Enum.GetValues<AppleStore>()[0] == AppleStore.HongKong,
            "Hong Kong, Japan and US stores, Hong Kong first");
        IReadOnlyList<AlbumCandidate> found = apple.SearchAsync("叶惠美", "周杰伦", AppleStore.HongKong).GetAwaiter().GetResult();
        string query = Uri.UnescapeDataString(Requests[0].RequestUri!.Query);
        Require(Requests[0].RequestUri!.Host == "itunes.apple.com" && query.Contains("country=hk", StringComparison.Ordinal)
            && query.Contains("term=周杰伦 叶惠美", StringComparison.Ordinal) && query.Contains("entity=album", StringComparison.Ordinal),
            "the search names the store and sends the typed words");
        Require(found.Single() is { Id: "42", TrackCount: 2, Year: "2003", Source: AlbumSource.Apple }, "the album is listed");
        AlbumDetails details = apple.ReadAsync(found[0], AppleStore.Japan).GetAwaiter().GetResult();
        Require(Uri.UnescapeDataString(Requests[^1].RequestUri!.Query).Contains("country=jp", StringComparison.Ordinal), "lookup uses the store");
        Require(details.Tracks.Select(track => track.Title).SequenceEqual(["以父之名", "晴天"]) && details.Artist == "Jay Chou",
            "tracks are in disc order");

        Reply((_, _) => Ok("""{"results":[{"wrapperType":"collection","collectionName":"x"}]}"""));
        Require(Throws<AlbumLookupException>(() => apple.ReadAsync(found[0], AppleStore.HongKong).GetAwaiter().GetResult(), "no tracks")
            .Message.Contains("换个区域", StringComparison.Ordinal), "a store without tracks suggests another region");
        Reply((_, _) => new HttpReply((HttpStatusCode)429, ""));
        Require(Throws<AlbumLookupException>(() => apple.SearchAsync("x", null, AppleStore.UnitedStates).GetAwaiter().GetResult(), "rate limit")
            .Retryable, "Apple's rate limit is retryable");
    }

    private static void TestEncodingIssues()
    {
        Require(CdExtraCdTextModel.Issue("Sunny Day") is null && CdExtraCdTextModel.Issue("Café Ü") is null
            && CdExtraCdTextModel.Issue(" ") is null, "Latin-1 text has no issue");
        Require(CdExtraCdTextModel.Issue("東京")!.Contains("MS-JIS", StringComparison.Ordinal), "Japanese switches the disc to MS-JIS");
        string? chinese = CdExtraCdTextModel.Issue("我们周杰伦");
        Require(chinese!.Contains("们", StringComparison.Ordinal) && chinese.Contains("伦", StringComparison.Ordinal)
            && !chinese.Contains("周", StringComparison.Ordinal) && chinese.Contains("？", StringComparison.Ordinal),
            "characters MS-JIS lacks are named");
        Require(DiscImageStudio.Core.CdTextPacks.IsLatin1Text("Ü") && !DiscImageStudio.Core.CdTextPacks.IsLatin1Text("東")
            && DiscImageStudio.Core.CdTextPacks.MsJisUnrepresentable("東京").Count == 0, "the encoder helpers agree");
    }

    private static void TestWindow(string scratch)
    {
        string first = Path.Combine(scratch, "01 first.wav"), second = Path.Combine(scratch, "02 second.wav");
        CdExtraLayoutTests.WriteWave(first, frames: 588 * 100, seed: 3);
        CdExtraLayoutTests.WriteWave(second, frames: 588 * 100, seed: 4);
        var geometry = new CdDiscParameters(24.5, 27.5, 15_000, UsableSectors: 14_998);
        ProjectCdText design = new(true, "设计专辑", "设计艺术家");
        Dictionary<string, CdTextTrack> designTracks = new() { [first] = new CdTextTrack("设计曲名", "设计歌手") };
        var window = new CdExtraWindow(null, geometry, ToneProfile.Photo, design, designTracks) { ShowActivated = false, AnimateTransitions = false };
        try
        {
            window.Show();
            T Find<T>(string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException(name + " is missing");
            Require(Find<CheckBox>("CdTextCheck").IsChecked == false, "CD-TEXT starts unticked even when the design page has it on");
            Require(Find<RadioButton>("SourceWindowChoice").IsChecked == true && !Find<FrameworkElement>("CdTextFields").IsEnabled,
                "the window's own music is the default; the fields wait for the tick");
            foreach (string name in new[] { "LookupButton", "CdTextTrackList" }) Find<FrameworkElement>(name);

            var music = (System.Collections.ObjectModel.ObservableCollection<string>)typeof(CdExtraWindow)
                .GetField("_music", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var model = (CdExtraCdTextModel)typeof(CdExtraWindow)
                .GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            music.Add(first);
            music.Add(second);
            window.UpdateLayout();
            Require(model.Rows.Count == 2 && model.Rows[0].Title == "01 first" && model.AlbumTitle.Length == 0,
                "the table lists the window's music from its own files, not the design page");
            Require(Find<ItemsControl>("CdTextTrackList").Items.Count == 2, "the table is shown");

            Find<CheckBox>("CdTextCheck").IsChecked = true;
            Require(Find<RadioButton>("SourceDesignChoice").IsEnabled, "the design page can be chosen once CD-TEXT is ticked");
            Find<RadioButton>("SourceDesignChoice").IsChecked = true;
            Require(model.Source == CdExtraTextSource.Design && model.AlbumTitle == "设计专辑" && model.Rows[0].Title == "设计曲名"
                && model.Rows[1].Title == "02 second", "choosing the design page fills from it, matched by file");
            Find<TextBox>("CdTextAlbumBox").Text = "Edited";
            Require(model.AlbumTitle == "Edited" && model.IsEdited, "the album box edits the window's text");

            var collect = typeof(CdExtraWindow).GetMethod("CollectCdText", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var text = (CdExtraPackageCdText)collect.Invoke(window, [music.ToArray()])!;
            Require(text.AlbumTitle == "Edited" && text.Tracks.Select(track => track.Title).SequenceEqual(["设计曲名", "02 second"]),
                "the package gets exactly the table");
            Require(design.AlbumTitle == "设计专辑", "the design page is untouched");
            ScrollToCdText(window);
            Shot(window, "1-cdextra-cdtext");

            // Lookup: every search is confirmed first; a refusal sends nothing.
            var lookup = new AlbumLookupWindow(model) { Owner = window, ShowActivated = false, AnimateTransitions = false };
            try
            {
                lookup.Show();
                Require(((TextBox)lookup.FindName("AlbumBox")).Text == "Edited", "the search starts from the album box");
                Require(((ComboBox)lookup.FindName("StoreBox")).SelectedIndex == 0, "Apple defaults to the first store, Hong Kong");
                string? asked = null;
                lookup.Confirm = question => { asked = question; return false; };
                Reply((_, _) => throw new InvalidOperationException("nothing may be sent"));
                ((Button)lookup.FindName("SearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(asked is not null && asked.Contains("musicbrainz.org", StringComparison.Ordinal)
                    && asked.Contains("Edited", StringComparison.Ordinal) && Requests.Count == 0, "a refused search sends nothing");

                // Without the test hook the question is the app's own confirmation card.
                lookup.Confirm = null;
                ((Button)lookup.FindName("SearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var card = (DiscImageStudio.Controls.ConfirmModal)lookup.FindName("NetworkConfirm");
                Require(card.IsOpen && card.Root.Visibility == Visibility.Visible && card.TitleText.Text == "联网搜索专辑"
                    && card.MessageText.Text.Contains("musicbrainz.org", StringComparison.Ordinal) && Requests.Count == 0,
                    "the search waits on the in-app confirmation card");
                Shot(lookup, "2a-lookup-confirm");
                Require(card.HandleKeyDown(System.Windows.Input.Key.Escape) && Requests.Count == 0, "Escape refuses the search");

                lookup.ShowComparison(new AlbumDetails(AlbumSource.MusicBrainz, "Found Album", "Found Artist",
                    [new AlbumTrackInfo(1, "Found One", "A"), new AlbumTrackInfo(2, "Found Two", "B"), new AlbumTrackInfo(3, "Extra", null)], "note"));
                var rows = lookup.ComparisonRows;
                Require(rows.Count == 4 && rows.All(row => row.Apply), "album fields and the two window tracks are compared");
                ((ListBox)lookup.FindName("ResultList")).ItemsSource = new[] { new { Title = "Found Album", Detail = "Found Artist · 2003 · Album · 3 个版本" } };
                ((TextBlock)lookup.FindName("ResultHint")).Visibility = Visibility.Collapsed;
                Shot(lookup, "2-lookup");
                rows[0].Apply = false;
                ((CheckBox)lookup.FindName("PerformerCheck")).IsChecked = false;
                Require(lookup.ApplyChosen(), "ticked lines are applied");
                Require(model.AlbumTitle == "Edited" && model.AlbumPerformer == "Found Artist"
                    && model.Rows[0].Title == "Found One" && model.Rows[0].Performer == "设计歌手" && model.Rows[1].Origin == "MusicBrainz",
                    "an unticked line and the performers stay as they were");
            }
            finally { lookup.Close(); }

            // The check before preparing a package lists non-Latin fields on the in-app card.
            model.Rows[1].Title = "第二首";
            Require(model.Rows[1].HasTitleIssue, "the edited title is marked");
            var check = (Task<bool>)typeof(CdExtraWindow).GetMethod("ConfirmCdTextIssuesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [])!;
            var confirm = (DiscImageStudio.Controls.ConfirmModal)window.FindName("CdTextConfirm");
            Require(!check.IsCompleted && confirm.IsOpen && confirm.TitleText.Text == "检查 CD-TEXT"
                && confirm.MessageText.Text.Contains("第二首", StringComparison.Ordinal)
                && confirm.MessageText.Text.Contains("设计歌手", StringComparison.Ordinal), "the check lists each marked field");
            ScrollToCdText(window);
            Shot(window, "6-cdextra-check");
            confirm.CancelPending();
            Pump();
            Require(check.IsCompleted && !check.Result, "cancelling the check stops the package");
            model.Rows[1].Title = "Second Song";
            model.Rows[0].Performer = "Design Singer";
            check = (Task<bool>)typeof(CdExtraWindow).GetMethod("ConfirmCdTextIssuesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [])!;
            Require(check.IsCompleted && check.Result && !confirm.IsOpen, "all-Latin text goes on without asking");
        }
        finally
        {
            ScrollToCdText(window);
            Shot(window, "5-cdextra-after");
            window.Close();
        }
    }

    /// <summary>Runs queued dispatcher work, such as an awaited confirmation's continuation.</summary>
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void ScrollToCdText(Window window)
    {
        window.Height = 1040;
        window.UpdateLayout();
    }

    /// <summary>With DIS_SHOTS set to a folder, saves the window as a PNG for a visual check.</summary>
    private static void Shot(Window window, string name)
    {
        string? folder = Environment.GetEnvironmentVariable("DIS_SHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        window.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        FrameworkElement content = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder);
        using FileStream file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }

    private static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"{what}: expected {typeof(T).Name}");
    }

    /// <summary>Every confirmation uses the app's own ConfirmModal card; a system MessageBox
    /// looks foreign next to it (CD Extra burn, resume and "no CD-TEXT" prompts included).</summary>
    private static void TestNoSystemMessageBox()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiscImageStudio.slnx")))
            root = root.Parent;
        Require(root is not null, "repository root not found");
        string app = Path.Combine(root!.FullName, "src", "DiscImageStudio.App");
        var offenders = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(file => File.ReadAllText(file).Contains("MessageBox.Show", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Require(offenders.Count == 0, "system MessageBox still used in: " + string.Join(", ", offenders));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("album-info: " + message);
    }
}
