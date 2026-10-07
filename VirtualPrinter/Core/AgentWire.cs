using System.Buffers.Binary;
using System.Text.Json;

namespace XTReader.Printing;

public sealed record Delivery(string InstanceId, int JobId, string Title, int Copies, long Length);
public static class AgentWire
{
    public const string PipeName = "XTReader_PrintBroker_v1";
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value), prefix = new byte[4];
        if (json.Length > 16384) throw new InvalidDataException("Message too long");
        BinaryPrimitives.WriteInt32LittleEndian(prefix, json.Length);
        await stream.WriteAsync(prefix, token); await stream.WriteAsync(json, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is < 1 or > 16384) throw new InvalidDataException("Invalid message length");
        byte[] json = new byte[size]; await stream.ReadExactlyAsync(json, token);
        return JsonSerializer.Deserialize<T>(json)!;
    }
}
