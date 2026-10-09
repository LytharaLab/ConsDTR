using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConsDTR.Runtime;

/// <summary>Typed, length-prefixed canonical bytes. JSON is a strict interchange representation only.</summary>
public static class DtrCanonical
{
    internal static UTF8Encoding StrictUtf8 { get; } = new(false, true);
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 128
        };
        options.Converters.Add(new StrictStringConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    public static T Parse<T>(string json)
    {
        EncodeJson(json); // Reject repeated keys and unsupported numeric encodings before materialization.
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidDataException("Null canonical object.");
    }
    public static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(Encode(value)));
    public static byte[] Encode<T>(T value)
    {
        using var document = JsonDocument.Parse(Json(value), new JsonDocumentOptions { MaxDepth = 128 });
        using var output = new MemoryStream();
        Write(document.RootElement, output);
        return output.ToArray();
    }

    public static byte[] EncodeJson(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        using var output = new MemoryStream();
        Write(document.RootElement, output);
        return output.ToArray();
    }

    private static void Length(Stream stream, int length)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, length);
        stream.Write(bytes);
    }

    private static void Text(Stream stream, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        Length(stream, bytes.Length);
        stream.Write(bytes);
    }

    private sealed class StrictStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            var value = reader.GetString()!;
            Validate(value); return value;
        }
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        { Validate(value); writer.WriteStringValue(value); }
        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        { var value = reader.GetString()!; Validate(value); return value; }
        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        { Validate(value); writer.WritePropertyName(value); }
        private static void Validate(string value)
        {
            try { StrictUtf8.GetByteCount(value); }
            catch (EncoderFallbackException error) { throw new InvalidDataException("Canonical strings must be valid Unicode.", error); }
        }
    }

    private static void Write(JsonElement value, Stream output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null: output.WriteByte(0); break;
            case JsonValueKind.False: output.WriteByte(1); break;
            case JsonValueKind.True: output.WriteByte(2); break;
            case JsonValueKind.String: output.WriteByte(3); Text(output, value.GetString()!); break;
            case JsonValueKind.Number:
                output.WriteByte(4);
                if (!value.TryGetInt64(out var number)) throw new InvalidDataException("Canonical numeric fields must be signed 64-bit integers.");
                Span<byte> bytes = stackalloc byte[8];
                BinaryPrimitives.WriteInt64BigEndian(bytes, number);
                output.Write(bytes);
                break;
            case JsonValueKind.Array:
                output.WriteByte(5); Length(output, value.GetArrayLength());
                foreach (var element in value.EnumerateArray()) Write(element, output);
                break;
            case JsonValueKind.Object:
                output.WriteByte(6);
                var properties = value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new InvalidDataException("Duplicate canonical object key.");
                Length(output, properties.Length);
                foreach (var property in properties) { Text(output, property.Name); Write(property.Value, output); }
                break;
            default: throw new InvalidDataException("Unsupported canonical field.");
        }
    }
}
