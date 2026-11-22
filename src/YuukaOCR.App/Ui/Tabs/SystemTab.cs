using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using YuukaOCR.App.Services;
using YuukaOCR.App.Ui.Controls;
using YuukaOCR.Core;
using YuukaOCR.Core.Input;
using YuukaOCR.Core.Interop;
using YuukaOCR.Core.Localization;
using YuukaOCR.Core.Providers;
using YuukaOCR.Core.Update;

namespace YuukaOCR.App.Ui.Tabs;

public partial class SystemTab : UserControl
{
    /// <summary>One row of the key list: the mask for display, the raw key for the copy button.</summary>
    public sealed record KeyEntry(int Index, string Masked, string Key);

    private AppServices _services = null!;
    private bool _loading;

    // What the model box suggests: the provider's live list, ranked, plus the models this user has used
    // before (that is where an OpenRouter "@preset/..." entry comes from — a preset never appears in
    // /models). Deliberately empty until a key has been proven to work.
    private List<string> _modelPool = new();

    // Bumped for every model-list request: a slow answer for a provider the user already switched away
    // from must not overwrite the list of the current one.
    private int _modelRequest;

    public SystemTab() => InitializeComponent();

    public void Initialize(AppServices services)
    {
        _services = services;

        ProviderCombo.ItemsSource = ProviderRegistry.All;

        // The picker owns the whole search behaviour (focus shows everything, typing filters) and only
        // reports back when a value is final: Enter, a click on a row, or the field losing focus.
        ModelPicker.TextCommitted += (_, _) => CommitModel();

        HotkeyControl.SpecChanged += spec =>
        {
            _services.Settings.Update(s => s.HookOcrHotkey = spec.Serialize());
            _services.Log.ConsoleLog(L.F("sys.hotkey.changed", spec.Describe()));
        };

        Load();
        RefreshUpdateSection();
    }

    public void SelectProvider(string providerId)
    {
        var info = ProviderRegistry.All.FirstOrDefault(p => p.Id == providerId);
        if (info is not null) ProviderCombo.SelectedItem = info;
    }

    /// <summary>
    /// The startup check calls this when the current provider has no key, or the stored one was
    /// rejected: the notice sits right above the key input, where the fix is.
    /// </summary>
    public void ShowKeyPrompt(string? reason)
    {
        var info = ProviderRegistry.Get(_services.Settings.Settings.ApiProvider);
        KeyPrompt.Text = string.IsNullOrWhiteSpace(reason)
            ? L.F("sys.key.promptMissing", info.DisplayName)
            : L.F("sys.key.promptInvalid", reason);
        KeyPrompt.Visibility = Visibility.Visible;
        KeyBox.Focus();
    }

    private void HideKeyPrompt() => KeyPrompt.Visibility = Visibility.Collapsed;

    private void Load()
    {
        _loading = true;

        var settings = _services.Settings.Settings;
        var info = ProviderRegistry.Get(settings.ApiProvider);

        ProviderCombo.SelectedItem = ProviderRegistry.All.FirstOrDefault(p => p.Id == info.Id);
        ProviderNote.Text = info.NoteText.Length > 0
            ? $"{info.NoteText}  ·  {info.DocsUrl}"
            : info.DocsUrl;

        // The box always shows a usable value (the saved model, or the provider default) even while the
        // list behind it is still empty.
        ModelPicker.Text = string.IsNullOrWhiteSpace(settings.Model) ? info.DefaultModel : settings.Model;
        ClearModels();

        BaseUrlBox.Text = settings.CustomBaseUrl;
        HotkeyControl.Spec = HotkeySpec.Parse(settings.HookOcrHotkey);
        StartWithSystemBox.IsChecked = settings.StartWithSystem;
        AutoUpdateBox.IsChecked = settings.AutoUpdateEnabled;
        ProcessTextBox.IsChecked = settings.ProcessTextClipboard;
        ProcessImageBox.IsChecked = settings.ProcessImageClipboard;
        ProcessFileBox.IsChecked = settings.ProcessFileClipboard;

        _loading = false;
        HideKeyError();
        HideKeyPrompt();
        RefreshKeys();

        // Every time the tab opens, try the configured key and load the list behind it.
        _ = LoadModelsAsync(announce: false);
    }

    // ------------------------------------------------------------- model list

