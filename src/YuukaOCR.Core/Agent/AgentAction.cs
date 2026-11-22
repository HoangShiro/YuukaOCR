using System.Text.Json.Serialization;

namespace YuukaOCR.Core.Agent;

/// <summary>
/// One registered action: an icon and the instruction that is executed when its button is pressed in
/// the result window's action bar.
///
/// An action is not a separate pipeline — it is a pre-composed chat turn sent through the same agent
/// (and therefore the same conversation) as the chat box.
/// </summary>
public sealed class AgentAction
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = AgentActions.DefaultIcon;
    [JsonPropertyName("instruction")] public string Instruction { get; set; } = "";

    public AgentAction Clone() => new() { Id = Id, Name = Name, Icon = Icon, Instruction = Instruction };

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? AgentActions.NewId() : Id.Trim();
        Name = (Name ?? "").Trim();
        Icon = AgentActions.NormalizeIcon(Icon);
        Instruction = (Instruction ?? "").Trim();
    }
}

/// <summary>
/// The action table's limits and the icon keys Core is allowed to store. The geometry behind each key
/// lives in the App layer (`Ui/Controls/ActionIcons.cs`) — Core must not reference WPF.
/// </summary>
public static class AgentActions
{
    public const int Max = 10;
    public const string DefaultIcon = "chat";

    /// <summary>Every icon an action may use, in the order the picker shows them.</summary>
    public static IReadOnlyList<string> Icons { get; } =
    [
        "chat", "translate", "summarize", "explain", "check", "list",
        "code", "search", "star", "bulb", "wand", "quill"
    ];

    public static string NormalizeIcon(string? icon) =>
        !string.IsNullOrWhiteSpace(icon) && Icons.Contains(icon.Trim(), StringComparer.Ordinal)
            ? icon.Trim()
            : DefaultIcon;

    public static string NewId() => "act-" + Guid.NewGuid().ToString("n")[..8];

    /// <summary>
    /// The instruction a shipped action starts with, or null for an action the user created. The
    /// settings tab uses this for *Reset*: a seeded action goes back to its default text, while an
    /// action with no predefined default has nothing to restore and keeps the button disabled.
    /// </summary>
    public static string? DefaultInstruction(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Defaults().FirstOrDefault(action => action.Id == id)?.Instruction;

    /// <summary>
    /// The three actions a fresh install ships with, so the action bar is not empty and the feature is
    /// discoverable. Deleting them all is preserved: seeds are only used for a brand-new settings file.
    /// </summary>
    public static List<AgentAction> Defaults() =>
    [
        new()
        {
            Id = "act-translate",
            Name = "Translate",
            Icon = "translate",
            Instruction = "Translate the context into the language of my message. Reply with the translation only."
        },
        new()
        {
            Id = "act-summarize",
            Name = "Summarize",
            Icon = "summarize",
            Instruction = "Summarize the context in at most five bullet points. Keep names, numbers and units exactly as they are."
        },
        new()
        {
            Id = "act-explain",
            Name = "Explain",
            Icon = "explain",
            Instruction = "Explain what the context means in plain language, and call out anything that looks like an error or a warning."
        }
    ];
}
