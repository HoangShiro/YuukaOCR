using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuukaOCR.Core.Agent;

/// <summary>
/// The action preset file: a portable copy of the user's action tabs, so a configuration can be moved
/// between machines or kept as a backup.
///
/// Shape (JSON, UTF-8):
/// <code>
/// {
///   "format": "yuukaocr.actions",
///   "version": 1,
///   "exported_at": "2026-11-22T10:00:00Z",
///   "actions": [
///     { "name": "Translate", "icon": "translate", "instruction": "Translate the context ..." }
///   ]
/// }
/// </code>
///
/// Ids are deliberately **not** stored: an id identifies an action inside one settings file, and a
/// preset is content. Importing assigns fresh ids, which also makes "import the same file twice"
/// impossible to turn into duplicate ids.
///
/// Parsing is total: every failure comes back as a localization key plus its argument, never as an
/// exception, so a broken file can never damage the configuration that is already loaded.
/// </summary>
public static class AgentPreset
{
    public const string Format = "yuukaocr.actions";
    public const int Version = 1;

    /// <summary>Localization key prefix used by every result the UI shows.</summary>
    public const string ErrorEmpty = "preset.error.empty";
    public const string ErrorJson = "preset.error.json";
    public const string ErrorFormat = "preset.error.format";
    public const string ErrorVersion = "preset.error.version";
    public const string ErrorCount = "preset.error.count";
    public const string ErrorShape = "preset.error.shape";

    /// <summary>Serializes the actions (in tab order) into the preset document.</summary>
    public static string Export(IEnumerable<AgentAction> actions, string exportedAt)
    {
        var document = new PresetDocument
        {
            Format = Format,
            Version = Version,
            ExportedAt = exportedAt,
            Actions = actions.Select(action => new PresetAction
            {
                Name = action.Name,
                Icon = AgentActions.NormalizeIcon(action.Icon),
                Instruction = action.Instruction
            }).ToList<PresetAction?>()
        };

        return JsonSerializer.Serialize(document, PresetJson.Options);
    }

    /// <summary>
    /// Reads a preset. On failure <see cref="PresetLoadResult.ErrorKey"/> names the reason and
    /// <see cref="PresetLoadResult.Actions"/> is empty — the caller must keep its current table.
    /// </summary>
    public static PresetLoadResult Import(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return PresetLoadResult.Fail(ErrorEmpty);

        PresetDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<PresetDocument>(json, PresetJson.Options);
        }
        catch (JsonException ex)
        {
            return PresetLoadResult.Fail(ErrorJson, Short(ex.Message));
        }

        if (document is null)
            return PresetLoadResult.Fail(ErrorEmpty);

        if (!string.Equals(document.Format, Format, StringComparison.OrdinalIgnoreCase))
            return PresetLoadResult.Fail(ErrorFormat, document.Format ?? "");

        // A file written by a future build may hold fields this one cannot represent; refusing is
        // safer than silently dropping half of the user's configuration.
        if (document.Version <= 0 || document.Version > Version)
            return PresetLoadResult.Fail(ErrorVersion, document.Version.ToString());

        if (document.Actions is null || document.Actions.Count == 0)
            return PresetLoadResult.Fail(ErrorCount, "0");

        if (document.Actions.Count > AgentActions.Max)
            return PresetLoadResult.Fail(ErrorCount, document.Actions.Count.ToString());

        var actions = new List<AgentAction>(document.Actions.Count);
        foreach (var entry in document.Actions)
        {
            if (entry is null)
                return PresetLoadResult.Fail(ErrorShape);

            // A missing instruction is kept: the settings tab already treats it as a tab the user is
            // still writing, and the result window simply does not draw a button for it.
            actions.Add(new AgentAction
            {
                Id = AgentActions.NewId(),
                Name = (entry.Name ?? "").Trim(),
                Icon = AgentActions.NormalizeIcon(entry.Icon),
                Instruction = (entry.Instruction ?? "").Trim()
            });
        }

        return PresetLoadResult.Ok(actions);
    }

    /// <summary>A default file name that sorts chronologically, e.g. <c>yuukaocr-actions-20261122-1017.json</c>.</summary>
    public static string SuggestedFileName(DateTime when) =>
        $"yuukaocr-actions-{when:yyyyMMdd-HHmm}.json";

    private static string Short(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 120 ? single : single[..120] + "...";
    }

    // ---------------------------------------------------------------- document

    private sealed class PresetDocument
    {
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("exported_at")] public string? ExportedAt { get; set; }
        [JsonPropertyName("actions")] public List<PresetAction?>? Actions { get; set; }
    }

    private sealed class PresetAction
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("icon")] public string? Icon { get; set; }
        [JsonPropertyName("instruction")] public string? Instruction { get; set; }
    }

    /// <summary>Indented, camel-cased, and tolerant of comments — the same rules as settings.json.</summary>
    private static class PresetJson
    {
        public static readonly JsonSerializerOptions Options = new(Json.Options)
        {
            WriteIndented = true
        };
    }
}

/// <summary>Outcome of reading a preset file: either the actions, or the reason it was refused.</summary>
public sealed record PresetLoadResult(bool Success, List<AgentAction> Actions, string? ErrorKey, string? ErrorArg)
{
    public static PresetLoadResult Ok(List<AgentAction> actions) => new(true, actions, null, null);

    public static PresetLoadResult Fail(string errorKey, string? errorArg = null) =>
        new(false, new List<AgentAction>(), errorKey, errorArg);
}