    /// <summary>
    /// Asks the provider for its catalogue with the key that is configured for the current provider.
    /// An invalid key leaves the list empty on purpose: a suggestion the key cannot use is a trap.
    /// </summary>
    private async Task LoadModelsAsync(bool announce)
    {
        var request = ++_modelRequest;
        var providerId = _services.Settings.Settings.ApiProvider;

        if (announce) RefreshModelsButton.IsEnabled = false;
        ModelStatus.Text = L.T(announce ? "sys.models.checking" : "sys.models.loading");

        try
        {
            var result = await _services.Ocr.VerifyAsync();
            if (request != _modelRequest) return;   // a newer request (provider or key change) won

            if (!result.Success)
            {
                ClearModels();

                // "No key at all" is not an error to read: the notice above the key input already says
                // what to do, so the status only points at it instead of repeating a provider message.
                var noKey = ProviderRegistry.Get(providerId).RequiresKey
                            && ApiKeyPool.LoadFor(AppPaths.EnvFile, providerId).Count == 0;
                ModelStatus.Text = noKey ? L.T("sys.models.empty") : L.F("sys.models.failed", result.Message);
                return;
            }

            ApplyModels(providerId, result.Models);
            ModelStatus.Text = _modelPool.Count > 0
                ? L.F("sys.models.ok", _modelPool.Count)
                : L.T("sys.models.empty");
        }
        catch (Exception ex)
        {
            // VerifyAsync folds provider failures into its result, so this is a local problem (a disposed
            // service, out of memory) — still shown instead of leaving an empty list unexplained.
            if (request == _modelRequest)
            {
                ClearModels();
                ModelStatus.Text = L.F("sys.models.failed", ex.Message);
            }
        }
        finally
        {
            if (announce) RefreshModelsButton.IsEnabled = true;
        }
    }

    private void ApplyModels(string providerId, IEnumerable<string> models)
    {
        var ranked = ModelRanking.Rank(models);

        // The user's own picks are merged back in: they are the OpenRouter presets, and the models they
        // used before that the provider may no longer advertise.
        if (_services.Settings.Settings.RecentModels.TryGetValue(providerId, out var recent))
            ranked = ModelRanking.Rank(ranked.Concat(recent));

        // The saved model stays pickable even when the provider does not list it (a preset, a local
        // model). It goes right after the presets so it never pushes them off the top.
        var current = ModelPicker.Text.Trim();
        if (current.Length > 0 && !ranked.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            var insertAt = ranked.FindIndex(m => !ModelRanking.IsPreset(m));
            ranked.Insert(insertAt < 0 ? ranked.Count : insertAt, current);
        }

        _modelPool = ranked;
        ModelPicker.SetModels(_modelPool);
    }

    private void ClearModels()
    {
        _modelPool = new List<string>();
        ModelPicker.SetModels(_modelPool);
    }

    /// <summary>Add the just-typed model to the suggestion list so it can be picked again later.</summary>
    private void EnsureModelInList(string model)
    {
        if (_modelPool.Contains(model, StringComparer.OrdinalIgnoreCase)) return;

        _modelPool = ModelRanking.Rank(_modelPool.Append(model));
        ModelPicker.SetModels(_modelPool);
    }

    // -------------------------------------------------------------------- keys

    private void RefreshKeys()
    {
        var providerId = _services.Settings.Settings.ApiProvider;
        var info = ProviderRegistry.Get(providerId);
        var keys = ApiKeyPool.LoadFor(AppPaths.EnvFile, providerId);

        KeyHint.Text = info.RequiresKey
            ? L.F("sys.key.hintEnv", info.EnvKey)
            : L.T("sys.key.hintLocal");

        // The rows carry the *raw* key as well as the mask: the copy/remove buttons act on it, and the
        // index is what ApiKeyPool.SaveAll rewrites, so no lookup by masked text is ever needed.
        KeyList.ItemsSource = keys
            .Select((key, index) => new KeyEntry(index, ApiKeyPool.Mask(key), key))
            .ToList();

        KeyList.Visibility = keys.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        KeyEmpty.Visibility = keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        KeyStatus.Text = keys.Count == 0 ? "" : L.F("sys.key.count", keys.Count);
    }

    private void ShowKeyError(string message)
    {
        KeyError.Text = message;
        KeyError.Visibility = Visibility.Visible;
    }

    private void HideKeyError()
    {
        KeyError.Text = "";
        KeyError.Visibility = Visibility.Collapsed;
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderCombo.SelectedItem is not ProviderInfo info) return;

        HideKeyError();
        HideKeyPrompt();

        _services.Settings.Update(s =>
        {
            s.ApiProvider = info.Id;
            s.Model = "";
            s.RememberProvider(info.Id);
        });

        // Empty until the new provider's key answers: the old provider's models are not valid here.
        ClearModels();
        ModelPicker.Text = info.DefaultModel;

        RefreshKeys();
        ProviderNote.Text = info.NoteText.Length > 0 ? $"{info.NoteText}  ·  {info.DocsUrl}" : info.DocsUrl;

