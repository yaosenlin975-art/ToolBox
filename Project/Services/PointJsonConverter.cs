using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace ToolBox.Services;

public class PointJsonConverter : JsonConverter<Point>
{
    public override Point Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            // Old format: "1462, 422"
            var str = reader.GetString() ?? "0, 0";
            var parts = str.Split(',');
            double x = 0, y = 0;
            if (parts.Length >= 1) double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out x);
            if (parts.Length >= 2) double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out y);
            return new Point(x, y);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            // New format: {"X":1462,"Y":422}
            double x = 0, y = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return new Point(x, y);
                var prop = reader.GetString();
                reader.Read();
                if (prop == "X" || prop == "x") x = reader.GetDouble();
                else if (prop == "Y" || prop == "y") y = reader.GetDouble();
            }
        }

        return new Point(0, 0);
    }

    public override void Write(Utf8JsonWriter writer, Point value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"{value.X}, {value.Y}");
    }
}
