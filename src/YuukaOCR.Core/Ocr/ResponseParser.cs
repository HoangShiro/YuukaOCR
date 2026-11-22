using System.Text;
using System.Text.Json;
using YuukaOCR.Core.Localization;

namespace YuukaOCR.Core.Ocr;

public sealed record ParseOutcome(bool Ok, string Text, JsonElement? Data, string? Error)
{
    public static ParseOutcome Success(string text, JsonElement? data = null) => new(true, text, data, null);
    public static ParseOutcome Failure(string error) => new(false, "", null, error);
}

/// <summary>
/// Turns whatever the model returned into plain text. Models wrap JSON in code fences, truncate it
/// when they hit the token limit, or ignore the format entirely — all three are handled, because a
/// failed OCR run must still show the user something usable.
/// </summary>
public static class ResponseParser
{
    public static ParseOutcome Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ParseOutcome.Failure(L.T("parser.empty"));

        var text = StripFences(raw.Trim());
        if (text.Length == 0) return ParseOutcome.Failure(L.T("parser.empty"));

        if (TryParseObject(text, out var data, out var extracted))
            return ParseOutcome.Success(extracted, data);

        if (TryRepairTruncated(text, out var repaired))
            return ParseOutcome.Success(repaired);

        // Last resort: the model answered in prose. Use it rather than failing the whole run.
        return ParseOutcome.Success(text);
    }

    private static string StripFences(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewline = text.IndexOf('\n');
        var body = firstNewline >= 0 ? text[(firstNewline + 1)..] : text[3..];
        var fence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fence >= 0 ? body[..fence] : body).Trim();
    }

    private static bool TryParseObject(string text, out JsonElement? data, out string extracted)
    {
        data = null;
        extracted = "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            switch (root.ValueKind)
            {
                case JsonValueKind.Object:
                    data = root.Clone();
                    extracted = ExtractFromObject(root);
                    if (extracted.Length == 0) extracted = text; // keep the JSON visible for debugging
                    return true;

                case JsonValueKind.String:
                    extracted = root.GetString() ?? "";
                    return extracted.Length > 0;

                case JsonValueKind.Array:
                    var joined = string.Join("\n", root.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (joined.Length == 0) return false;
                    extracted = joined;
                    return true;

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ExtractFromObject(JsonElement root)
    {
        foreach (var key in new[] { "extracted_text", "translated_text", "text", "result", "content" })
        {
            if (!root.TryGetProperty(key, out var value)) continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var str = value.GetString() ?? "";
                // The model sometimes nests a JSON string inside the field.
                if (str.TrimStart().StartsWith('{'))
                {
                    var inner = ExtractFromJsonString(str);
                    if (inner.Length > 0) return inner;
                }
                if (str.Length > 0) return str;
            }
            else if (value.ValueKind == JsonValueKind.Array || value.ValueKind == JsonValueKind.Object)
            {
                var nested = ExtractFromObject(value.ValueKind == JsonValueKind.Object
                    ? value
                    : value.EnumerateArray().FirstOrDefault());
                if (nested.Length > 0) return nested;
            }
        }
        return "";
    }

    private static string ExtractFromJsonString(string jsonString)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return ExtractFromObject(doc.RootElement);
        }
        catch (JsonException)
        {
            // Not valid JSON after all; caller falls back to the raw string.
        }
        return "";
    }

    /// <summary>Recovers the text from a response cut off mid-string by the token limit.</summary>
    private static bool TryRepairTruncated(string text, out string extracted)
    {
        extracted = "";
        var marker = text.IndexOf("\"extracted_text\"", StringComparison.Ordinal);
        if (marker < 0) return false;

        var colon = text.IndexOf(':', marker);
        if (colon < 0) return false;

        var startQuote = text.IndexOf('"', colon);
        if (startQuote < 0) return false;

        var endQuote = -1;
        for (var i = text.Length - 1; i > startQuote; i--)
        {
            if (text[i] == '"' && !IsEscaped(text, i))
            {
                endQuote = i;
                break;
            }
        }

        if (endQuote <= startQuote) return false;
        extracted = Unescape(text[(startQuote + 1)..endQuote]).Trim();
        return extracted.Length > 0;
    }

    private static bool IsEscaped(string s, int index)
    {
        var backslashes = 0;
        for (var i = index - 1; i >= 0 && s[i] == '\\'; i--) backslashes++;
        return backslashes % 2 == 1;
    }

    private static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }

            var next = value[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'u' when i + 4 < value.Length:
                    if (int.TryParse(value.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    else
                    {
                        sb.Append("\\u");
                    }
                    break;
                default:
                    sb.Append('\\').Append(next);
                    break;
            }
        }
        return sb.ToString();
    }
}
