using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

/// <summary>Checks shipped translations and the language preference independently of WPF
/// windows, including culture isolation and the save/cancel boundary.</summary>
internal static class LocalizationTests
{
    private static readonly (int Index, string Language)[] LanguageChoices =
    [
        (0, L.SystemLanguage),
        (1, L.Chinese),
        (2, L.TraditionalChinese),
        (3, L.English),
        (4, L.Japanese),
        (5, L.Turkish),
        (6, L.Spanish),
        (7, L.Czech),
    ];

    // An English term must come out as the matching Czech stem, so a Czech string cannot drift
    // to another subject. Stems leave room for inflection (vrstva/vrstev, disk/discích).
    private static readonly (Regex English, Regex Czech)[] CzechTerms =
    [
        (Term(@"\bpresets?\b"), Term("předvol")),
        (Term(@"\blayers?\b"), Term("vrst")),
        (Term(@"\bsectors?\b"), Term("sektor")),
        (Term(@"\bcalibrat"), Term("kalibr")),
        (Term(@"\bcache\b"), Term("mezipam")),
        (Term(@"\brecorders?\b"), Term("vypalovač")),
        (Term(@"\bburn"), Term("vypal|vypál")),
        (Term(@"\bprojects?\b"), Term("projekt")),
        (Term(@"\bpitch\b"), Term("rozteč")),
        (Term("interleav"), Term("proklád")),
        (Term(@"\bradi(us|i)\b"), Term("poloměr")),
        (Term(@"\bphoto"), Term("fot|foť")),
        (Term(@"\bpreview"), Term("náhled")),
        (Term(@"\bcurves?\b"), Term("křiv")),
        (Term(@"\bsettings\b"), Term("nastaven")),
        (Term(@"\bthemes?\b"), Term("motiv")),
        (Term(@"\btracks?\b"), Term("stop")),
        (Term(@"\breadback"), Term("zpětn")),
        (Term(@"\bdiscs?\b"), Term(@"dis[kc]|\b(CD|DVD|BD)\b|Blu-ray|médi")),
    ];

    public static void Run() => WpfTestHost.Run(CheckLocalization);