        _ = LoadModelsAsync(announce: false);
    }

    /// <summary>
    /// Commit the typed model. The box allows free input: local models (ollama/lmstudio), OpenRouter
    /// presets ("@preset/tools"), or any name the server returns.
    /// </summary>
    private void CommitModel()
    {
        if (_loading) return;

        var model = ModelPicker.Text.Trim();
        if (model == _services.Settings.Settings.Model) return;

        // Nothing was selected and the box already shows the provider's default model, so do not write:
        // focusing the box also blurs it, and "default" should not become a hard-coded choice.
        var provider = ProviderRegistry.Get(_services.Settings.Settings.ApiProvider);
        if (_services.Settings.Settings.Model.Length == 0 && model == provider.DefaultModel) return;

        _services.Settings.Update(s =>
        {
            s.Model = model;
            if (model.Length > 0) s.RememberModel(s.ApiProvider, model);
        });

        if (model.Length > 0) EnsureModelInList(model);

        _services.Log.ConsoleLog(model.Length > 0
            ? L.F("sys.model.changed", model)
            : L.T("sys.model.default"));
    }

    private void OnBaseUrlLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var url = BaseUrlBox.Text?.Trim() ?? "";
        if (url == _services.Settings.Settings.CustomBaseUrl) return;

        _services.Settings.Update(s => s.CustomBaseUrl = url);

        // A custom/local endpoint is exactly where the base URL decides the list, so re-read it.
        _ = LoadModelsAsync(announce: false);
    }

    private async void OnRefreshModels(object sender, RoutedEventArgs e) => await LoadModelsAsync(announce: true);

    /// <summary>
    /// "Add" is also the validation test: the key is sent to the provider's model-list endpoint first and
    /// only stored when it answers. A rejected key never reaches user/.env, the reason appears right
    /// under the input, and a key that does answer loads the model list immediately.
    /// </summary>
    private async void OnAddKey(object sender, RoutedEventArgs e)
    {
        HideKeyError();

        var key = KeyBox.Password.Trim();
        if (key.Length == 0)
        {
            ShowKeyError(L.T("sys.key.empty"));
            return;
        }

        var providerId = _services.Settings.Settings.ApiProvider;
        var info = ProviderRegistry.Get(providerId);

        // Local servers (Ollama, LM Studio) run unauthenticated — there is nothing to verify against,
        // but the list still comes from the server, so it is reloaded either way.
        if (!info.RequiresKey)
        {
            ApiKeyPool.AddKey(AppPaths.EnvFile, providerId, key);
            KeyBox.Clear();
            RefreshKeys();
            HideKeyPrompt();
            KeyStatus.Text = L.T("sys.key.added");
            await LoadModelsAsync(announce: false);
            return;
        }

        AddKeyButton.IsEnabled = false;
        KeyStatus.Text = L.T("sys.key.checking");

        try
        {
            var result = await _services.Ocr.ValidateKeyAsync(key);
            if (!result.Success)
            {
                KeyStatus.Text = "";
                ShowKeyError(L.F("sys.key.invalid", result.Message));
                return;
            }

            ApiKeyPool.AddKey(AppPaths.EnvFile, providerId, key);
            KeyBox.Clear();
            RefreshKeys();
            HideKeyPrompt();
            KeyStatus.Text = L.T("sys.key.verified");

            // A valid key is the moment the list can be trusted, so it is fetched right away.
            await LoadModelsAsync(announce: false);
        }
        finally
        {
            AddKeyButton.IsEnabled = true;
        }
    }

    private void OnCopyKey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not KeyEntry entry) return;

        try
        {
            Clipboard.SetText(entry.Key);
            // The clipboard watcher polls: without this the app would run OCR on its own API key.
            _services.Clipboard.SuppressNext();
            KeyStatus.Text = L.T("sys.key.copied");
        }
        catch (Exception ex)
        {
            KeyStatus.Text = L.F("common.errorPrefix", ex.Message);
        }
    }

    private async void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not KeyEntry entry) return;

        var providerId = _services.Settings.Settings.ApiProvider;
        var all = ApiKeyPool.LoadAll(AppPaths.EnvFile);
        if (!all.TryGetValue(providerId, out var keys) || entry.Index >= keys.Count) return;

        keys.RemoveAt(entry.Index);
        ApiKeyPool.SaveAll(AppPaths.EnvFile, all);
        RefreshKeys();
        KeyStatus.Text = L.T("sys.key.removed");

        // The last key may have just gone: the list must not keep suggesting models nothing can call.
        await LoadModelsAsync(announce: false);
    }

    // ----------------------------------------------------------------- the rest

    private void OnStartWithSystem(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var wanted = StartWithSystemBox.IsChecked == true;
        var error = AutoStart.SetEnabled(wanted);
        if (error is not null)
        {
            KeyStatus.Text = L.F("sys.autostart.error", error);
            return;
        }

        _services.Settings.Update(s => s.StartWithSystem = wanted);
    }

    private void OnAutoUpdate(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.Settings.Update(s => s.AutoUpdateEnabled = AutoUpdateBox.IsChecked == true);
    }

    private void OnClipboardOption(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.Settings.Update(s =>
        {
            s.ProcessTextClipboard = ProcessTextBox.IsChecked == true;
            s.ProcessImageClipboard = ProcessImageBox.IsChecked == true;
            s.ProcessFileClipboard = ProcessFileBox.IsChecked == true;
        });
    }

    private void OnOpenUserFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserDir);
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.UserDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            KeyStatus.Text = ex.Message;
        }
    }

    // ------------------------------------------------------------------ update

    /// <summary>Latest answer from GitHub, kept so the section still shows it after a tab switch.</summary>
    private UpdateCheck? _check;

    private bool _updateBusy;

    /// <summary>Redraws the whole Update section from <see cref="_check"/>.</summary>
    private void RefreshUpdateSection()
    {
        UpdateCurrent.Text = L.F("sys.update.current", _services.Releases.CurrentVersion);
        UpdateLatest.Text = L.F("sys.update.latest", _check?.LatestVersion ?? "—");

        if (_check is not null) UpdateStatus.Text = Describe(_check);

        // The install button exists only when there is something to install: a release without a
        // package (or an older/equal version) must not offer a button that cannot work.
        InstallUpdateButton.Visibility = _check?.CanInstall == true ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = !_updateBusy;

        // A dev checkout has .git and can pull commits even when no release exists; a published build
        // has neither and simply never sees this button.
        GitUpdateButton.Visibility = GitUpdateService.IsCheckout(AppPaths.Root)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Turns a check/download result into one line of text in the user's language.</summary>
    private static string Describe(UpdateCheck check) =>
        check.MessageArg is null ? L.T(check.MessageKey) : L.F(check.MessageKey, check.MessageArg);

    private static string Describe(UpdateDownload download) =>
        download.MessageArg is null ? L.T(download.MessageKey) : L.F(download.MessageKey, download.MessageArg);

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;

        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        UpdateStatus.Text = L.T("sys.update.checking");
        RefreshUpdateSection();

        try
        {
            _check = await _services.Releases.CheckAsync();
            _services.Log.ConsoleLog($"[update] {Describe(_check)}");
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = true;
            RefreshUpdateSection();
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _check is not { Asset: { } asset } check) return;

        var answer = MessageBox.Show(L.F("sys.update.confirm", check.LatestVersion), "YuukaOCR",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _updateBusy = true;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        UpdateStatus.Text = L.T(GitHubUpdateService.KeyDownloading);
        RefreshUpdateSection();

        try
        {
            var target = GitHubUpdateService.PackagePath(check.LatestVersion ?? "latest");
            var progress = new Progress<double>(value => UpdateProgress.Value = value);
            var download = await _services.Releases.DownloadAsync(asset, target, progress);

            UpdateStatus.Text = Describe(download);
            if (!download.Success || download.Path is null) return;

            var install = _services.Releases.Install(download.Path, AppContext.BaseDirectory,
                "YuukaOCR.exe", Environment.ProcessId);

            UpdateStatus.Text = install.Success
                ? L.T(GitHubUpdateService.KeyInstallStarted)
                : L.F(GitHubUpdateService.KeyInstallFailed, install.MessageArg);

            if (install.Success)
            {
                // The script waits for this process to exit and then relaunches the new build.
                // user/ is untouched by design, so settings, keys, presets and history survive.
                Application.Current.Shutdown();
            }
        }
        finally
        {
            _updateBusy = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            RefreshUpdateSection();
        }
    }

    /// <summary>The pre-existing mechanism: pull commits in a working copy. Only shown in a checkout.</summary>
    private async void OnGitUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;

        _updateBusy = true;
        GitUpdateButton.IsEnabled = false;
        UpdateStatus.Text = L.T("sys.update.checking");

        try
        {
            var check = await _services.Updater.CheckAsync();
            UpdateStatus.Text = check.Message;

            if (!check.UpdateAvailable) return;

            var answer = MessageBox.Show(L.F("sys.update.confirm", check.Message), "YuukaOCR",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            var result = await _services.Updater.ApplyAsync();
            UpdateStatus.Text = result.Success
                ? L.T("sys.update.done")
                : L.F("sys.update.failed", result.Combined);
        }
        finally
        {
            _updateBusy = false;
            GitUpdateButton.IsEnabled = true;
            RefreshUpdateSection();
        }
    }
}
