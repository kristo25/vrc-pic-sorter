using System.Buffers.Binary;

namespace VrcPicSorter.Core.Imaging;

/// <summary>Repairs only missing transparent palette entries, without altering the source.</summary>
internal static class GifPaletteCompatibility
{
    private sealed record Table(long PackedOffset, byte Packed, long End, int Count)
    {
        public int RequiredCount { get; set; } = Count;
    }

    private sealed record Frame(long DataOffset, int MinimumCodeSize, long Pixels, Table Table, int Transparent);
    private sealed record Edit(long Offset, int Removed, byte[] Bytes);

    public static Stream? CreateView(Stream source)
    {
        var start = source.Position;
        try
        {
            if (source.Length - start > ImageResourceLimits.MaximumEncodedBytes)
                throw Invalid("Encoded-file resource limit exceeded.");
            Span<byte> header = stackalloc byte[13];
            source.ReadExactly(header);
            if (!header[..6].SequenceEqual("GIF87a"u8) && !header[..6].SequenceEqual("GIF89a"u8))
                throw Invalid("Invalid signature.");
            var width = Word(header, 6);
            var height = Word(header, 8);
            ImageResourceLimits.EnsureSafe(width, height, 1);
            var tables = new List<Table>();
            var global = ReadTable(source, start + 10, header[10], tables);
            var frames = new List<Frame>();
            var transparent = -1;
            var invalidExtensionSize = false;
            Span<byte> descriptor = stackalloc byte[9];
            while (true)
            {
                var marker = Byte(source);
                if (marker == 0x3b) break;
                if (marker == 0x21)
                {
                    var label = Byte(source);
                    if (label == 0xf9)
                    {
                        if (Byte(source) != 4) throw Invalid("Invalid graphic control extension.");
                        var flags = Byte(source);
                        Byte(source); Byte(source);
                        var index = Byte(source);
                        if (Byte(source) != 0) throw Invalid("Invalid graphic control terminator.");
                        transparent = (flags & 1) != 0 ? index : -1;
                    }
                    else
                    {
                        if (label is 0x01 or 0xff)
                        {
                            var count = Byte(source);
                            if (count != (label == 0x01 ? 12 : 11)) invalidExtensionSize = true;
                            Skip(source, count);
                            if (count != 0) SkipBlocks(source);
                        }
                        else SkipBlocks(source);
                        // Plain text is a graphic rendering block and consumes the pending GCE.
                        if (label == 0x01) transparent = -1;
                    }
                    continue;
                }
                if (marker != 0x2c) throw Invalid("Invalid block marker.");
                source.ReadExactly(descriptor);
                var frameWidth = Word(descriptor, 4);
                var frameHeight = Word(descriptor, 6);
                if (frameWidth == 0 || frameHeight == 0)
                    throw Invalid("Invalid image rectangle.");
                width = Math.Max(width, Word(descriptor, 0) + frameWidth);
                height = Math.Max(height, Word(descriptor, 2) + frameHeight);
                ImageResourceLimits.EnsureSafe(width, height, frames.Count + 1);
                var table = ReadTable(source, source.Position - 1, descriptor[8], tables) ?? global
                    ?? throw Invalid("Image has no colour table.");
                if (transparent >= table.Count)
                {
                    var required = table.Count;
                    while (required <= transparent) required *= 2;
                    table.RequiredCount = Math.Max(table.RequiredCount, required);
                }
                var minimum = Byte(source);
                if (minimum is < 2 or > 8) throw Invalid("Invalid LZW code size.");
                frames.Add(new Frame(source.Position, minimum, (long)frameWidth * frameHeight, table, transparent));
                SkipBlocks(source);
                transparent = -1;
            }
            if (!tables.Any(table => table.RequiredCount != table.Count)) return null;
            if (invalidExtensionSize) throw Invalid("Invalid fixed extension header size.");
            if (global is not null && header[11] >= global.Count)
                throw Invalid("Background colour index is outside its original palette.");
            // Validate original indices before expanding a palette, including opaque frames sharing it.
            foreach (var frame in frames) ValidateIndices(source, frame);
            var edits = new List<Edit>();
            foreach (var table in tables.Where(table => table.RequiredCount != table.Count))
            {
                var size = 0;
                for (var count = 2; count < table.RequiredCount; count *= 2) size++;
                edits.Add(new Edit(table.PackedOffset, 1, [(byte)((table.Packed & 0xf8) | size)]));
                edits.Add(new Edit(table.End, 0, new byte[(table.RequiredCount - table.Count) * 3]));
            }
            return new Overlay(source, start, edits.OrderBy(edit => edit.Offset).ToArray());
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Truncated GIF data.", exception);
        }
        finally { source.Position = start; }
    }

