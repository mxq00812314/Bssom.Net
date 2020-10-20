﻿using BssomSerializers.Internal;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace BssomSerializers.BssomBuffer
{
    internal sealed class ExpandableBufferWriter : IBssomBufferWriter, IDisposable
    {
        private const int MinimumBufferSize = short.MaxValue;

        private BssomComplexBuffer complexBuffer;
        private int[] bufferedsRelativeSpan;

        public ExpandableBufferWriter(byte[] bufData) : this(new BssomComplexBuffer(bufData))
        {
        }

        public ExpandableBufferWriter(BssomComplexBuffer complexBuffer)
        {
            this.complexBuffer = complexBuffer;
            this.bufferedsRelativeSpan = new int[complexBuffer.Spans.Length];
        }

        /// <summary>
        /// <see cref="complexBuffer"/> position
        /// </summary>
        public long Position => complexBuffer.Position;
        /// <summary>
        /// Records all the buffered in simpleBuffer. When SpanBuffered in simpleBuffer is changed, this parameter will be changed at the same time
        /// </summary>
        public long Buffered { get; private set; } = 0;
        /// <summary>
        /// Records the Buffered of the CurrentSpan in simpleBuffer, this value is only used internally
        /// </summary>
        private int CurrentSpanBuffered => bufferedsRelativeSpan[bufferedsRelativeSpan.Length - 1];

        /// <summary>
        /// flush the boundary of the last Span in the <see cref="complexBuffer"/>, and then return 
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IBssomBuffer GetBssomBuffer()
        {
            FlushLastSpanBoundary();
            return complexBuffer;
        }

        /// <summary>
        /// Only when the current Span is position and buffered in <see cref="complexBuffer"/> are the same, advance forward
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int count)
        {
            if (complexBuffer.CurrentSpanPosition == CurrentSpanBuffered)
            {
                Buffered += count;
                bufferedsRelativeSpan[complexBuffer.CurrentSpanIndex] += count;
            }

            complexBuffer.SeekWithOutVerify(count, BssomSeekOrgin.Current);
        }

        /// <summary>
        /// There are no restrictions, so call the <see cref="complexBuffer"/> is method directly
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SeekWithOutVerify(long postion, BssomSeekOrgin orgin)
        {
            complexBuffer.SeekWithOutVerify(postion, orgin);
        }

        /// <summary>
        /// Seeking in the writer will be constrained by <see cref="Buffered"/>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Seek(long position, BssomSeekOrgin orgin = BssomSeekOrgin.Begin)
        {
            complexBuffer.Seek(position, orgin, Buffered);
        }

        /// <summary>
        /// When the capacity of <see cref="complexBuffer"/> is not enough to write size, a new span will be generated
        /// If the span found is not the last one and the size has exceeded the boundary, so in order to keep the upper-level logical behavior consistent, an exception will be thrown
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte GetRef(int sizeHint = 0)
        {
            if (complexBuffer.CurrentSpanPosition + sizeHint > complexBuffer.CurrentSpan.Boundary)
                MoveNextSpan(sizeHint);
            return ref complexBuffer.ReadRef(sizeHint);
        }

        /// <summary>
        /// Traverse each span in <see cref="complexBuffer"/>, combine the Buffered parts of each span and return
        /// </summary>
        public byte[] GetBufferedArray()
        {
            byte[] array;
            if (complexBuffer.Spans.Length == 1)
            {
                array = new byte[Buffered];
                if (array.Length != 0)
                    Unsafe.CopyBlock(ref array[0], ref complexBuffer.CurrentSpan.Buffer[0], (uint)Buffered);
            }
            else
            {
                FlushLastSpanBoundary();
                array = new byte[Buffered];
                int start = 0;
                for (int i = 0; i < complexBuffer.Spans.Length; i++)
                {
                    if (bufferedsRelativeSpan[i] != 0)
                    {
                        Unsafe.CopyBlock(ref array[start], ref complexBuffer.Spans[i].Buffer[0], (uint)bufferedsRelativeSpan[i]);
                        start += bufferedsRelativeSpan[i];
                    }
                }
            }
            return array;
        }


        public void CopyTo(Stream stream, CancellationToken CancellationToken)
        {
            if (Buffered > 0)
            {
                FlushLastSpanBoundary();
                for (int i = 0; i < complexBuffer.Spans.Length; i++)
                {
                    if (bufferedsRelativeSpan[i] != 0)
                    {
                        CancellationToken.ThrowIfCancellationRequested();
                        stream.Write(complexBuffer.Spans[i].Buffer, 0, bufferedsRelativeSpan[i]);
                    }
                }
            }
        }

        public async Task CopyToAsync(Stream stream, CancellationToken CancellationToken)
        {
            if (Buffered > 0)
            {
                FlushLastSpanBoundary();
                for (int i = 0; i < complexBuffer.Spans.Length; i++)
                {
                    if (bufferedsRelativeSpan[i] != 0)
                    {
                        CancellationToken.ThrowIfCancellationRequested();
                        await stream.WriteAsync(complexBuffer.Spans[i].Buffer, 0, bufferedsRelativeSpan[i]).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        /// Look for spans that satisfy that capacity, and if the current span is not the last, then logically go back to <see cref="GetRef"/>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MoveNextSpan(int size)
        {
            if (complexBuffer.CurrentSpanIsLast)
            {
                if (complexBuffer.CurrentSpanPosition + size <= complexBuffer.CurrentSpan.BufferLength)
                {
                    //Only when GetBuffer is called can the branch be entered,so need to restore the logic of refreshing Boundary to Buffered in GetBuffer, and re-assign BufferLength to Boundary
                    complexBuffer.Spans[complexBuffer.CurrentSpanIndex].Boundary = complexBuffer.CurrentSpan.BufferLength;
                    return;
                }
                //Add
                FlushLastSpanBoundary();
                CreateBufferSpan(size * 2);
            }

            complexBuffer.MoveToNextSpan();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CreateBufferSpan(int size)
        {
            if (size < MinimumBufferSize)
                size = MinimumBufferSize;
            var span = new BssomComplexBuffer.BufferSpan(ArrayPool<byte>.Shared.Rent(size));

            this.SpansResize(complexBuffer.Spans.Length + 1);

            complexBuffer.Spans[complexBuffer.Spans.Length - 1] = span;
            complexBuffer.SpansCumulativeBoundary[complexBuffer.SpansCumulativeBoundary.Length - 1] = complexBuffer.SpansCumulativeBoundary[complexBuffer.SpansCumulativeBoundary.Length - 2] + complexBuffer.Spans[complexBuffer.Spans.Length - 2].Boundary;
        }

        private void SpansResize(int capacity)
        {
            complexBuffer.SpansResize(capacity);
            Array.Resize(ref complexBuffer.SpansCumulativeBoundary, capacity);
            Array.Resize(ref bufferedsRelativeSpan, capacity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FlushLastSpanBoundary()
        {
            complexBuffer.Spans[complexBuffer.Spans.Length - 1].Boundary = bufferedsRelativeSpan[bufferedsRelativeSpan.Length - 1];
        }

        public static ExpandableBufferWriter CreateTemporary()
        {
            return new ExpandableBufferWriter(new BssomComplexBuffer(ArrayPool<byte>.Shared.Rent(1024)));
        }

        public static ExpandableBufferWriter CreateGlobar()
        {
            return new ExpandableBufferWriter(new BssomComplexBuffer(BssomSerializerIBssomBufferWriterBufferCache.GetUnsafeBssomArrayCache()));
        }

        public void Dispose()
        {
            if (complexBuffer.Spans.Length > 1)
            {
                for (int i = 1; i < complexBuffer.Spans.Length; i++)
                {
                    ArrayPool<byte>.Shared.Return(complexBuffer.Spans[i].Buffer);
                }
            }
        }
    }
}