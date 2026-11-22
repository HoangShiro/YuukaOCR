using System.Text;
using System.Text.RegularExpressions;

namespace YuukaOCR.Core.Providers;

/// <summary>
/// Multi-key storage in user/.env, compatible with the old Python format:
///   PROVIDER_API_KEY=first
///   PROVIDER_API_KEY_1=first
///   PROVIDER_API_KEY_2=second
/// Keys are read/written here and nowhere else (the Python version had three parsers).
/// </summary>
public sealed partial class ApiKeyPool
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _cursor = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(?<base>[A-Z0-9_]+?)_(?<index>\d+)$")]
    private static partial Regex NumberedKeyRegex();

    public static string EnvKeyFor(string providerId) => ProviderRegistry.Get(providerId).EnvKey;

    /// <summary>
    /// One raw value from user/.env, for entries that are not provider keys — <c>GITHUB_TOKEN</c> is the
    /// only one today. Goes through the same parser as the key pool, so there is still exactly one
    /// place that knows the file format.
    /// </summary>
    public static string ReadValue(string envPath, string key) =>
        ReadEnv(envPath).TryGetValue(key, out var value) ? value.Trim() : "";

    public static Dictionary<string, List<string>> LoadAll(string envPath)
    {
        var kv = ReadEnv(envPath);
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in ProviderRegistry.All)
        {
            var baseKey = info.EnvKey;
            var keys = new List<string>();

            var numbered = kv.Keys
                .Select(key => (Key: key, Match: NumberedKeyRegex().Match(key)))
                .Where(x => x.Match.Success && x.Match.Groups["base"].Value == baseKey)
                .Select(x => (Index: int.Parse(x.Match.Groups["index"].Value), Value: kv[x.Key]))
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .OrderBy(x => x.Index)
                .Select(x => x.Value.Trim())
                .ToList();

            keys.AddRange(numbered);
            if (kv.TryGetValue(baseKey, out var single) && !string.IsNullOrWhiteSpace(single))
                keys.Add(single.Trim());

            if (info.Id == "gemini")
            {
                foreach (var legacy in new[] { "GOOGLE_API_KEY", "API_KEY" })
                {
                    if (kv.TryGetValue(legacy, out var value) && !string.IsNullOrWhiteSpace(value))
                        keys.Add(value.Trim());
                }
            }

            var deduped = Dedupe(keys);
            if (deduped.Count > 0) result[info.Id] = deduped;
        }

        return result;
    }

    public static List<string> LoadFor(string envPath, string providerId) =>
        LoadAll(envPath).TryGetValue(providerId, out var keys) ? keys : new List<string>();

    public static void SaveAll(string envPath, Dictionary<string, List<string>> keysByProvider)
    {
        var kv = ReadEnv(envPath);

        foreach (var info in ProviderRegistry.All)
        {
            var baseKey = info.EnvKey;
            foreach (var key in kv.Keys.ToList())
            {
                var match = NumberedKeyRegex().Match(key);
                if (key == baseKey || (match.Success && match.Groups["base"].Value == baseKey))
                    kv.Remove(key);
            }
            if (info.Id == "gemini") kv.Remove("GOOGLE_API_KEY");
        }

        foreach (var (providerId, rawKeys) in keysByProvider)
        {
            if (!ProviderRegistry.Exists(providerId)) continue;
            var baseKey = EnvKeyFor(providerId);
            var clean = Dedupe(rawKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()));
            for (var i = 0; i < clean.Count; i++) kv[$"{baseKey}_{i + 1}"] = clean[i];
            if (clean.Count > 0)
            {
                kv[baseKey] = clean[0];
                if (providerId == "gemini") kv["GOOGLE_API_KEY"] = clean[0];
            }
        }

        WriteEnv(envPath, kv);
    }

    public static List<string> AddKey(string envPath, string providerId, string apiKey)
    {
        var key = (apiKey ?? "").Trim();
        if (key.Length == 0) return new List<string>();

        var all = LoadAll(envPath);
        var current = all.TryGetValue(providerId, out var existing) ? existing : new List<string>();
        var updated = Dedupe(current.Append(key));
        all[providerId] = updated;
        SaveAll(envPath, all);
        return updated;
    }

    public static void RemoveProvider(string envPath, string providerId)
    {
        var all = LoadAll(envPath);
        if (all.Remove(providerId)) SaveAll(envPath, all);
    }

    /// <summary>Round-robin over the provider's keys. Thread-safe; returns "" when the pool is empty.</summary>
    public string Next(string providerId, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return "";
        lock (_lock)
        {
            var index = _cursor.TryGetValue(providerId, out var current) ? current % keys.Count : 0;
            _cursor[providerId] = (index + 1) % keys.Count;
            return keys[index];
        }
    }

    public void ResetCursor(string? providerId = null)
    {
        lock (_lock)
        {
            if (providerId is null) _cursor.Clear();
            else _cursor.Remove(providerId);
        }
    }

    public static string Mask(string? key) =>
        string.IsNullOrEmpty(key) ? "*****" : key.Length <= 6 ? "*****" : $"*****{key[^4..]}";

    private static List<string> Dedupe(IEnumerable<string> keys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var trimmed = key.Trim();
            if (seen.Add(trimmed)) result.Add(trimmed);
        }
        return result;
    }

    private static Dictionary<string, string> ReadEnv(string envPath)
    {
        var kv = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(envPath)) return kv;
            foreach (var raw in File.ReadAllLines(envPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var split = line.IndexOf('=');
                if (split <= 0) continue;
                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();
                if (key.Length > 0) kv[key] = value;
            }
        }
        catch (IOException)
        {
            // Unreadable .env behaves like an empty one.
        }
        return kv;
    }

    private static void WriteEnv(string envPath, Dictionary<string, string> kv)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(envPath)!);
            var sb = new StringBuilder();
            sb.AppendLine("# YuukaOCR API keys - one provider per block, numbered for rotation");
            foreach (var (key, value) in kv.OrderBy(x => x.Key, StringComparer.Ordinal))
                sb.AppendLine($"{key}={value}");

            var tmp = envPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, envPath, overwrite: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Cannot write .env: {ex.Message}");
        }
    }
}
