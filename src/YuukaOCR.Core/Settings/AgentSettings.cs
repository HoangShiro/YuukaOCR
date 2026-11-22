using System.Text.Json.Serialization;

namespace YuukaOCR.Core.Settings;

/// <summary>
/// The chat agent's own settings, separate from the OCR pipeline: a persona instruction, the
/// provider/model it talks to (empty = follow the OCR provider), the sampling temperature and the
/// history budget in approximate tokens.
///
/// The agent is used by the action bar's chat box *and* by every registered action, so these values
/// apply to both — an action is a pre-composed chat turn, not a second pipeline.
/// </summary>
public sealed class AgentSettings
{
    /// <summary>Default persona. Kept short: it is prepended to every request, so it is paid for each time.</summary>
    public const string DefaultInstruction =
        "You are Yuuka, a concise OCR assistant. The context below is text that was just read from the " +
        "user's screen. Answer in the language the user writes in, keep replies short and factual, and " +
        "never invent text that is not in the context.";

    [JsonPropertyName("instruction")] public string Instruction { get; set; } = DefaultInstruction;

    /// <summary>Provider id, or an empty string to reuse <see cref="AppSettings.ApiProvider"/>.</summary>
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";

    /// <summary>Model name, or an empty string to reuse the provider's default model.</summary>
    [JsonPropertyName("model")] public string Model { get; set; } = "";

    [JsonPropertyName("temperature")] public double Temperature { get; set; } =
        SliderSpecs.Default(SliderSpecs.AgentTemperature);

    /// <summary>Approximate token budget for the conversation history sent with each request.</summary>
    [JsonPropertyName("context_tokens")] public int ContextTokens { get; set; } =
        (int)SliderSpecs.Default(SliderSpecs.AgentContextTokens);

    public void Normalize()
    {
        Instruction ??= DefaultInstruction;
        Provider = (Provider ?? "").Trim().ToLowerInvariant();
        Model = (Model ?? "").Trim();
        Temperature = SliderSpecs.Snap(SliderSpecs.AgentTemperature, Temperature);
        ContextTokens = (int)SliderSpecs.Snap(SliderSpecs.AgentContextTokens, ContextTokens);
    }

    public AgentSettings Clone() => new()
    {
        Instruction = Instruction,
        Provider = Provider,
        Model = Model,
        Temperature = Temperature,
        ContextTokens = ContextTokens
    };
}
