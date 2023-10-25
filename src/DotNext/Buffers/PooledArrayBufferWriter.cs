using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotNext.Buffers;

using static Runtime.Intrinsics;

/// <summary>
/// Represents memory writer that is backed by the array obtained from the pool.
/// </summary>
/// <remarks>
/// This class provides additional methods for access to array segments in contrast to <see cref="PooledBufferWriter{T}"/>.
/// </remarks>
/// <typeparam name="T">The data type that can be written.</typeparam>
public sealed class PooledArrayBufferWriter<T> : BufferWriter<T>, ISupplier<ArraySegment<T>>, IList<T>
{
    private readonly ArrayPool<T> pool;
    private T[] buffer;

    /// <summary>
    /// Initializes a new writer with the specified initial capacity.
    /// </summary>
    /// <param name="pool">The array pool.</param>
    /// <param name="initialCapacity">The initial capacity of the writer.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is less than or equal to zero.</exception>
    [Obsolete("Use init-only properties to set the capacity and allocator")]
    public PooledArrayBufferWriter(ArrayPool<T> pool, int initialCapacity)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        this.pool = pool;
        buffer = pool.Rent(initialCapacity);
    }

    /// <summary>
    /// Initializes a new writer with the default initial capacity.
    /// </summary>
    /// <param name="pool">The array pool.</param>
    [Obsolete("Use init-only properties to set the capacity and pool")]
    public PooledArrayBufferWriter(ArrayPool<T> pool)
    {
        this.pool = pool;
        buffer = Array.Empty<T>();
    }

    /// <summary>
    /// Initializes a new writer with the specified initial capacity and <see cref="ArrayPool{T}.Shared"/>
    /// as the array pooling mechanism.
    /// </summary>
    /// <param name="initialCapacity">The initial capacity of the writer.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is less than or equal to zero.</exception>
    [Obsolete("Use init-only properties to set the capacity and pool")]
    public PooledArrayBufferWriter(int initialCapacity)
        : this(ArrayPool<T>.Shared, initialCapacity)
    {
    }

    /// <summary>
    /// Initializes a new writer with the default initial capacity and <see cref="ArrayPool{T}.Shared"/>
    /// as the array pooling mechanism.
    /// </summary>
    /// <seealso cref="BufferPool"/>
    /// <seealso cref="Capacity"/>
    public PooledArrayBufferWriter()
    {
        pool = ArrayPool<T>.Shared;
        buffer = Array.Empty<T>();
    }

    /// <summary>
    /// Sets the array pool that will be used to rent the internal buffer.
    /// </summary>
    /// <remarks>
    /// It is recommended to initialize this property before <see cref="Capacity"/>.
    /// <see langword="null"/> value is the same as <see cref="ArrayPool{T}.Shared"/>.
    /// </remarks>
    public ArrayPool<T>? BufferPool
    {
        init
        {
            value ??= ArrayPool<T>.Shared;

            var length = buffer.Length;

            // cover situation when Capacity initializer called before this initializer
            if (length > 0)
            {
                pool.Return(buffer); // no need to clear fresh array
                buffer = value.Rent(length);
            }

            pool = value;
        }
    }

    /// <inheritdoc/>
    int ICollection<T>.Count => WrittenCount;

    /// <inheritdoc/>
    bool ICollection<T>.IsReadOnly => false;

    /// <inheritdoc/>
    void ICollection<T>.CopyTo(T[] array, int arrayIndex)
    {
        var source = MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetArrayDataReference(buffer), position);
        source.CopyTo(array.AsSpan(arrayIndex));
    }

    /// <summary>
    /// Gets the element at the specified index.
    /// </summary>
    /// <param name="index">The index of the element to retrieve.</param>
    /// <value>The element at the specified index.</value>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> the index is invalid.</exception>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public new ref T this[int index] => ref this[(long)index];

    /// <summary>
    /// Gets the element at the specified index.
    /// </summary>
    /// <param name="index">The index of the element to retrieve.</param>
    /// <value>The element at the specified index.</value>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> the index is invalid.</exception>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public ref T this[long index]
    {
        get
        {
            ThrowIfDisposed();
            if ((ulong)index >= (ulong)position)
                throw new ArgumentOutOfRangeException(nameof(index));

            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), (nint)index);
        }
    }

    /// <inheritdoc/>
    int IList<T>.IndexOf(T item)
    {
        ThrowIfDisposed();
        return Array.IndexOf(buffer, item, 0, position);
    }

    /// <inheritdoc/>
    bool ICollection<T>.Contains(T item)
    {
        ThrowIfDisposed();
        return Array.IndexOf(buffer, item, 0, position) >= 0;
    }

    private void RemoveAt(int index)
    {
        Debug.Assert(buffer.Length > 0);
        CopyFast(buffer, index + 1, buffer, index, position - index - 1);

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            buffer[position - 1] = default!;

        if (--position is 0)
        {
            ReturnBuffer();
            buffer = Array.Empty<T>();
        }
    }

    /// <inheritdoc/>
    void IList<T>.RemoveAt(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)position)
            throw new ArgumentOutOfRangeException(nameof(index));

        RemoveAt(index);
    }

    /// <inheritdoc/>
    bool ICollection<T>.Remove(T item)
    {
        ThrowIfDisposed();
        var index = Array.IndexOf(buffer, item, 0, position);
        if (index < 0)
            return false;

        RemoveAt(index);
        return true;
    }

    /// <inheritdoc/>
    void IList<T>.Insert(int index, T item)
        => Insert(index, MemoryMarshal.CreateReadOnlySpan(ref item, 1));

    /// <summary>
    /// Inserts the elements into this buffer at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index at which the new elements should be inserted.</param>
    /// <param name="items">The span whose elements should be inserted into this buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> the index is invalid.</exception>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public void Insert(int index, ReadOnlySpan<T> items)
    {
        ThrowIfDisposed();
        if ((uint)index > (uint)position)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (items.IsEmpty)
            goto exit;

        if (GetLength(buffer) is 0)
        {
            buffer = pool.Rent(items.Length);
        }
        else if (position + items.Length <= GetLength(buffer))
        {
            CopyFast(buffer, index, buffer, index + items.Length, position - index);
        }
        else
        {
            Debug.Assert(buffer.Length > 0);

            var newBuffer = pool.Rent(buffer.Length + items.Length);
            CopyFast(buffer, newBuffer, index);
            CopyFast(buffer, index, newBuffer, index + items.Length, buffer.Length - index);
            ReturnBuffer();
            buffer = newBuffer;
        }

        items.CopyTo(buffer.AsSpan(index));
        position += items.Length;

    exit:
        return;
    }

    /// <summary>
    /// Overwrites the elements in this buffer.
    /// </summary>
    /// <param name="index">The zero-based index at which the new elements should be rewritten.</param>
    /// <param name="items">The span whose elements should be added into this buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> the index is invalid.</exception>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public void Overwrite(int index, ReadOnlySpan<T> items)
    {
        ThrowIfDisposed();
        if ((uint)index > (uint)position)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (GetLength(buffer) is 0)
        {
            buffer = pool.Rent(items.Length);
        }
        else if (index + items.Length <= GetLength(buffer))
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Array.Clear(buffer, index, position - index);
        }
        else
        {
            Debug.Assert(buffer.Length > 0);

            var newBuffer = pool.Rent(index + items.Length);
            CopyFast(buffer, newBuffer, index);
            ReturnBuffer();
            buffer = newBuffer;
        }

        items.CopyTo(buffer.AsSpan(index));
        position = index + items.Length;
    }

    /// <inheritdoc/>
    T IList<T>.this[int index]
    {
        get => this[index];
        set => this[index] = value;
    }

    /// <inheritdoc/>
    void ICollection<T>.Clear() => Clear(false);

    /// <inheritdoc />
    public override int Capacity
    {
        get => buffer.Length;

        init
        {
            switch (value)
            {
                case < 0:
                    throw new ArgumentOutOfRangeException(nameof(value));
                case > 0:
                    buffer = pool.Rent(value);
                    break;
            }
        }
    }

    /// <summary>
    /// Gets the data written to the underlying buffer so far.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public override ReadOnlyMemory<T> WrittenMemory
    {
        get
        {
            ThrowIfDisposed();
            return new(buffer, 0, position);
        }
    }

    /// <summary>
    /// Gets the data written to the underlying array so far.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public ArraySegment<T> WrittenArray
    {
        get
        {
            ThrowIfDisposed();
            return new(buffer, 0, position);
        }
    }

    /// <inheritdoc/>
    ArraySegment<T> ISupplier<ArraySegment<T>>.Invoke() => WrittenArray;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReturnBuffer() => pool.Return(buffer, RuntimeHelpers.IsReferenceOrContainsReferences<T>());

    /// <summary>
    /// Clears the data written to the underlying memory.
    /// </summary>
    /// <param name="reuseBuffer"><see langword="true"/> to reuse the internal buffer; <see langword="false"/> to destroy the internal buffer.</param>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public override void Clear(bool reuseBuffer = false)
    {
        ThrowIfDisposed();

        if (GetLength(buffer) is 0)
        {
            // nothing to do
        }
        else if (!reuseBuffer)
        {
            ReturnBuffer();
            buffer = Array.Empty<T>();
        }
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(buffer, 0, position);
        }

        position = 0;
    }

    /// <inheritdoc />
    public override MemoryOwner<T> DetachBuffer()
    {
        ThrowIfDisposed();
        MemoryOwner<T> result;
        if (position > 0)
        {
            result = new(pool, buffer, position);
            buffer = Array.Empty<T>();
            position = 0;
        }
        else
        {
            result = default;
        }

        return result;
    }

    private T[] GetRawArray(int sizeHint)
    {
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint));

        ThrowIfDisposed();
        CheckAndResizeBuffer(sizeHint);
        return buffer;
    }

    /// <summary>
    /// Returns the memory to write to that is at least the requested size.
    /// </summary>
    /// <param name="sizeHint">The minimum length of the returned memory.</param>
    /// <returns>The memory block of at least the size <paramref name="sizeHint"/>.</returns>
    /// <exception cref="OutOfMemoryException">The requested buffer size is not available.</exception>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public override Memory<T> GetMemory(int sizeHint = 0)
        => GetRawArray(sizeHint).AsMemory(position);

    /// <summary>
    /// Returns the memory to write to that is at least the requested size.
    /// </summary>
    /// <param name="sizeHint">The minimum length of the returned memory.</param>
    /// <returns>The memory block of at least the size <paramref name="sizeHint"/>.</returns>
    /// <exception cref="OutOfMemoryException">The requested buffer size is not available.</exception>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public override Span<T> GetSpan(int sizeHint = 0)
    {
        var array = GetRawArray(sizeHint);
        Debug.Assert(position <= array.Length);

        // Perf: skip redundant checks implemented internally by MemoryExtensions.AsSpan() extension method
        return MemoryMarshal.CreateSpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), position), array.Length - position);
    }

    /// <summary>
    /// Returns the memory to write to that is at least the requested size.
    /// </summary>
    /// <param name="sizeHint">The minimum length of the returned memory.</param>
    /// <returns>The memory block of at least the size <paramref name="sizeHint"/>.</returns>
    /// <exception cref="OutOfMemoryException">The requested buffer size is not available.</exception>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public ArraySegment<T> GetArray(int sizeHint = 0)
        => new(GetRawArray(sizeHint), position, FreeCapacity);

    /// <inheritdoc/>
    public override void AddAll(ICollection<T> items)
    {
        ThrowIfDisposed();

        var count = items.Count;
        if (count <= 0)
            return;

        CheckAndResizeBuffer(count);
        items.CopyTo(buffer, position);
        position += count;
    }

    /// <summary>
    /// Removes the specified number of elements from the tail of this buffer.
    /// </summary>
    /// <param name="count">The number of elements to be removed from the tail of this buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than 0.</exception>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public void RemoveLast(int count)
    {
        ThrowIfDisposed();
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (GetLength(buffer) is 0)
        {
            // nothing to do
        }
        else if (count >= position)
        {
            ReturnBuffer();
            buffer = Array.Empty<T>();
            position = 0;
        }
        else if (count > 0)
        {
            var newSize = position - count;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                Array.Clear(buffer, newSize, position - newSize);
            }

            position = newSize;
        }
    }

    /// <summary>
    /// Removes the specified number of elements from the head of this buffer.
    /// </summary>
    /// <param name="count">The number of elements to be removed from the head of this buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than 0.</exception>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    public void RemoveFirst(int count)
    {
        ThrowIfDisposed();
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (GetLength(buffer) is 0)
        {
            // nothing to do
        }
        else if (count >= position)
        {
            ReturnBuffer();
            buffer = Array.Empty<T>();
            position = 0;
        }
        else if (count > 0)
        {
            Debug.Assert(buffer.Length > 0);

            var newSize = position - count;
            var newBuffer = pool.Rent(newSize);
            Array.Copy(buffer, count, newBuffer, 0, newSize);
            ReturnBuffer();
            buffer = newBuffer;
            position = newSize;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyFast(T[] source, T[] destination, int length)
    {
        Debug.Assert(length <= source.Length);
        Debug.Assert(length <= destination.Length);

        var src = MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetArrayDataReference(source), length);
        var dest = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetArrayDataReference(destination), length);
        src.CopyTo(dest);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyFast(T[] source, int sourceIndex, T[] destination, int destinationIndex, int length)
    {
        Debug.Assert(sourceIndex < source.Length);
        Debug.Assert(length <= source.Length - sourceIndex);
        Debug.Assert(destinationIndex < destination.Length);
        Debug.Assert(length <= destination.Length - destinationIndex);

        var src = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(source), sourceIndex), length);
        var dest = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(destination), destinationIndex), length);
        src.CopyTo(dest);
    }

    /// <inheritdoc/>
    private protected override void Resize(int newSize)
    {
        var newBuffer = pool.Rent(newSize);
        if (GetLength(buffer) > 0)
        {
            CopyFast(buffer, newBuffer, position);
            ReturnBuffer();
        }

        buffer = newBuffer;
#pragma warning disable CS0618
        AllocationCounter?.WriteMetric(newBuffer.LongLength);
#pragma warning restore CS0618
        PooledArrayBufferWriter.AllocationMeter.Record(newBuffer.LongLength, measurementTags);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
#pragma warning disable CS0618
            BufferSizeCallback?.Invoke(buffer.Length);
#pragma warning restore CS0618
            if (GetLength(buffer) > 0)
            {
                ReturnBuffer();
                buffer = Array.Empty<T>();
            }
        }

        base.Dispose(disposing);
    }
}

// TODO: Convert to file-local class in C# 11
internal static class PooledArrayBufferWriter
{
    internal static readonly Histogram<long> AllocationMeter;

    static PooledArrayBufferWriter()
    {
        var meter = new Meter("DotNext.Buffers.PooledArrayBuffer");
        AllocationMeter = meter.CreateHistogram<long>("capacity", "Capacity");
    }
}