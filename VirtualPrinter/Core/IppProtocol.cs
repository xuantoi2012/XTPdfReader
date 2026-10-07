using System.Buffers.Binary;
using System.Text;

namespace XTReader.Printing;

public sealed record IppAttribute(byte Group, byte Tag, string Name, byte[] Value)
{
    public string Text => Encoding.UTF8.GetString(Value);
    public int Integer => Value.Length == 4 ? BinaryPrimitives.ReadInt32BigEndian(Value) : throw new IppException(0x0400, "Invalid integer");
}

public sealed record IppRequest(byte Major, byte Minor, ushort Operation, int RequestId, IReadOnlyList<IppAttribute> Attributes)
{
    public IppAttribute? Find(string name) => Attributes.FirstOrDefault(a => a.Name == name);
    public string Text(string name, string fallback = "") => Find(name)?.Text ?? fallback;
    public int Integer(string name, int fallback) => Find(name)?.Integer ?? fallback;
}

public sealed class IppException(ushort status, string message) : Exception(message)
{
    public ushort Status { get; } = status;
}

public static class IppProtocol
{
    // Read attributes only; leave the document in the stream for bounded-memory spooling.
    public static async Task<IppRequest> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header, token);
        if (header[0] is not (1 or 2) || (header[0] == 1 && header[1] != 1) || (header[0] == 2 && header[1] != 0))
            throw new IppException(0x0503, "Unsupported IPP version");
        var attrs = new List<IppAttribute>();
        byte group = 0;
        string name = "";
        int consumed = 8;
        byte[] one = new byte[1], length = new byte[2];
        while (true)
        {
            await stream.ReadExactlyAsync(one, token); consumed++;
            if (consumed > 65536) throw new IppException(0x0400, "Attribute budget exceeded");
            byte tag = one[0];
            if (tag == 3) break;
            if (tag < 0x10)
            {
                if (tag is not (1 or 2 or 4 or 5)) throw new IppException(0x0400, "Invalid group");
                group = tag; name = ""; continue;
            }
            if (group == 0) throw new IppException(0x0400, "Missing attribute group");
            await stream.ReadExactlyAsync(length, token); consumed += 2;
            int n = BinaryPrimitives.ReadUInt16BigEndian(length);
            if (n > 255) throw new IppException(0x0400, "Attribute name too long");
            byte[] bytes = new byte[n]; await stream.ReadExactlyAsync(bytes, token); consumed += n;
            if (n > 0) name = Encoding.UTF8.GetString(bytes);
            else if (name.Length == 0) throw new IppException(0x0400, "Unnamed attribute");
            await stream.ReadExactlyAsync(length, token); consumed += 2;
            n = BinaryPrimitives.ReadUInt16BigEndian(length);
            if (consumed + n > 65536 || attrs.Count >= 256) throw new IppException(0x0400, "Attribute budget exceeded");
            bytes = new byte[n]; await stream.ReadExactlyAsync(bytes, token); consumed += n;
            attrs.Add(new(group, tag, name, bytes));
        }
        if (attrs.FirstOrDefault()?.Name != "attributes-charset" || attrs.ElementAtOrDefault(1)?.Name != "attributes-natural-language")
            throw new IppException(0x0400, "Missing charset or language");
        if (attrs[0].Text != "utf-8") throw new IppException(0x040D, "Only UTF-8 is supported");
        return new(header[0], header[1], BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)), attrs);
    }
}

public sealed class IppResponse : IDisposable
{
    private readonly MemoryStream stream = new();
    public IppResponse(int requestId, ushort status = 0, byte major = 2, byte minor = 0)
    {
        stream.Write([major, minor]); Write16(status); Write32(requestId);
        Group(1); Text(0x47, "attributes-charset", "utf-8"); Text(0x48, "attributes-natural-language", "en");
    }
    public IppResponse Group(byte group) { stream.WriteByte(group); return this; }
    public IppResponse Text(byte tag, string name, string value) => Value(tag, name, Encoding.UTF8.GetBytes(value));
    public IppResponse Integer(byte tag, string name, int value) { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); return Value(tag, name, bytes); }
    public IppResponse Boolean(string name, bool value) => Value(0x22, name, [value ? (byte)1 : (byte)0]);
    public IppResponse Value(byte tag, string name, byte[] bytes)
    {
        byte[] label = Encoding.UTF8.GetBytes(name);
        stream.WriteByte(tag); Write16(checked((ushort)label.Length)); stream.Write(label); Write16(checked((ushort)bytes.Length)); stream.Write(bytes); return this;
    }
    public byte[] Finish() { stream.WriteByte(3); return stream.ToArray(); }
    private void Write16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); stream.Write(b); }
    private void Write32(int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, value); stream.Write(b); }
    public void Dispose() => stream.Dispose();
}
