// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CommunityToolkit.HighPerformance.Buffers.Views;
#if NET6_0_OR_GREATER
using BitOperations = System.Numerics.BitOperations;
#else
using BitOperations = CommunityToolkit.HighPerformance.Helpers.Internals.BitOperations;
#endif

namespace CommunityToolkit.HighPerformance.Buffers;

/// <summary>
/// Represents a heap-based, array-backed output sink into which <typeparamref name="T"/> data can be written.
/// </summary>
/// <typeparam name="T">The type of items to write to the current instance.</typeparam>
/// <remarks>
/// This is a custom <see cref="IBufferWriter{T}"/> implementation that replicates the
/// functionality and API surface of the array-based buffer writer available in
/// .NET Standard 2.1, with the main difference being the fact that in this case
/// the arrays in use are rented from the shared <see cref="ArrayPool{T}"/> instance,
/// and that <see cref="ArrayPoolBufferWriter{T}"/> is also available on .NET Standard 2.0.
/// </remarks>
[DebuggerTypeProxy(typeof(MemoryDebugView<>))]
[DebuggerDisplay("{ToString(),raw}")]
public sealed class ArrayPoolBufferWriter<T> : IBuffer<T>, IMemoryOwner<T>
{
    /// <summary>
    /// The default buffer size to use to expand empty arrays.
    /// </summary>
    private const int DefaultInitialBufferSize = 256;

    /// <summary>
    /// The <see cref="ArrayPool{T}"/> instance used to rent <see cref="array"/>.
    /// </summary>
    private readonly ArrayPool<T> pool;

    /// <summary>
    /// The underlying <typeparamref name="T"/> array.
    /// </summary>
    private T[]? array;

#pragma warning disable IDE0032 // Use field over auto-property (clearer and faster)
    /// <summary>
    /// The starting offset within <see cref="array"/>.
    /// </summary>
    private int index;
#pragma warning restore IDE0032

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrayPoolBufferWriter{T}"/> class.
    /// </summary>
    public ArrayPoolBufferWriter()
        : this(ArrayPool<T>.Shared, DefaultInitialBufferSize)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrayPoolBufferWriter{T}"/> class.
    /// </summary>
    /// <param name="pool">The <see cref="ArrayPool{T}"/> instance to use.</param>
    public ArrayPoolBufferWriter(ArrayPool<T> pool)
        : this(pool, DefaultInitialBufferSize)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrayPoolBufferWriter{T}"/> class.
    /// </summary>
    /// <param name="initialCapacity">The minimum capacity with which to initialize the underlying buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="initialCapacity"/> is not valid.</exception>
    public ArrayPoolBufferWriter(int initialCapacity)
        : this(ArrayPool<T>.Shared, initialCapacity)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrayPoolBufferWriter{T}"/> class.
    /// </summary>
    /// <param name="pool">The <see cref="ArrayPool{T}"/> instance to use.</param>
    /// <param name="initialCapacity">The minimum capacity with which to initialize the underlying buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="initialCapacity"/> is not valid.</exception>
    public ArrayPoolBufferWriter(ArrayPool<T> pool, int initialCapacity)
    {
        // Since we're using pooled arrays, we can rent the buffer with the
        // default size immediately, we don't need to use lazy initialization
        // to save unnecessary memory allocations in this case.
        // Additionally, we don't need to manually throw the exception if
        // the requested size is not valid, as that'll be thrown automatically
        // by the array pool in use when we try to rent an array with that size.
        this.pool = pool;
        this.array = pool.Rent(initialCapacity);
        this.index = 0;
    }

