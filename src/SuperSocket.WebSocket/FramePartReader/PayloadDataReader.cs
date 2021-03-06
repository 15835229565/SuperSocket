﻿using System;
using System.Buffers;
using System.Text;
using SuperSocket.ProtoBase;

namespace SuperSocket.WebSocket.FramePartReader
{
    class PayloadDataReader : PackagePartReader
    {
        public override bool Process(WebSocketPackage package, object filterContext, ref SequenceReader<byte> reader, out IPackagePartReader<WebSocketPackage> nextPartReader, out bool needMoreData)
        {
            nextPartReader = null;

            long required = package.PayloadLength;

            if (reader.Remaining < required)
            {
                needMoreData = true;
                return false;
            }

            needMoreData = false;

            var seq = reader.Sequence.Slice(reader.Consumed, required);

            if (package.HasMask)
                DecodeMask(ref seq, package.MaskKey);

            try
            {
                if (package.Data.Length == 0)
                {
                    package.Data = seq;
                }
                else
                {
                    var currentData = package.Data;
                    package.Data = ConcactSequence(ref currentData, ref seq);
                }

                if (package.FIN)
                {
                    var data = package.Data;

                    var websocketFilterContext = filterContext as WebSocketPipelineFilterContext;

                    if (websocketFilterContext != null && websocketFilterContext.Extensions != null && websocketFilterContext.Extensions.Count > 0)
                    {
                        foreach (var extension in websocketFilterContext.Extensions)
                        {
                            try
                            {
                                extension.Decode(ref data);
                            }
                            catch (Exception e)
                            {
                                throw new Exception($"Problem happened when decode with the extension {extension.Name}.", e);
                            }
                        }
                    }

                    if (package.OpCode == OpCode.Text)
                    {
                        package.Message = data.GetString(Encoding.UTF8);
                        package.Data = default;
                    }
                    else
                    {
                        package.Data = CopySequence(ref data);
                    }

                    return true;
                }
                else
                {
                    // start to process next fragment
                    nextPartReader = FixPartReader;
                    return false;
                }
            }
            finally
            {
                reader.Advance(required);
            }
        }

        private ReadOnlySequence<byte> CopySequence(ref ReadOnlySequence<byte> seq)
        {
            SequenceSegment head = null;
            SequenceSegment tail = null;

            foreach (var segment in seq)
            {                
                var newSegment = SequenceSegment.CopyFrom(segment);

                if (head == null)
                    tail = head = newSegment;
                else
                    tail = tail.SetNext(newSegment);
            }

            return new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        }

        private ReadOnlySequence<byte> ConcactSequence(ref ReadOnlySequence<byte> first, ref ReadOnlySequence<byte> second)
        {
            SequenceSegment head = first.Start.GetObject() as SequenceSegment;
            SequenceSegment tail = first.End.GetObject() as SequenceSegment;
            
            if (head == null)
            {
                foreach (var segment in first)
                {                
                    if (head == null)
                        tail = head = new SequenceSegment(segment);
                    else
                        tail = tail.SetNext(new SequenceSegment(segment));
                }
            }

            if (!second.IsEmpty)
            {
                foreach (var segment in second)
                {
                    tail = tail.SetNext(new SequenceSegment(segment));
                }
            }

            return new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        }

        internal unsafe void DecodeMask(ref ReadOnlySequence<byte> sequence, byte[] mask)
        {
            var index = 0;
            var maskLen = mask.Length;

            foreach (var piece in sequence)
            {
                fixed (byte* ptr = &piece.Span.GetPinnableReference())
                {
                    var span = new Span<byte>(ptr, piece.Span.Length);

                    for (var i = 0; i < span.Length; i++)
                    {
                        span[i] = (byte)(span[i] ^ mask[index++ % maskLen]);
                    }
                }
            }
        }
    }
}
