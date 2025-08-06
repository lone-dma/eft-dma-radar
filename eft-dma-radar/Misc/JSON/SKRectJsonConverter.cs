﻿namespace eft_dma_radar.Misc.JSON
{
    public class SKRectJsonConverter : JsonConverter<SKRect>
    {
        public override SKRect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected StartObject token for SKRect.");

            float left = 0, top = 0, right = 0, bottom = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return new SKRect(left, top, right, bottom);

                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("Expected PropertyName token.");

                string propertyName = reader.GetString()!;
                reader.Read(); // Move to the value token.

                switch (propertyName)
                {
                    case nameof(SKRect.Left): left = reader.GetSingle(); break;
                    case nameof(SKRect.Top): top = reader.GetSingle(); break;
                    case nameof(SKRect.Right): right = reader.GetSingle(); break;
                    case nameof(SKRect.Bottom): bottom = reader.GetSingle(); break;
                    default: reader.Skip(); break;
                }
            }

            throw new JsonException("Unexpected end of JSON for SKRect.");
        }

        public override void Write(Utf8JsonWriter writer, SKRect value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber(nameof(SKRect.Left), value.Left);
            writer.WriteNumber(nameof(SKRect.Top), value.Top);
            writer.WriteNumber(nameof(SKRect.Right), value.Right);
            writer.WriteNumber(nameof(SKRect.Bottom), value.Bottom);
            writer.WriteEndObject();
        }
    }
}
