using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Agent;

/// <summary>One stored conversation turn. <see cref="Role"/> is <c>user</c> or <c>assistant</c>.</summary>
public sealed record ChatTurn(string Role, string Text, string Stamp)
{
    public bool IsUser => Role == AgentPrompts.UserRole;
}

/// <summary>What a chat or action request produced.</summary>
public sealed record AgentReply(bool Ok, string Text, string Error = "")
{
    public static AgentReply Success(string text) => new(true, text);

    public static AgentReply Fail(string error) => new(false, "", error);
}

/// <summary>
/// Pure composition helpers for the agent: how a request is put together, how the history is trimmed
/// to the token budget, and how an action becomes a chat turn.
///
/// Kept static and free of I/O so the rules ("never drop the system prompt", "oldest turns go first",
/// "an action carries the global instruction too") can be tested without a network or a window.
/// </summary>
public static class AgentPrompts
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";

    /// <summary>Rough characters-per-token ratio. Good enough for a budget, and no tokenizer needed.</summary>
    public const int CharsPerToken = 4;

    public static int TokensOf(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + CharsPerToken - 1) / CharsPerToken;

    /// <summary>
    /// System prompt = the agent instruction plus the OCR context. The context lives in the system
    /// message so it is stated once per request instead of being repeated in every user turn.
    /// </summary>
    public static string SystemPrompt(string? instruction, string? context, string? contextLabel = null)
    {
        var parts = new List<string>();

        var persona = (instruction ?? "").Trim();
        if (persona.Length == 0) persona = Settings.AgentSettings.DefaultInstruction;
        parts.Add(persona);

        var body = (context ?? "").Trim();
        if (body.Length > 0)
        {
            var label = string.IsNullOrWhiteSpace(contextLabel) ? L.T("agent.context") : contextLabel!;
            parts.Add($"{label}:\n{body}");
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Newest turns first are kept until the budget is used up; the oldest are dropped. A single turn
    /// larger than the whole budget is truncated rather than dropped, because it is usually the one the
    /// user is asking about. At least one turn survives so a request is never empty.
    /// </summary>
    public static IReadOnlyList<ChatTurn> TrimHistory(IReadOnlyList<ChatTurn> history, int budgetTokens)
    {
        if (history.Count == 0) return [];

        var budget = Math.Max(1, budgetTokens);
        var kept = new List<ChatTurn>();
        var used = 0;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            var turn = history[i];
            var cost = TokensOf(turn.Text);

            if (used + cost <= budget)
            {
                kept.Insert(0, turn);
                used += cost;
                continue;
            }

            if (kept.Count == 0)
            {
                // Nothing kept yet: this turn alone is over budget, so cut it down instead of losing it.
                var maxChars = Math.Max(1, budget * CharsPerToken);
                var text = turn.Text.Length <= maxChars ? turn.Text : turn.Text[..maxChars] + "…";
                kept.Insert(0, turn with { Text = text });
            }

            break;
        }

        return kept;
    }

    /// <summary>
    /// The user turn an action produces: the action's own instruction, followed by the context it
    /// should act on. The global instruction travels as the system prompt (see <see cref="SystemPrompt"/>),
    /// so an action never has to repeat the persona.
    /// </summary>
    public static string ComposeAction(string? actionInstruction, string? context, string? contextLabel = null)
    {
        var instruction = (actionInstruction ?? "").Trim();
        if (instruction.Length == 0) instruction = L.T("agent.action.empty");

        var body = (context ?? "").Trim();
        if (body.Length == 0) return instruction;

        var label = string.IsNullOrWhiteSpace(contextLabel) ? L.T("agent.context") : contextLabel!;
        return $"{instruction}\n\n{label}:\n{body}";
    }
}
