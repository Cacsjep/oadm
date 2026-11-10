using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.Vision.Model;

/// <summary>
/// The JSON of <see cref="RelativeBox"/> and <see cref="RelativePoint"/> (docs/spec/vision-center.md "Page"): only their
/// own values, each rounded to 1/100,000 of the picture (0.01 pixel of a 1280 picture). A preview's state carries hundreds
/// of boxes and trail points up to 25 times a second; the computed edges and centres and seventeen digits per number were
/// most of it (2026-11-09).
/// </summary>
internal static class RelativeJson
{
    public const int Digits = 5;

    public static double Round(double value) => Math.Round(value, Digits);

    public static double Read(ref Utf8JsonReader reader) => reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : 0;
}

internal sealed class RelativeBoxJson : JsonConverter<RelativeBox>
{
    public override RelativeBox Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A box is an object.");
        }

        double x = 0, y = 0, width = 0, height = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name?.ToUpperInvariant())
            {
                case "X":
                    x = RelativeJson.Read(ref reader);
                    break;
                case "Y":
                    y = RelativeJson.Read(ref reader);
                    break;
                case "WIDTH":
                    width = RelativeJson.Read(ref reader);
                    break;
                case "HEIGHT":
                    height = RelativeJson.Read(ref reader);
                    break;
                default:
                    reader.Skip(); // the edges and centres an older server wrote
                    break;
            }
        }

        return new RelativeBox(x, y, width, height);
    }

    public override void Write(Utf8JsonWriter writer, RelativeBox value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteNumber("x", RelativeJson.Round(value.X));
        writer.WriteNumber("y", RelativeJson.Round(value.Y));
        writer.WriteNumber("width", RelativeJson.Round(value.Width));
        writer.WriteNumber("height", RelativeJson.Round(value.Height));
        writer.WriteEndObject();
    }
}

internal sealed class RelativePointJson : JsonConverter<RelativePoint>
{
    public override RelativePoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A point is an object.");
        }

        double x = 0, y = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name?.ToUpperInvariant())
            {
                case "X":
                    x = RelativeJson.Read(ref reader);
                    break;
                case "Y":
                    y = RelativeJson.Read(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new RelativePoint(x, y);
    }

    public override void Write(Utf8JsonWriter writer, RelativePoint value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteNumber("x", RelativeJson.Round(value.X));
        writer.WriteNumber("y", RelativeJson.Round(value.Y));
        writer.WriteEndObject();
    }
}