    /// <inheritdoc/>
    Memory<T> IMemoryOwner<T>.Memory
    {
        // This property is explicitly implemented so that it's hidden
        // under normal usage, as the name could be confusing when
        // displayed besides WrittenMemory and GetMemory().
        // The IMemoryOwner<T> interface is implemented primarily
        // so that the AsStream() extension can be used on this type,
        // allowing users to first create a ArrayPoolBufferWriter<byte>
        // instance to write data to, then get a stream through the
        // extension and let it take care of returning the underlying
        // buffer to the shared pool when it's no longer necessary.
        // Inlining is not needed here since this will always be a callvirt.
        get => MemoryMarshal.AsMemory(WrittenMemory);
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<T> WrittenMemory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            T[]? array = this.array;

            if (array is null)
            {
                ThrowObjectDisposedException();
            }

            return array!.AsMemory(0, this.index);
        }
    }

    /// <inheritdoc/>
    public ReadOnlySpan<T> WrittenSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            T[]? array = this.array;

            if (array is null)
            {
                ThrowObjectDisposedException();
            }

            return array!.AsSpan(0, this.index);
        }
    }

    /// <inheritdoc/>
    public int WrittenCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => this.index;
    }

    /// <inheritdoc/>
    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            T[]? array = this.array;

            if (array is null)
            {
                ThrowObjectDisposedException();
            }

            return array!.Length;
        }
    }

    /// <inheritdoc/>
    public int FreeCapacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            T[]? array = this.array;

            if (array is null)
            {
                ThrowObjectDisposedException();
            }

            return array!.Length - this.index;
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        T[]? array = this.array;

        if (array is null)
        {
            ThrowObjectDisposedException();
        }

        array.AsSpan(0, this.index).Clear();

        this.index = 0;
    }

    /// <inheritdoc/>
    public void Advance(int count)
    {
        T[]? array = this.array;

        if (array is null)
        {
            ThrowObjectDisposedException();
        }

        if (count < 0)
        {
            ThrowArgumentOutOfRangeExceptionForNegativeCount();
        }

        if (this.index > array!.Length - count)
        {
            ThrowArgumentExceptionForAdvancedTooFar();
        }

        this.index += count;
    }

    /// <inheritdoc/>
    public Memory<T> GetMemory(int sizeHint = 0)
    {
        CheckBufferAndEnsureCapacity(sizeHint);

        return this.array.AsMemory(this.index);
    }

    /// <inheritdoc/>
    public Span<T> GetSpan(int sizeHint = 0)
    {
        CheckBufferAndEnsureCapacity(sizeHint);

        return this.array.AsSpan(this.index);
    }

    /// <summary>
    /// Ensures that <see cref="array"/> has enough free space to contain a given number of new items.
    /// </summary>
    /// <param name="sizeHint">The minimum number of items to ensure space for in <see cref="array"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckBufferAndEnsureCapacity(int sizeHint)
    {
        T[]? array = this.array;

        if (array is null)
        {
            ThrowObjectDisposedException();
        }

        if (sizeHint < 0)
        {
            ThrowArgumentOutOfRangeExceptionForNegativeSizeHint();
        }

        if (sizeHint == 0)
        {
            sizeHint = 1;
        }

        if (sizeHint > array!.Length - this.index)
        {
            ResizeBuffer(sizeHint);
        }
    }

    /// <summary>
    /// Resizes <see cref="array"/> to ensure it can fit the specified number of new items.
    /// </summary>
    /// <param name="sizeHint">The minimum number of items to ensure space for in <see cref="array"/>.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ResizeBuffer(int sizeHint)
    {
        uint minimumSize = (uint)this.index + (uint)sizeHint;

        // The ArrayPool<T> class has a maximum threshold of 1024 * 1024 for the maximum length of
        // pooled arrays, and once this is exceeded it will just allocate a new array every time
        // of exactly the requested size. In that case, we manually round up the requested size to
        // the nearest power of two, to ensure that repeated consecutive writes when the array in
        // use is bigger than that threshold don't end up causing a resize every single time.
        if (minimumSize > 1024 * 1024)
        {
            minimumSize = BitOperations.RoundUpToPowerOf2(minimumSize);
        }

        this.pool.Resize(ref this.array, (int)minimumSize);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        T[]? array = this.array;

        if (array is null)
        {
            return;
        }

        this.array = null;

        this.pool.Return(array);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        // See comments in MemoryOwner<T> about this
        if (typeof(T) == typeof(char) &&
            this.array is char[] chars)
        {
            return new(chars, 0, this.index);
        }

        // Same representation used in Span<T>
        return $"CommunityToolkit.HighPerformance.Buffers.ArrayPoolBufferWriter<{typeof(T)}>[{this.index}]";
    }

    /// <summary>
    /// Zero-copy output of the underlying memory to the stream, using <c>Stream.Write</c>.
    /// </summary>
    /// <param name="stream">Stream to write the buffer to.</param>
    /// <exception cref="ArgumentNullException">Stream invalid.</exception>
    /// <exception cref="ArgumentException">Used with T other than byte.</exception>
    public void WriteTo(Stream stream)
    {
        if (typeof(T) == typeof(byte) &&
            this.array is byte[] bytes)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            stream.Write(bytes, offset: 0, count: this.index);
        }
        else
        {
            throw new ArgumentException("T : byte expected");
        }
    }

    /// <summary>
    /// Throws an <see cref="ArgumentOutOfRangeException"/> when the requested count is negative.
    /// </summary>
    private static void ThrowArgumentOutOfRangeExceptionForNegativeCount()
    {
        throw new ArgumentOutOfRangeException("count", "The count can't be a negative value.");
    }

    /// <summary>
    /// Throws an <see cref="ArgumentOutOfRangeException"/> when the size hint is negative.
    /// </summary>
    private static void ThrowArgumentOutOfRangeExceptionForNegativeSizeHint()
    {
        throw new ArgumentOutOfRangeException("sizeHint", "The size hint can't be a negative value.");
    }

    /// <summary>
    /// Throws an <see cref="ArgumentOutOfRangeException"/> when the requested count is negative.
    /// </summary>
    private static void ThrowArgumentExceptionForAdvancedTooFar()
    {
        throw new ArgumentException("The buffer writer has advanced too far.");
    }

    /// <summary>
    /// Throws an <see cref="ObjectDisposedException"/> when <see cref="array"/> is <see langword="null"/>.
    /// </summary>
    private static void ThrowObjectDisposedException()
    {
        throw new ObjectDisposedException("The current buffer has already been disposed.");
    }
}