    private static void CheckLocalization()
    {
        string originalLanguage = L.CurrentLanguage;
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo? originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        string directory = Path.Combine(Path.GetTempPath(), "disc-localization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestCatalog();
            TestCzechText();
            TestMissingTranslations();
            TestLanguageResolution();
            TestTranslationAndFormatting();
            TestDefaultSettings(directory);
            TestLanguageSettings(directory);
            TestCancelledSettings(directory);
            TestFailedSettings(directory);
        }
        finally
        {
            L.Initialize(originalLanguage);
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUiCulture;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestCatalog()
    {
        Require(L.Entries.Count > 0, "the published assembly embeds translation catalogs");
        foreach ((string key, Dictionary<string, string> translations) in L.Entries)
        {
            ValidateCatalogEntry(key, translations);
        }
    }

    private static void ValidateCatalogEntry(string key, Dictionary<string, string> translations)
    {
        Require(translations.TryGetValue(L.Chinese, out string? chinese)
                && !string.IsNullOrWhiteSpace(chinese), key + " has Simplified Chinese fallback text");
        HashSet<string> sourceArguments = ReadArguments(chinese!, key);
        foreach (string language in new[] { L.TraditionalChinese, L.English, L.Japanese, L.Turkish, L.Spanish, L.Czech })
        {
            if (!translations.TryGetValue(language, out string? translated) || string.IsNullOrWhiteSpace(translated))
                continue;
            HashSet<string> translatedArguments = ReadArguments(translated, key);
            Require(sourceArguments.SetEquals(translatedArguments),
                key + " preserves argument indices and numeric/date formats in " + language);
        }
    }

    private static void TestMissingTranslations()
    {
        const string key = "Xaml.LayerCountFormat";
        Dictionary<string, string> translations = L.Entries[key];
        var saved = new Dictionary<string, string>(translations);
        string originalLanguage = L.CurrentLanguage;
        try
        {
            translations.Clear();
            translations[L.Chinese] = saved[L.Chinese];
            ValidateCatalogEntry(key, translations);
            TestCzechText();
            foreach (string language in new[] { L.TraditionalChinese, L.English, L.Japanese, L.Turkish, L.Spanish, L.Czech })
            {
                L.Initialize(language);
                Require(L.T(key) == saved[L.Chinese], language + " uses Chinese when the requested and English translations are absent");
                Require(L.Format(key, 3) == string.Format(CultureInfo.CurrentCulture, saved[L.Chinese], 3),
                    language + " fallback preserves format arguments");
                translations[language] = " \t";
                Require(L.T(key) == saved[L.Chinese], language + " skips blank requested and English translations");
            }
            ValidateCatalogEntry(key, translations);
            TestCzechText();

            translations[L.English] = "{0} layers";
            foreach (string language in new[] { L.TraditionalChinese, L.Japanese, L.Turkish, L.Spanish, L.Czech })
            {
                L.Initialize(language);
                translations.Remove(language);
                Require(L.T(key) == "{0} layers" && L.Format(key, 3) == "3 layers",
                    language + " uses English with its format arguments before Chinese");
                translations[language] = " \t";
                Require(L.T(key) == "{0} layers", language + " blank text also falls back to English");
            }

            L.Initialize(L.Chinese);
            Require(L.T(key) == saved[L.Chinese], "the requested Chinese translation takes precedence over English");
            translations[L.Czech] = "Počet vrstev: {0}";
            L.Initialize(L.Czech);
            Require(L.Format(key, 3) == "Počet vrstev: 3", "the requested translation takes precedence over English and Chinese");
            translations.Remove(L.English);
            TestCzechText(); // A supplied Czech translation does not require an English translation.
            Require(L.Format(key, 3) == "Počet vrstev: 3", "a requested translation remains usable without English");

            translations[L.Czech] = " ";
            Require(L.T(key) == saved[L.Chinese], "missing English falls back to Chinese");
            translations[L.English] = " \t";
            Require(L.Format(key, 3) == string.Format(CultureInfo.CurrentCulture, saved[L.Chinese], 3),
                "blank English also falls back to Chinese with its arguments");
            translations[L.Chinese] = " \t";
            Require(L.T(key) == key, "blank translations at every level return the original key");
            translations.Clear();
            Require(L.T(key) == key, "an entry without any translations returns its original key");
            translations[L.Chinese] = saved[L.Chinese];

            translations[L.English] = "{1} layers";
            bool rejected = false;
            try { ValidateCatalogEntry(key, translations); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "provided translations still reject changed format arguments");
            translations[L.English] = "{0} layers";
            L.Initialize(L.English);
            Require(L.T(key) == "{0} layers", "a later translation replaces the fallback without changing the key");
        }
        finally
        {
            translations.Clear();
            foreach (var entry in saved) translations.Add(entry.Key, entry.Value);
            L.Initialize(originalLanguage);
        }
    }

    private static HashSet<string> ReadArguments(string text, string key)
    {
        try
        {
            // The runtime parser handles escaped braces, alignment, and format specifiers;
            // recording arguments allows translations to reorder placeholders freely.
            CompositeFormat format = CompositeFormat.Parse(text);
            HashSet<string> seen = new(StringComparer.Ordinal);
            object[] arguments = Enumerable.Range(0, format.MinimumArgumentCount)
                .Select(index => (object)new RecordingArgument(index, seen)).ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, format, arguments);
            return seen;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Invalid localization format: " + key, exception);
        }
    }

    private static void TestCzechText()
    {
        Regex chinese = new(@"[\p{IsCJKUnifiedIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}]");
        Regex looseOneLetterWord = new(@"(?<![\w\-./\\{])[kKsSvVzZoOuUaAiI] (?=\S)");
        foreach ((string key, Dictionary<string, string> translations) in L.Entries)
        {
            if (!translations.TryGetValue(L.Czech, out string? czech) || string.IsNullOrWhiteSpace(czech)) continue;
            translations.TryGetValue(L.English, out string? english);
            bool hasEnglish = !string.IsNullOrWhiteSpace(english);
            string name = key + " (cs)";
            Require(!hasEnglish || czech != english || !english!.Contains(' '), name + " is translated, not copied from English");
            Require(!chinese.IsMatch(czech) || czech == english, name + " contains no untranslated Chinese");
            Require(!czech.Contains("...") && !czech.Contains('\u2014') && !czech.Contains('"') && !czech.Contains('\u201D'),
                name + " uses Czech typography");
            Require(czech.Count(c => c == '\u201E') == czech.Count(c => c == '\u201C'),
                name + " closes every Czech quotation mark");
            Require(!hasEnglish || char.IsWhiteSpace(czech[0]) == char.IsWhiteSpace(english![0])
                    && char.IsWhiteSpace(czech[^1]) == char.IsWhiteSpace(english![^1]),
                name + " keeps the edge spaces that concatenated fragments rely on");
            Require(!looseOneLetterWord.IsMatch(czech),
                name + " binds one-letter prepositions to the next word with U+00A0");
            if (!hasEnglish) continue;
            string englishSubject = english!.Replace("Disc Image Studio", string.Empty, StringComparison.Ordinal);
            string czechSubject = czech.Replace("Disc Image Studio", string.Empty, StringComparison.Ordinal);
            foreach ((Regex englishTerm, Regex czechTerm) in CzechTerms)
            {
                Require(!englishTerm.IsMatch(englishSubject) || czechTerm.IsMatch(czechSubject),
                    name + " translates /" + englishTerm + "/ as /" + czechTerm + "/");
            }
        }
    }

    private static Regex Term(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void TestLanguageResolution()
    {
        Require(L.NormalizeLanguage(" EN-us ") == L.English, "English regional tags normalize");
        Require(L.NormalizeLanguage("en-AU") == L.English, "other explicit English regions normalize to English");
        Require(L.NormalizeLanguage("zh-Hans") == L.Chinese, "Simplified Chinese tags normalize");
        Require(L.NormalizeLanguage("zh-Hans-TW") == L.Chinese,
            "an explicit Simplified script takes precedence over a Traditional region");
        foreach (string language in new[] { "zh-TW", "zh-HK", "zh-MO", "zh-Hant", "zh-Hant-HK" })
        {
            Require(L.NormalizeLanguage(language) == L.TraditionalChinese,
                language + " normalizes to Traditional Chinese");
            Require(L.ResolveLanguage(L.SystemLanguage, CultureInfo.GetCultureInfo(language)) == L.TraditionalChinese,
                language + " system locale uses Traditional Chinese");
        }
        Require(L.NormalizeLanguage(" ZH-hAnT-tW ") == L.TraditionalChinese,
            "Traditional Chinese normalization ignores case and surrounding space");
        Require(L.NormalizeLanguage("zh-Hant-CN") == L.TraditionalChinese,
            "an explicit Traditional script takes precedence over the region");
        foreach (string language in new[] { "zh-CN", "zh-SG", "zh-Hans", "zh-Hans-SG" })
        {
            Require(L.ResolveLanguage(L.SystemLanguage, CultureInfo.GetCultureInfo(language)) == L.Chinese,
                language + " system locale uses Simplified Chinese");
        }
        Require(L.NormalizeLanguage("SYSTEM") == L.SystemLanguage, "system preference normalizes");
        foreach (string? language in new string?[] { null, "", "   ", "unknown" })
        {
            Require(L.NormalizeLanguage(language) == L.SystemLanguage,
                "missing or invalid preferences follow the system language");
            Require(L.ResolveLanguage(language, CultureInfo.GetCultureInfo("zh-TW")) == L.TraditionalChinese,
                "missing or invalid preferences use a Traditional Chinese system locale");
            Require(L.ResolveLanguage(language, CultureInfo.GetCultureInfo("fr-FR")) == L.English,
                "missing or invalid preferences use English on a non-Chinese, non-Japanese system");
            Require(L.ResolveLanguage(language, CultureInfo.GetCultureInfo("ja-JP")) == L.Japanese,
                "missing or invalid preferences use Japanese on a Japanese system");
            Require(L.ResolveLanguage(language, CultureInfo.GetCultureInfo("tr-TR")) == L.Turkish,
                "missing or invalid preferences use Turkish on a Turkish system");
        }
        Require(L.NormalizeLanguage(" JA-jp ") == L.Japanese, "Japanese regional tags normalize");
        Require(L.NormalizeLanguage("ja") == L.Japanese, "Japanese normalizes");
        foreach (string language in new[] { "ja", "ja-JP" })
        {
            Require(L.ResolveLanguage(L.SystemLanguage, CultureInfo.GetCultureInfo(language)) == L.Japanese,
                language + " system locale uses Japanese");
        }
        Require(L.ResolveLanguage(L.Japanese, CultureInfo.GetCultureInfo("zh-CN")) == L.Japanese,
            "explicit Japanese overrides a Chinese system");
        Require(L.ResolveLanguage(L.English, CultureInfo.GetCultureInfo("ja-JP")) == L.English,
            "explicit English overrides a Japanese system");
        Require(L.NormalizeLanguage(" TR-tr ") == L.Turkish, "Turkish regional tags normalize");
        Require(L.NormalizeLanguage("tr") == L.Turkish, "Turkish normalizes");
        foreach (string language in new[] { "tr", "tr-TR", "tr-CY" })
        {
            Require(L.ResolveLanguage(L.SystemLanguage, CultureInfo.GetCultureInfo(language)) == L.Turkish,
                language + " system locale uses Turkish");
        }
        Require(L.ResolveLanguage(L.Turkish, CultureInfo.GetCultureInfo("zh-CN")) == L.Turkish,
            "explicit Turkish overrides a Chinese system");
        Require(L.ResolveLanguage(L.English, CultureInfo.GetCultureInfo("tr-TR")) == L.English,
            "explicit English overrides a Turkish system");
        Require(L.NormalizeLanguage(" ES-mx ") == L.Spanish, "Spanish regional tags normalize");
        Require(L.NormalizeLanguage("es") == L.Spanish, "Spanish normalizes");
        foreach (string language in new[] { "es", "es-ES", "es-MX", "es-419" })
        {
            Require(L.ResolveLanguage(L.SystemLanguage, CultureInfo.GetCultureInfo(language)) == L.Spanish,
                language + " system locale uses Spanish");
        }
        Require(L.ResolveLanguage(L.Spanish, CultureInfo.GetCultureInfo("zh-CN")) == L.Spanish,
            "explicit Spanish overrides a Chinese system");
        Require(L.ResolveLanguage(L.English, CultureInfo.GetCultureInfo("es-ES")) == L.English,
            "explicit English overrides a Spanish system");
        Require(L.NormalizeLanguage(" CS-cz ") == L.Czech, "Czech regional tags normalize");
        Require(L.NormalizeLanguage("cs") == L.Czech, "Czech normalizes");
        foreach (string language in new[] { "cs", "cs-CZ" })
        {
            Require(L.ResolveLanguage(L.SystemLanguage, CultureInfo.GetCultureInfo(language)) == L.Czech,
                language + " system locale uses Czech");
        }
        Require(L.ResolveLanguage(L.Czech, CultureInfo.GetCultureInfo("ja-JP")) == L.Czech,
            "explicit Czech overrides a Japanese system");
        Require(L.ResolveLanguage(L.English, CultureInfo.GetCultureInfo("cs-CZ")) == L.English,
            "explicit English overrides a Czech system");
        foreach (string language in new[] { "en-US", "fr-FR", "ko-KR" })
        {
            Require(L.ResolveLanguage(L.SystemLanguage, CultureInfo.GetCultureInfo(language)) == L.English,
                language + " system locale uses English");
        }
        Require(L.ResolveLanguage(L.English, CultureInfo.GetCultureInfo("zh-CN")) == L.English,
            "explicit language overrides system language");
        Require(L.ResolveLanguage("en-AU", CultureInfo.GetCultureInfo("zh-CN")) == L.English,
            "an explicit English regional tag overrides a Chinese system");
        Require(L.ResolveLanguage("zh-Hans-TW", CultureInfo.GetCultureInfo("zh-TW")) == L.Chinese,
            "an explicit Simplified script overrides a Traditional Chinese system");
        Require(L.ResolveLanguage(L.TraditionalChinese, CultureInfo.GetCultureInfo("en-US")) == L.TraditionalChinese,
            "explicit Traditional Chinese overrides an English system");
        Require(L.ResolveLanguage(L.Chinese, CultureInfo.GetCultureInfo("zh-TW")) == L.Chinese,
            "explicit Simplified Chinese overrides a Traditional Chinese system");
    }

    private static void TestTranslationAndFormatting()
    {
        CultureInfo numericCulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentCulture = numericCulture;
        L.Initialize(L.English);
        Require(ReferenceEquals(CultureInfo.CurrentCulture, numericCulture),
            "initialization does not alter numeric culture");
        Require(CultureInfo.CurrentUICulture.Name == L.English, "UI culture follows the selected language");
        Require(L.T("Xaml.Cancel") == "Cancel", "English text resolves from the embedded catalog");
        Require(L.T("unregistered localization key") == "unregistered localization key",
            "missing translation falls back to its key");

        string action = "close";
        string project = "Mosaic";
        Require(L.F($"{action}之前要保存对「{project}」的更改吗？")
                == "Save changes to “Mosaic” before continuing (close)?",
            "interpolated arguments follow the translated order");
        long expected = 12345;
        long actual = 6789;
        Require(L.F($"BD ISO 长度与总扇区数不符：需要 {expected:N0} 字节，实际 {actual:N0} 字节。")
                == $"BD ISO size does not match the sector count: expected {expected:N0} bytes, found {actual:N0}.",
            "interpolated numeric formats honor the existing numeric culture");
        Require(L.Format("Xaml.LayerCountFormat", 3) == "3 layers", "named formats accept arguments");

        L.Initialize(L.TraditionalChinese);
        Require(L.CurrentLanguage == L.TraditionalChinese && CultureInfo.CurrentUICulture.Name == L.TraditionalChinese,
            "Traditional Chinese initializes the requested UI culture");
        Require(L.T("Xaml.OpenSettingsShortcut") == "開啟設定 (Ctrl+,)",
            "Traditional Chinese resolves from the embedded catalog");
        Require(L.T("unregistered localization key") == "unregistered localization key",
            "Traditional Chinese preserves the missing-key fallback");
        Require(ReferenceEquals(CultureInfo.CurrentCulture, numericCulture),
            "Traditional Chinese does not alter numeric culture");
        string formattedTraditional = L.F($"BD ISO 长度与总扇区数不符：需要 {expected:N0} 字节，实际 {actual:N0} 字节。");
        Require(formattedTraditional.Contains(expected.ToString("N0", numericCulture), StringComparison.Ordinal)
                && formattedTraditional.Contains(actual.ToString("N0", numericCulture), StringComparison.Ordinal),
            "Traditional Chinese interpolation preserves both formatted numeric arguments");

        L.Initialize(L.Japanese);
        Require(L.CurrentLanguage == L.Japanese && CultureInfo.CurrentUICulture.Name == L.Japanese,
            "Japanese initializes the requested UI culture");
        Require(L.T("Xaml.Cancel") == "キャンセル", "Japanese resolves from the embedded catalog");
        Require(ReferenceEquals(CultureInfo.CurrentCulture, numericCulture),
            "Japanese does not alter numeric culture");
        string formattedJapanese = L.F($"BD ISO 长度与总扇区数不符：需要 {expected:N0} 字节，实际 {actual:N0} 字节。");
        Require(formattedJapanese.Contains(expected.ToString("N0", numericCulture), StringComparison.Ordinal)
                && formattedJapanese.Contains(actual.ToString("N0", numericCulture), StringComparison.Ordinal),
            "Japanese interpolation preserves both formatted numeric arguments");

        L.Initialize(L.Turkish);
        Require(L.CurrentLanguage == L.Turkish && CultureInfo.CurrentUICulture.Name == L.Turkish,
            "Turkish initializes the requested UI culture");
        Require(L.T("Xaml.Cancel") == "İptal", "Turkish resolves from the embedded catalog");
        Require(L.T("unregistered localization key") == "unregistered localization key",
            "Turkish preserves the missing-key fallback");
        Require(ReferenceEquals(CultureInfo.CurrentCulture, numericCulture),
            "Turkish does not alter numeric culture");
        string formattedTurkish = L.F($"BD ISO 长度与总扇区数不符：需要 {expected:N0} 字节，实际 {actual:N0} 字节。");
        Require(formattedTurkish.Contains(expected.ToString("N0", numericCulture), StringComparison.Ordinal)
                && formattedTurkish.Contains(actual.ToString("N0", numericCulture), StringComparison.Ordinal),
            "Turkish interpolation preserves both formatted numeric arguments");

        L.Initialize(L.Spanish);
        Require(L.CurrentLanguage == L.Spanish && CultureInfo.CurrentUICulture.Name == L.Spanish,
            "Spanish initializes the requested UI culture");
        Require(L.T("Xaml.Cancel") == "Cancelar", "Spanish resolves from the embedded catalog");
        Require(L.T("unregistered localization key") == "unregistered localization key",
            "Spanish preserves the missing-key fallback");
        Require(ReferenceEquals(CultureInfo.CurrentCulture, numericCulture),
            "Spanish does not alter numeric culture");
        string formattedSpanish = L.F($"BD ISO 长度与总扇区数不符：需要 {expected:N0} 字节，实际 {actual:N0} 字节。");
        Require(formattedSpanish.Contains(expected.ToString("N0", numericCulture), StringComparison.Ordinal)
                && formattedSpanish.Contains(actual.ToString("N0", numericCulture), StringComparison.Ordinal),
            "Spanish interpolation preserves both formatted numeric arguments");

        L.Initialize(L.Czech);
        Require(L.CurrentLanguage == L.Czech && CultureInfo.CurrentUICulture.Name == L.Czech,
            "Czech initializes the requested UI culture");
        Require(L.T("Xaml.Cancel") == "Zrušit", "Czech resolves from the embedded catalog");
        Require(L.T("unregistered localization key") == "unregistered localization key",
            "Czech preserves the missing-key fallback");
        Require(L.Format("Xaml.LayerCountFormat", 3) == "Vrstvy: 3", "Czech named formats accept arguments");
        Require(ReferenceEquals(CultureInfo.CurrentCulture, numericCulture),
            "Czech does not alter numeric culture");
        string formattedCzech = L.F($"BD ISO 长度与总扇区数不符：需要 {expected:N0} 字节，实际 {actual:N0} 字节。");
        Require(formattedCzech.Contains(expected.ToString("N0", numericCulture), StringComparison.Ordinal)
                && formattedCzech.Contains(actual.ToString("N0", numericCulture), StringComparison.Ordinal),
            "Czech interpolation preserves both formatted numeric arguments");

        L.Initialize(L.Chinese);
        Require(L.T("Xaml.Cancel") == "取消", "Chinese text is available after switching back");
        Require(ReferenceEquals(CultureInfo.CurrentCulture, numericCulture),
            "switching back does not alter numeric culture");
        L.Initialize("unsupported");
        Require(L.CurrentLanguage == L.ResolveLanguage(L.SystemLanguage),
            "unknown language starts with the captured system UI language");
        Require(ReferenceEquals(CultureInfo.CurrentCulture, numericCulture),
            "following the system UI language does not alter numeric culture");
    }

    private static void TestDefaultSettings(string directory)
    {
        AppSettingsStore store = new(Path.Combine(directory, "default-settings.json"));
        SettingsViewModel viewModel = new(new AppSettings(), store);
        Require(viewModel.ThemeIndex == 2 && viewModel.LanguageIndex == 0,
            "new settings select Follow system for both theme and language");
        Require(viewModel.TrySave(), "default settings save successfully");
        AppSettings saved = store.Load();
        Require(saved.Theme == AppTheme.System && saved.Language == L.SystemLanguage,
            "saving defaults preserves the system preferences");

        SettingsViewModel normalized = new(new AppSettings { Theme = (AppTheme)99, Language = "unknown" }, store);
        Require(normalized.ThemeIndex == 2 && normalized.LanguageIndex == 0,
            "invalid stored values select the system options");
        normalized.ThemeIndex = -1;
        normalized.LanguageIndex = 99;
        Require(normalized.TrySave(), "invalid selector indices normalize on save");
        AppSettings normalizedSaved = store.Load();
        Require(normalizedSaved.Theme == AppTheme.System && normalizedSaved.Language == L.SystemLanguage,
            "invalid selector indices fall back to system preferences");
    }

    private static void TestLanguageSettings(string directory)
    {
        L.Initialize(L.Chinese);
        foreach ((int index, string language) in LanguageChoices)
        {
            AppSettingsStore store = new(Path.Combine(directory, "language-" + index + ".json"));
            SettingsViewModel viewModel = new(new AppSettings { Language = language }, store);
            Require(viewModel.LanguageIndex == index, "saved language selects the matching option");
            viewModel.LanguageIndex = index;
            Require(viewModel.TrySave(), "language preference saves successfully");
            Require(store.Load().Language == language && viewModel.SavedSettings?.Language == language,
                "the selected language persists and is returned to the caller");
            Require(L.CurrentLanguage == L.Chinese && CultureInfo.CurrentUICulture.Name == L.Chinese,
                "saving a language does not partially switch the running application");
        }
    }

    private static void TestCancelledSettings(string directory)
    {
        AppSettingsStore store = new(Path.Combine(directory, "cancel.json"));
        AppSettings initial = new() { Language = L.Chinese, Theme = AppTheme.Light };
        store.Save(initial);
        string before = File.ReadAllText(store.FilePath);
        foreach ((int index, string language) in LanguageChoices)
        {
            SettingsViewModel viewModel = new(store.Load(), store) { LanguageIndex = index, ThemeIndex = 0 };
            Require(File.ReadAllText(store.FilePath) == before,
                "editing " + language + " without Save leaves preferences unchanged");
            Require(viewModel.SavedSettings is null, "an unconfirmed dialog exposes no saved settings");
            Require(L.CurrentLanguage == L.Chinese, "cancelled language edits do not affect the session");
        }
    }

    private static void TestFailedSettings(string directory)
    {
        string destination = Path.Combine(directory, "existing-directory");
        Directory.CreateDirectory(destination);
        foreach ((int index, string language) in LanguageChoices)
        {
            SettingsViewModel viewModel = new(new AppSettings(), new AppSettingsStore(destination))
            {
                LanguageIndex = index,
            };
            Require(!viewModel.TrySave(), "a directory cannot be overwritten with " + language + " preferences");
            Require(viewModel.SavedSettings is null && viewModel.ErrorMessage.Length > 0,
                "a failed save reports an error without exposing an applied preference");
            Require(L.CurrentLanguage == L.Chinese, "a failed save preserves the running language");
        }
        Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "failed saving cleans up its temporary file");
    }

    private sealed class RecordingArgument(int index, HashSet<string> seen) : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider)
        {
            seen.Add(index.ToString(CultureInfo.InvariantCulture) + ":" + format);
            return string.Empty;
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Localization check failed: " + name);
    }
}
