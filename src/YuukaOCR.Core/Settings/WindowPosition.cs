using System.Text.Json.Serialization;

namespace YuukaOCR.Core.Settings;

public sealed class WindowPosition
{
    [JsonPropertyName("x")] public int X { get; set; } = 50;
    [JsonPropertyName("y")] public int Y { get; set; } = 50;
}
