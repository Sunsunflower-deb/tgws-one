using System.Buffers.Binary;
using System.Text;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine.Tests.Desync;

/// <summary>Builds synthetic ClientHello records so the parser and planner tests do not depend on a TLS stack.</summary>
internal static class DesyncTestData
{
    public const string Host = "www.example.com";

    public static byte[] ClientHello(string? host = Host, bool tls13 = false, bool sessionId = false,
                                     bool grease = false, int padding = 0, byte? recordType = null,
                                     byte? handshakeType = null, int? recordLength = null)
    {
        var body = new List<byte> { 0x03, 0x03 };
        body.AddRange(Enumerable.Range(1, 32).Select(value => (byte)value));
        if (sessionId)
        {
            body.Add(32);
            body.AddRange(Enumerable.Repeat((byte)0x5a, 32));
        }
        else
        {
            body.Add(0);
        }

        var suites = new List<byte>();
        if (grease) suites.AddRange([0x0a, 0x0a]);
        suites.AddRange([0x13, 0x01, 0x13, 0x02, 0x13, 0x03, 0x00, 0x2f]);
        AddVector16(body, suites);

        body.Add(1);
        body.Add(0);

        var extensions = new List<byte>();
        if (host is not null) AddServerName(extensions, host);
        if (tls13)
        {
            AddExtension(extensions, 0x002b, [0x02, 0x03, 0x04]);
            AddExtension(extensions, 0x000a, [0x00, 0x02, 0x00, 0x1d]);
        }
        if (grease) AddExtension(extensions, 0x1a1a, []);
        if (padding > 0) AddExtension(extensions, 0x0015, new byte[padding]);
        AddVector16(body, extensions);

        var handshake = new List<byte> { handshakeType ?? 0x01 };
        handshake.Add((byte)(body.Count >> 16));
        handshake.Add((byte)(body.Count >> 8));
        handshake.Add((byte)body.Count);
        handshake.AddRange(body);

        int length = recordLength ?? handshake.Count;
        var record = new List<byte>
        {
            recordType ?? 0x16, 0x03, 0x01, (byte)(length >> 8), (byte)length
        };
        record.AddRange(handshake);
        return [.. record];
    }

    public static ClientHelloLayout Parse(byte[] hello)
    {
        Assert.True(ClientHelloLayout.TryParse(hello, out ClientHelloLayout layout));
        return layout;
    }

    /// <summary>Splits one record into two records at <paramref name="payloadCut"/>, the shape a TlsRec step produces.</summary>
    public static byte[] SplitRecord(byte[] hello, int payloadCut)
    {
        int recordLength = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(3, 2));
        Assert.InRange(payloadCut, 1, recordLength - 1);
        var result = new byte[hello.Length + 5];
        hello.AsSpan(0, 5 + payloadCut).CopyTo(result);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(3, 2), (ushort)payloadCut);
        int second = recordLength - payloadCut;
        int header = 5 + payloadCut;
        result[header] = 0x16;
        result[header + 1] = 0x03;
        result[header + 2] = hello[2];
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(header + 3, 2), (ushort)second);
        hello.AsSpan(5 + payloadCut).CopyTo(result.AsSpan(header + 5));
        return result;
    }

    private static void AddServerName(List<byte> extensions, string host)
    {
        byte[] name = Encoding.ASCII.GetBytes(host);
        int listLength = name.Length + 3;
        var data = new List<byte>
        {
            (byte)(listLength >> 8), (byte)listLength, 0,
            (byte)(name.Length >> 8), (byte)name.Length
        };
        data.AddRange(name);
        AddExtension(extensions, 0x0000, [.. data]);
    }

    private static void AddExtension(List<byte> extensions, int type, byte[] data)
    {
        extensions.Add((byte)(type >> 8));
        extensions.Add((byte)type);
        extensions.Add((byte)(data.Length >> 8));
        extensions.Add((byte)data.Length);
        extensions.AddRange(data);
    }

    private static void AddVector16(List<byte> body, List<byte> value)
    {
        body.Add((byte)(value.Count >> 8));
        body.Add((byte)value.Count);
        body.AddRange(value);
    }
}
