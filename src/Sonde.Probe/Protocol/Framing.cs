using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sonde.Probe.Protocol;

/// <summary>
/// The wire framing: a 4-byte little-endian payload length, then the UTF-8 JSON body. One request
/// and one response per connection. Works on any <see cref="Stream"/>, so the same code serves a
/// socket, a pipe, or a <see cref="MemoryStream"/> in a test. The spec is <c>docs/protocol.md</c>.
/// </summary>
public static class Framing
{
    /// <summary>Hard cap on a single frame. Compiled scripts are a few hundred KB at most.</summary>
    public const int MaxFrameBytes = 128 * 1024 * 1024;

    /// <summary>The JSON shape on the wire: camelCase names, enums as camelCase strings, nulls omitted.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes one frame: the length header, then the payload, then a flush.</summary>
    /// <exception cref="InvalidOperationException">The payload exceeds <see cref="MaxFrameBytes"/>.</exception>
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > MaxFrameBytes)
            throw new InvalidOperationException($"Frame of {payload.Length} bytes exceeds the {MaxFrameBytes}-byte cap.");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame. Returns null on a clean end of stream before any header byte.</summary>
    /// <exception cref="InvalidDataException">The header names a length outside 0..<see cref="MaxFrameBytes"/>.</exception>
    /// <exception cref="EndOfStreamException">The stream ended inside the header or the body.</exception>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = await ReadExactAsync(stream, 4, allowCleanEof: true, cancellationToken).ConfigureAwait(false);
        if (header is null)
            return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxFrameBytes)
            throw new InvalidDataException($"Frame length {length} is out of range (0..{MaxFrameBytes}).");
        if (length == 0)
            return [];

        return await ReadExactAsync(stream, length, allowCleanEof: false, cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Stream ended mid-frame.");
    }

    /// <summary>Serialises <paramref name="message"/> with <see cref="Json"/> and writes it as one frame.</summary>
    public static async Task WriteMessageAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await WriteFrameAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame and deserialises it as <typeparamref name="T"/>. Default on a clean end of stream.</summary>
    public static async Task<T?> ReadMessageAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        var frame = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (frame is null)
            return default;
        return JsonSerializer.Deserialize<T>(frame, Json);
    }

    private static async Task<byte[]?> ReadExactAsync(Stream stream, int count, bool allowCleanEof, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0 && allowCleanEof)
                    return null;
                throw new EndOfStreamException("Stream ended before the expected byte count.");
            }
            offset += read;
        }
        return buffer;
    }
}