    private static Table? ReadTable(Stream source, long packedOffset, byte packed, List<Table> tables)
    {
        if ((packed & 0x80) == 0) return null;
        var count = 1 << ((packed & 7) + 1);
        Skip(source, count * 3);
        var table = new Table(packedOffset, packed, source.Position, count);
        tables.Add(table);
        return table;
    }

    private static int Word(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    private static int Byte(Stream source)
    {
        var value = source.ReadByte();
        return value < 0 ? throw new EndOfStreamException() : value;
    }
    private static void Skip(Stream source, int count)
    {
        if (count > source.Length - source.Position) throw new EndOfStreamException();
        source.Position += count;
    }
    private static void SkipBlocks(Stream source)
    {
        int count;
        while ((count = Byte(source)) != 0) Skip(source, count);
    }
    private static InvalidDataException Invalid(string message) => new($"Invalid GIF: {message}");

    private static void ValidateIndices(Stream source, Frame frame)
    {
        source.Position = frame.DataOffset;
        var reader = new Codes(source);
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var stack = new byte[4096];
        var clear = 1 << frame.MinimumCodeSize;
        var end = clear + 1;
        var next = end + 1;
        var size = frame.MinimumCodeSize + 1;
        var previous = -1;
        byte first = 0;
        long pixels = 0;
        while (true)
        {
            var code = reader.Read(size);
            if (code == clear)
            {
                next = end + 1; size = frame.MinimumCodeSize + 1; previous = -1;
                continue;
            }
            if (code == end)
            {
                if (pixels != frame.Pixels) throw Invalid("Incorrect decoded pixel count.");
                return;
            }
            if (code < 0 || code > next || (code == next && previous < 0)) throw Invalid("Invalid LZW code.");
            var original = code;
            var length = 0;
            if (code == next)
            {
                stack[length++] = first;
                code = previous;
            }
            while (code >= clear)
            {
                if (code >= next || length >= stack.Length - 1) throw Invalid("Invalid LZW dictionary chain.");
                stack[length++] = suffix[code];
                code = prefix[code];
            }
            first = (byte)code;
            stack[length++] = first;
            while (length > 0)
            {
                var index = stack[--length];
                if (index >= frame.Table.Count && index != frame.Transparent)
                    throw Invalid("Visible colour index is outside its original palette.");
                if (++pixels > frame.Pixels) throw Invalid("Too many decoded pixels.");
            }
            if (previous >= 0 && next < 4096)
            {
                prefix[next] = (short)previous;
                suffix[next++] = first;
                if (next == 1 << size && size < 12) size++;
            }
            previous = original;
        }
    }

    private sealed class Codes(Stream source)
    {
        private int remaining;
        private uint buffer;
        private int bits;
        public int Read(int size)
        {
            while (bits < size)
            {
                if (remaining == 0)
                {
                    remaining = Byte(source);
                    if (remaining == 0) throw Invalid("Missing LZW end code.");
                }
                buffer |= (uint)Byte(source) << bits;
                remaining--;
                bits += 8;
            }
            var value = (int)(buffer & ((1u << size) - 1));
            buffer >>= size;
            bits -= size;
            return value;
        }
    }

    private sealed class Overlay : Stream
    {
        private sealed record Segment(long Start, long Length, long SourceOffset, byte[]? Bytes);
        private readonly Stream source;
        private readonly long sourceStart;
        private readonly List<Segment> segments = [];
        private readonly long length;
        private long position;
        public Overlay(Stream source, long start, Edit[] edits)
        {
            this.source = source;
            sourceStart = start;
            var cursor = start;
            long target = 0;
            foreach (var edit in edits)
            {
                if (edit.Offset > cursor)
                {
                    segments.Add(new Segment(target, edit.Offset - cursor, cursor, null));
                    target += edit.Offset - cursor;
                }
                segments.Add(new Segment(target, edit.Bytes.Length, 0, edit.Bytes));
                target += edit.Bytes.Length;
                cursor = edit.Offset + edit.Removed;
            }
            segments.Add(new Segment(target, source.Length - cursor, cursor, null));
            length = target + source.Length - cursor;
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var total = 0;
            foreach (var segment in segments)
            {
                if (position < segment.Start || position >= segment.Start + segment.Length) continue;
                var offset = position - segment.Start;
                var count = (int)Math.Min(buffer.Length, segment.Length - offset);
                if (count == 0) break;
                if (segment.Bytes is { } bytes) bytes.AsSpan((int)offset, count).CopyTo(buffer);
                else
                {
                    source.Position = segment.SourceOffset + offset;
                    source.ReadExactly(buffer[..count]);
                }
                position += count; total += count; buffer = buffer[count..];
                if (buffer.IsEmpty) break;
            }
            return total;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = checked((origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => position,
                SeekOrigin.End => length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            }) + offset);
            if (target < 0) throw new IOException("Cannot seek before the GIF stream.");
            return position = target;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Position = sourceStart;
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
