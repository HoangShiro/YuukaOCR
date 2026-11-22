namespace YuukaOCR.Core.Ocr;

public static class Prompts
{
    public const string Image =
        "Extract all text from this image. Respond in a strict JSON format like this: " +
        "{\"extracted_text\": \"all the text you found\"}.";

    public const string File =
        "Analyze this file and extract all text from it. Respond in a strict JSON format like this: " +
        "{\"extracted_text\": \"all the text you found\"}.";

    public const string Text =
        "Process the following text. If a user instruction is provided, follow it. Otherwise, summarize the text. " +
        "Respond in a strict JSON format like this: {\"extracted_text\": \"your complete response here\"}.";
}

public sealed record PromptPlan(string Prompt, string? CustomInstruction)
{
    public bool HasCustom => !string.IsNullOrWhiteSpace(CustomInstruction);
}

public static class PromptBuilder
{
    /// <summary>
    /// Base instruction + the user's custom instruction (only when the toggle is on).
    /// The custom text is returned separately so it can be logged exactly once, when used.
    /// </summary>
    public static PromptPlan Build(string basePrompt, bool promptEnabled, string? customPrompt)
    {
        var custom = promptEnabled ? (customPrompt ?? "").Trim() : "";
        if (custom.Length == 0) return new PromptPlan(basePrompt, null);

        return new PromptPlan(
            $"{basePrompt}\n\nAdditionally, strictly follow this user instruction: {custom}",
            custom);
    }
}
