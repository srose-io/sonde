using System.Buffers.Binary;
using System.Text;
using Sonde.Probe.Protocol;

namespace Sonde.Probe.Tests;

public class FramingTests
{
    [Fact]
    public async Task Framing_IsFourByteLittleEndianLengthThenUtf8Json()
    {
        // The exact bytes docs/protocol.md shows for a Hello request.
        using var ms = new MemoryStream();
        await Framing.WriteMessageAsync(ms, new ProbeRequest { Kind = ProbeRequestKind.Hello });
        var bytes = ms.ToArray();

        var body = """{"kind":"hello","entryMethodName":"Run","timeoutMs":10000,"maxDepth":6}""";
        var expected = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(expected, body.Length);
        Encoding.UTF8.GetBytes(body, 0, body.Length, expected, 4);
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public async Task RoundTripsAFrame()
    {
        var payload = "hello frame"u8.ToArray();
        using var ms = new MemoryStream();
        await Framing.WriteFrameAsync(ms, payload);
        ms.Position = 0;
        Assert.Equal(payload, await Framing.ReadFrameAsync(ms));
    }

    [Fact]
    public async Task ReadReturnsNullOnCleanEof()
    {
        using var ms = new MemoryStream();
        Assert.Null(await Framing.ReadFrameAsync(ms));
    }

    [Fact]
    public async Task ReadThrowsOnTruncatedBody()
    {
        using var ms = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 100);
        ms.Write(header);
        ms.Write(new byte[10]);
        ms.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadFrameAsync(ms));
    }

    [Fact]
    public async Task ReadRejectsOversizeLength()
    {
        using var ms = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, Framing.MaxFrameBytes + 1);
        ms.Write(header);
        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadFrameAsync(ms));
    }

    [Fact]
    public async Task RoundTripsTypedMessage()
    {
        var request = new ProbeRequest { Kind = ProbeRequestKind.Run, EntryTypeName = "Script", TimeoutMs = 5000 };
        using var ms = new MemoryStream();
        await Framing.WriteMessageAsync(ms, request);
        ms.Position = 0;
        var read = await Framing.ReadMessageAsync<ProbeRequest>(ms);
        Assert.NotNull(read);
        Assert.Equal(ProbeRequestKind.Run, read.Kind);
        Assert.Equal("Script", read.EntryTypeName);
        Assert.Equal(5000, read.TimeoutMs);
    }

    [Fact]
    public async Task ListenerAnswersOneRequestPerConnection()
    {
        // A duplex in-memory pair: the listener reads the request and writes the response.
        using var request = new MemoryStream();
        await Framing.WriteMessageAsync(request, new ProbeRequest { Kind = ProbeRequestKind.Status });
        request.Position = 0;
        using var wire = new DuplexStream(request);

        await Listener.HandleConnectionAsync(wire, CancellationToken.None);

        wire.Written.Position = 0;
        var status = await Framing.ReadMessageAsync<StatusResponse>(wire.Written);
        Assert.NotNull(status);
        Assert.Equal(Environment.ProcessId, status.ProcessId);
    }

    /// <summary>Reads from one stream, writes to another, so a one-shot exchange can be driven in memory.</summary>
    private sealed class DuplexStream(Stream input) : Stream
    {
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            // Written stays readable after the listener closes the connection.
            base.Dispose(disposing);
        }
    }
}
