using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotNext.Buffers.Text;

using Buffers;
using StreamConsumer = IO.StreamConsumer;

public partial struct Base64Decoder
{
    private const byte PaddingByte = (byte)PaddingChar;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> AsReadOnlyBytes(in ulong value, int length)
    {
        Debug.Assert((uint)length <= (uint)sizeof(ulong));

        return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<ulong, byte>(ref Unsafe.AsRef(in value)), length);
    }

    [SuppressMessage("StyleCop.CSharp.SpacingRules", "SA1010", Justification = "False positive")]
    private bool DecodeFromUtf8Core<TWriter>(scoped ReadOnlySpan<byte> utf8Chars, scoped ref TWriter writer)
        where TWriter : notnull, IBufferWriter<byte>
    {
        Debug.Assert(reservedBufferSize >= 0);

        var produced = Base64.GetMaxDecodedFromUtf8Length(utf8Chars.Length);
        var buffer = writer.GetSpan(produced);

        // x & 3 is the same as x % 4
        switch (Base64.DecodeFromUtf8(utf8Chars, buffer, out var consumed, out produced, (utf8Chars.Length & 3) is 0))
        {
            default:
                return false;
            case OperationStatus.Done:
                reservedBufferSize = utf8Chars is [.., PaddingByte] ? GotPaddingFlag : 0;
                break;
            case OperationStatus.DestinationTooSmall:
                reservedBufferSize = 0;
                break;
            case OperationStatus.NeedMoreData:
                reservedBufferSize = utf8Chars.Length - consumed;
                Debug.Assert(reservedBufferSize <= sizeof(ulong));
                utf8Chars.Slice(consumed).CopyTo(Span.AsBytes(ref reservedBuffer));
                break;
        }

        writer.Advance(produced);
        return true;
    }

    [SkipLocalsInit]
    private bool CopyAndDecodeFromUtf8<TWriter>(scoped ReadOnlySpan<byte> utf8Chars, scoped ref TWriter writer)
        where TWriter : notnull, IBufferWriter<byte>
    {
        Debug.Assert(reservedBufferSize > 0);

        var newSize = reservedBufferSize + utf8Chars.Length;
        using var tempBuffer = (uint)newSize <= (uint)MemoryRental<byte>.StackallocThreshold ? stackalloc byte[newSize] : new MemoryRental<byte>(newSize);
        AsReadOnlyBytes(in reservedBuffer, reservedBufferSize).CopyTo(tempBuffer.Span);
        utf8Chars.CopyTo(tempBuffer.Span.Slice(reservedBufferSize));
        return DecodeFromUtf8Core(tempBuffer.Span, ref writer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool DecodeFromUtf8<TWriter>(scoped ReadOnlySpan<byte> utf8Chars, scoped ref TWriter writer)
        where TWriter : notnull, IBufferWriter<byte>
    {
        return reservedBufferSize switch
        {
            GotPaddingFlag => false,
            0 => DecodeFromUtf8Core(utf8Chars, ref writer),
            _ => CopyAndDecodeFromUtf8(utf8Chars, ref writer),
        };
    }

    /// <summary>
    /// Decodes UTF-8 encoded base64 string.
    /// </summary>
    /// <param name="utf8Chars">UTF-8 encoded portion of base64 string.</param>
    /// <param name="output">The output growable buffer used to write decoded bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The input base64 string is malformed.</exception>
    public void DecodeFromUtf8(scoped ReadOnlySpan<byte> utf8Chars, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!DecodeFromUtf8(utf8Chars, ref output))
            throw new FormatException(ExceptionMessages.MalformedBase64);
    }

    /// <summary>
    /// Decodes UTF-8 encoded base64 string.
    /// </summary>
    /// <param name="utf8Chars">UTF-8 encoded portion of base64 string.</param>
    /// <param name="output">The output growable buffer used to write decoded bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The input base64 string is malformed.</exception>
    public void DecodeFromUtf8(scoped in ReadOnlySequence<byte> utf8Chars, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        foreach (var chunk in utf8Chars)
        {
            if (!DecodeFromUtf8(chunk.Span, ref output))
                throw new FormatException(ExceptionMessages.MalformedBase64);
        }
    }

    /// <summary>
    /// Decoes UTF-8 encoded base64 string.
    /// </summary>
    /// <param name="utf8Chars">UTF-8 encoded portion of base64 string.</param>
    /// <param name="allocator">The allocator of the result buffer.</param>
    /// <returns>A buffer containing decoded bytes.</returns>
    public MemoryOwner<byte> DecodeFromUtf8(scoped ReadOnlySpan<byte> utf8Chars, MemoryAllocator<byte>? allocator = null)
    {
        var result = new MemoryOwnerWrapper<byte>(allocator);

        if (utf8Chars.IsEmpty || DecodeFromUtf8(utf8Chars, ref result))
            return result.Buffer;

        result.Buffer.Dispose();
        throw new FormatException(ExceptionMessages.MalformedBase64);
    }

    [SuppressMessage("StyleCop.CSharp.SpacingRules", "SA1010", Justification = "False positive")]
    [SkipLocalsInit]
    private void DecodeFromUtf8Core<TConsumer>(scoped ReadOnlySpan<byte> utf8Chars, TConsumer output)
        where TConsumer : notnull, IReadOnlySpanConsumer<byte>
    {
        Debug.Assert(reservedBufferSize >= 0);

        Span<byte> buffer = stackalloc byte[DecodingBufferSize];

        do
        {
            // x & 3 is the same as x % 4
            switch (Base64.DecodeFromUtf8(utf8Chars, buffer, out var consumed, out var produced, (utf8Chars.Length & 3) is 0))
            {
                default:
                    throw new FormatException(ExceptionMessages.MalformedBase64);
                case OperationStatus.Done:
                    reservedBufferSize = utf8Chars is [.., PaddingByte] ? GotPaddingFlag : 0;
                    break;
                case OperationStatus.DestinationTooSmall:
                    reservedBufferSize = 0;
                    break;
                case OperationStatus.NeedMoreData:
                    reservedBufferSize = utf8Chars.Length - consumed;
                    Debug.Assert(reservedBufferSize <= sizeof(ulong) / sizeof(char));
                    utf8Chars.Slice(consumed).CopyTo(Span.AsBytes(ref reservedBuffer));
                    break;
            }

            if (produced is 0 || consumed is 0)
                break;

            output.Invoke(buffer.Slice(0, produced));
            utf8Chars = utf8Chars.Slice(consumed);
        }
        while (reservedBufferSize is not GotPaddingFlag);
    }

    [SkipLocalsInit]
    private void CopyAndDecodeFromUtf8<TConsumer>(scoped ReadOnlySpan<byte> utf8Chars, TConsumer output)
        where TConsumer : notnull, IReadOnlySpanConsumer<byte>
    {
        Debug.Assert(reservedBufferSize > 0);

        var newSize = reservedBufferSize + utf8Chars.Length;
        using var tempBuffer = (uint)newSize <= (uint)MemoryRental<byte>.StackallocThreshold ? stackalloc byte[newSize] : new MemoryRental<byte>(newSize);
        AsReadOnlyBytes(in reservedBuffer, reservedBufferSize).CopyTo(tempBuffer.Span);
        utf8Chars.CopyTo(tempBuffer.Span.Slice(reservedBufferSize));
        DecodeFromUtf8Core(tempBuffer.Span, output);
    }

    /// <summary>
    /// Decodes base64-encoded bytes.
    /// </summary>
    /// <typeparam name="TConsumer">The type of the consumer.</typeparam>
    /// <param name="utf8Chars">The span containing base64-encoded bytes.</param>
    /// <param name="output">The consumer called for decoded portion of data.</param>
    /// <exception cref="FormatException">The input base64 string is malformed.</exception>
    public void DecodeFromUtf8<TConsumer>(scoped ReadOnlySpan<byte> utf8Chars, TConsumer output)
        where TConsumer : notnull, IReadOnlySpanConsumer<byte>
    {
        switch (reservedBufferSize)
        {
            case GotPaddingFlag:
                throw new FormatException(ExceptionMessages.MalformedBase64);
            case 0:
                DecodeFromUtf8Core(utf8Chars, output);
                break;
            default:
                CopyAndDecodeFromUtf8(utf8Chars, output);
                break;
        }
    }

    /// <summary>
    /// Decodes UTF-8 encoded base64 string.
    /// </summary>
    /// <typeparam name="TArg">The type of the argument to be passed to the callback.</typeparam>
    /// <param name="utf8Chars">UTF-8 encoded portion of base64 string.</param>
    /// <param name="output">The callback called for decoded portion of data.</param>
    /// <param name="arg">The argument to be passed to the callback.</param>
    /// <exception cref="FormatException">The input base64 string is malformed.</exception>
    public void DecodeFromUtf8<TArg>(scoped ReadOnlySpan<byte> utf8Chars, ReadOnlySpanAction<byte, TArg> output, TArg arg)
        => DecodeFromUtf8(utf8Chars, new DelegatingReadOnlySpanConsumer<byte, TArg>(output, arg));

    /// <summary>
    /// Decodes UTF-8 encoded base64 string.
    /// </summary>
    /// <typeparam name="TArg">The type of the argument to be passed to the callback.</typeparam>
    /// <param name="utf8Chars">UTF-8 encoded portion of base64 string.</param>
    /// <param name="output">The callback called for decoded portion of data.</param>
    /// <param name="arg">The argument to be passed to the callback.</param>
    /// <exception cref="FormatException">The input base64 string is malformed.</exception>
    [CLSCompliant(false)]
    public unsafe void DecodeFromUtf8<TArg>(scoped ReadOnlySpan<byte> utf8Chars, delegate*<ReadOnlySpan<byte>, TArg, void> output, TArg arg)
        => DecodeFromUtf8(utf8Chars, new ReadOnlySpanConsumer<byte, TArg>(output, arg));

    /// <summary>
    /// Decodes UTF-8 encoded base64 string and writes result to the stream synchronously.
    /// </summary>
    /// <param name="utf8Chars">UTF-8 encoded portion of base64 string.</param>
    /// <param name="output">The stream used as destination for decoded bytes.</param>
    /// <exception cref="FormatException">The input base64 string is malformed.</exception>
    public void DecodeFromUtf8(scoped ReadOnlySpan<byte> utf8Chars, Stream output)
        => DecodeFromUtf8<StreamConsumer>(utf8Chars, output);

    /// <summary>
    /// Decodes a sequence of base64-encoded bytes.
    /// </summary>
    /// <param name="utf8Chars">A sequence of base64-encoded bytes.</param>
    /// <param name="allocator">The allocator of the buffer used for decoded bytes.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>A sequence of decoded bytes.</returns>
    /// <exception cref="FormatException">The input base64 string is malformed.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> DecodeFromUtf8Async(IAsyncEnumerable<ReadOnlyMemory<byte>> utf8Chars, MemoryAllocator<byte>? allocator = null, [EnumeratorCancellation] CancellationToken token = default)
    {
        var decoder = new Base64Decoder();
        MemoryOwner<byte> buffer;

        await foreach (var chunk in utf8Chars.WithCancellation(token).ConfigureAwait(false))
        {
            using (buffer = decoder.DecodeFromUtf8(chunk.Span, allocator))
                yield return buffer.Memory;
        }

        if (decoder.NeedMoreData)
            throw new FormatException(ExceptionMessages.MalformedBase64);
    }
}