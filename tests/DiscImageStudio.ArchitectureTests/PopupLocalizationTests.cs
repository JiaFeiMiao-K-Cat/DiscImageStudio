using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Localization;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

internal static class PopupLocalizationTests
{
    internal static void Run()
    {
        WpfTestHost.Run(() => Wait(CheckFailures()));
        Console.WriteLine("popup-localization: photo failures, numeric cultures, other-language fallback, nested validation and opaque user text passed");
    }

    private static async Task CheckFailures()
    {
        string originalLanguage = L.CurrentLanguage;
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        var directory = Directory.CreateTempSubdirectory("disc-popup-language-");
        ShellViewModel? shell = null;
        try
        {
            L.Initialize(L.English);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            shell = new ShellViewModel(Dispatcher.CurrentDispatcher);
            shell.NavigateTo(Workspace.Calibration);
            var model = shell.Calibration;
            model.SelectedDiscType = "DVD";
            model.Parameters.DvdTotalSectors = "2297888";
            model.Parameters.DvdInnerRadius = "24";
            model.Parameters.DvdOuterRadius = "58";
            model.Parameters.DvdPitchLinear = model.Parameters.DvdPitchQuadratic = model.Parameters.DvdPitchCubic = "0";
            model.SelectIntermediateModeCommand.Execute(null);
            model.CreateTargetCommand.Execute(null);
            var target = model.Target!;
            var photo = MakeMismatchedPhoto(target);
            var fit = CalibrationPhotoRectifier.Fit(photo.OuterBoundary, photo.HoleBoundary);
            Require(!fit.Succeeded && fit.Transform is null && double.IsFinite(fit.OuterRmsPixels)
                && double.IsFinite(fit.HoleRmsPixels), "the photo exercises the real two-contour RMS rejection");

            string path = Path.Combine(directory.FullName, "photo.calibration.json");
            CalibrationSessionJson.Save(path, new(target, [], Photo: photo));
            await model.LoadSessionFromAsync(path);
            Require(model.PhotoImage is not null && model.RectifyPhotoCommand.CanExecute(null),
                "the unconfirmed photo can be corrected through the normal command");

            string? dotMessage = null;
            foreach (string cultureName in new[] { "en-US", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                shell.Toasts.Clear();
                await model.RectifyPhotoCommand.ExecuteAsync(null);
                string message = AssertPhotoFailure(shell, fit);
                Require(!model.IsRectificationReady && model.RectifiedPhoto is null,
                    "failed correction cannot install a usable photo transform");
                if (dotMessage is null) dotMessage = message;
                else Require(message != dotMessage && message.Contains(",", StringComparison.Ordinal),
                    "English wording retains the user's comma-decimal numeric culture");
            }

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            shell.Toasts.Clear();
            var originalImage = model.PhotoImage;
            CalibrationSessionJson.Save(path, new(target, [], Photo: photo with { RectificationConfirmed = true }));
            await model.LoadSessionFromAsync(path);
            Require(AssertPhotoFailure(shell, fit) == dotMessage
                && ReferenceEquals(originalImage, model.PhotoImage) && !model.IsRectificationReady,
                "a saved confirmed photo reports the same English rejection without partially replacing the current photo");
            SaveSnapshot(shell);
            CheckValidation(target, directory.FullName);
            await CheckOtherLanguages(shell, fit, dotMessage!);
        }
        finally
        {
            shell?.Calibration.OnWindowClosed();
            shell?.LivePreview.OnWindowClosed();
            CultureInfo.CurrentCulture = originalCulture;
            L.Initialize(originalLanguage);
            directory.Delete(recursive: true);
        }
    }

    private static string AssertPhotoFailure(ShellViewModel shell, CalibrationPhotoFitResult fit)
    {
        var toast = shell.Toasts.Last();
        string message = toast.Message;
        Require(!HasChinese(message) && !HasChinese(toast.Title)
            && message.Contains("outer edge", StringComparison.Ordinal)
            && message.Contains("center-hole", StringComparison.Ordinal),
            "the actual failure popup and its title are English");
        Require(message.Contains(fit.OuterRmsPixels.ToString("0.##", CultureInfo.CurrentCulture) + " px", StringComparison.Ordinal)
            && message.Contains(fit.HoleRmsPixels.ToString("0.##", CultureInfo.CurrentCulture) + " px", StringComparison.Ordinal),
            "both fitted RMS values survive localization with the current numeric culture");
        Require(message == shell.Calibration.LastStatusMessage && message == shell.JobStatusText,
            "the popup and the shared bottom status use the same translated error");
        return message;
    }

    private static void CheckValidation(CalibrationTarget target, string directory)
    {
        var versionError = Capture<ArgumentException>(() => new CalibrationTarget(991, target.Id,
            target.Parameters, target.Options, target.Cells, target.Artwork));
        Require(L.Error(versionError).Contains("991", StringComparison.Ordinal) && !HasChinese(L.Error(versionError)),
            "an unsupported calibration schema reports its actual version in English");

        var transformError = Capture<ArgumentException>(() => new CalibrationPhotoTransform([1, 2]));
        Require(transformError.ParamName == "discToSourceMatrix9" && HasChinese(transformError.Message)
            && !HasChinese(L.Error(transformError)) && L.Error(transformError).Contains("9", StringComparison.Ordinal),
            "parameter-suffixed errors retain their diagnostics and have a translated display message");

        // This user ID is also an exact translation key: it must remain data,
        // while the separately marked requirement nested in the error is translated.
        const string presetId = "必须大于 0";
        var presets = DiscPresetJsonStore.CreateInitialUserDocument();
        var invalidPresets = presets with { CdPresets = [presets.CdPresets[0] with { Id = presetId, Sectors = 0 }] };
        string presetMessage = L.Error(Capture<InvalidDataException>(() => DiscPresetJsonStore.Validate(invalidPresets)));
        Require(presetMessage.Contains("'" + presetId + "'", StringComparison.Ordinal)
            && presetMessage.Contains("sectors must be greater than 0", StringComparison.Ordinal)
            && !HasChinese(presetMessage.Replace(presetId, string.Empty, StringComparison.Ordinal)),
            "nested preset requirements translate without translating the user's identifier");

        string userPath = Path.Combine(directory, "照片{0}'-预设.json");
        string userFileName = Path.GetFileName(userPath);
        File.WriteAllText(userPath, "null");
        string pathMessage = L.Error(Capture<InvalidDataException>(() => DiscPresetJsonStore.Load(userPath)));
        Require(pathMessage.Contains(userFileName, StringComparison.Ordinal)
            && !HasChinese(pathMessage.Replace(userFileName, string.Empty, StringComparison.Ordinal)),
            "user filenames containing Chinese, braces and quotes remain unchanged inside translated errors");

        const string rawDiagnostic = "设备原始错误 {0}: E_FAIL";
        Require(L.Error(new IOException(rawDiagnostic)) == rawDiagnostic,
            "unknown device or library errors retain their original diagnostic text");
    }

    private static CalibrationPhotoSession MakeMismatchedPhoto(CalibrationTarget target)
    {
        const int size = 400;
        const double canvas = 63;
        var image = DiscImageStudio.Services.CalibrationPatternRenderer.Render(target, canvas, size, target.Parameters, .37);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        CalibrationPoint[] Boundary(double radius) => Enumerable.Range(0, 8).Select(index => new CalibrationPoint(
            (size - 1) / 2.0 + radius * size / (2 * canvas) * Math.Cos(index * Math.PI / 4),
            (size - 1) / 2.0 + radius * size / (2 * canvas) * Math.Sin(index * Math.PI / 4))).ToArray();
        // The printed ring at 14 mm is deliberately mistaken for the 7.5 mm physical hole.
        return new(Convert.ToBase64String(stream.ToArray()), Boundary(60), Boundary(14), 60, false, []);
    }

    private static async Task CheckOtherLanguages(ShellViewModel shell, CalibrationPhotoFitResult fit, string englishMessage)
    {
        var template = fit.MessageTemplate ?? throw new InvalidOperationException("The photo error must retain its template.");
        const string presetKey = "预设 '{0}' 的 {1} {2}。";
        const string requirementKey = "必须大于 0";
        string[] keys = [template.Format, presetKey, requirementKey];
        var saved = keys.ToDictionary(key => key, key => new Dictionary<string, string>(L.Entries[key]));
        string originalLanguage = L.CurrentLanguage;
        try
        {
            foreach (string key in keys) L.Entries[key].Remove(L.Spanish);
            L.Initialize(L.Spanish);
            shell.Toasts.Clear();
            await shell.Calibration.RectifyPhotoCommand.ExecuteAsync(null);
            Require(shell.Toasts.Last().Message == englishMessage
                && shell.Calibration.LastStatusMessage == englishMessage && shell.JobStatusText == englishMessage
                && shell.Toasts.Last().Title == "La operación de calibración no se completó",
                "a Spanish UI keeps its translated popup title and falls back to English for the missing dynamic error");
            SaveSnapshot(shell, "calibration-photo-error-es-fallback");

            const string presetId = "用户{0}";
            var presets = DiscPresetJsonStore.CreateInitialUserDocument();
            var invalid = presets with { CdPresets = [presets.CdPresets[0] with { Id = presetId, Sectors = 0 }] };
            var error = Capture<InvalidDataException>(() => DiscPresetJsonStore.Validate(invalid));
            Require(L.Error(error) == $"Preset '{presetId}' sectors must be greater than 0.",
                "both the wrapper and nested requirement fall back to English without changing the user identifier");

            // Adding another language needs only catalog entries, including for
            // nested diagnostics; no English-specific exception path is involved.
            L.Entries[template.Format][L.Spanish] = "Borde exterior {0:0.##} px; orificio central {1:0.##} px.";
            L.Entries[presetKey][L.Spanish] = "El campo {1} del ajuste '{0}' {2}.";
            L.Entries[requirementKey][L.Spanish] = "debe ser mayor que 0";
            Require(L.Error(DiagnosticMessage.Argument(template))
                == $"Borde exterior {fit.OuterRmsPixels:0.##} px; orificio central {fit.HoleRmsPixels:0.##} px.",
                "a supplied Spanish photo diagnostic preserves both formatted numeric arguments");
            Require(L.Error(error) == $"El campo sectors del ajuste '{presetId}' debe ser mayor que 0.",
                "Spanish diagnostics can reorder arguments and translate nested requirements after the error was created");
        }
        finally
        {
            foreach (string key in keys)
            {
                L.Entries[key].Clear();
                foreach (var entry in saved[key]) L.Entries[key].Add(entry.Key, entry.Value);
            }
            L.Initialize(originalLanguage);
        }
    }

    private static void SaveSnapshot(ShellViewModel shell, string name = "calibration-photo-error-en")
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISC_IMAGE_STUDIO_UI_SNAPSHOTS"))) return;
        var page = new CalibrationView(shell.Calibration);
        var dock = new JobDock { DataContext = shell };
        var toasts = new ToastHost { DataContext = shell, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(20) };
        var layout = new Grid { DataContext = shell };
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(page);
        Grid.SetRow(dock, 1);
        layout.Children.Add(dock);
        Grid.SetRowSpan(toasts, 2);
        layout.Children.Add(toasts);
        var host = new Window { DataContext = shell, Content = layout };
        try
        {
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            dock.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            layout.Measure(new Size(1600, 1200));
            layout.Arrange(new Rect(0, 0, 1600, 1200));
            layout.UpdateLayout();
            UiLayoutSnapshot.Save(layout, name);
        }
        finally
        {
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            dock.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            host.Content = null;
            host.Close();
        }
    }

    private static T Capture<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Popup localization: expected " + typeof(T).Name);
    }

    private static bool HasChinese(string value) => value.Any(character => character is >= '\u4e00' and <= '\u9fff');

    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Popup localization: " + message);
    }
}
