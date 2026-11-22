namespace YuukaOCR.Core.Settings;

/// <summary>
/// Single source of truth for user settings. Nothing else in the app keeps its own copy,
/// which is what went wrong in the Python version (MainWindow and the plugin drifted apart).
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly object _lock = new();
    private readonly System.Threading.Timer _saveTimer;
    private bool _dirty;

    public AppSettings Settings { get; private set; } = new();
    public string FilePath { get; }

    /// <summary>Raised after settings changed in memory (before the debounced save).</summary>
    public event EventHandler? Changed;

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? AppPaths.SettingsFile;
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Load()
    {
        lock (_lock)
        {
            Settings = LoadFromDisk();
            Settings.Normalize();
        }
    }

    private AppSettings LoadFromDisk()
    {
        // Preferred file first, then the legacy Python file name.
        foreach (var path in new[] { FilePath, AppPaths.LegacySettingsFile })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(path), Json.Options);
                if (loaded is not null) return loaded;
            }
            catch (Exception)
            {
                // Corrupt file: try the next candidate, then fall back to defaults.
            }
        }
        return new AppSettings();
    }

    public void Update(Action<AppSettings> mutate, bool save = true)
    {
        lock (_lock)
        {
            mutate(Settings);
            Settings.Normalize();
            _dirty = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (save) SaveSoon();
    }

    /// <summary>Persists after a short quiet period so slider drags do not hammer the disk.</summary>
    public void SaveSoon(int delayMs = 700)
    {
        _dirty = true;
        _saveTimer.Change(delayMs, Timeout.Infinite);
    }

    public void Save()
    {
        lock (_lock)
        {
            WriteAtomic(Settings);
            _dirty = false;
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            WriteAtomic(Settings);
            _dirty = false;
        }
    }

    private void WriteAtomic(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureDirs();
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(settings, Json.Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Saving must never crash the app; surface it on stderr for the launcher log.
            Console.Error.WriteLine($"Settings save failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Flush();
        _saveTimer.Dispose();
    }
}
