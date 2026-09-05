using System.Text.Json;
using System.Text.Json.Serialization;

namespace DurableLinq;

/// <summary>Converts step results, inputs, and signals to bytes. Stores only ever see <c>byte[]</c>.</summary>
public interface IWorkflowSerializer
{
    byte[] Serialize<T>(T value);
    T Deserialize<T>(byte[] data);
    byte[] Serialize(object? value, Type type);
    object? Deserialize(byte[] data, Type type);
}

/// <summary>Default serializer. System.Text.Json with web defaults and enums as strings.</summary>
public sealed class JsonWorkflowSerializer : IWorkflowSerializer
{
    public static readonly JsonSerializerOptions DefaultOptions = CreateDefaultOptions();
    public static readonly JsonWorkflowSerializer Instance = new();

    private readonly JsonSerializerOptions _options;

    public JsonWorkflowSerializer(JsonSerializerOptions? options = null) => _options = options ?? DefaultOptions;

    public static JsonSerializerOptions CreateDefaultOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, _options);
    public T Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data, _options)!;
    public byte[] Serialize(object? value, Type type) => JsonSerializer.SerializeToUtf8Bytes(value, type, _options);
    public object? Deserialize(byte[] data, Type type) => JsonSerializer.Deserialize(data, type, _options);
}
