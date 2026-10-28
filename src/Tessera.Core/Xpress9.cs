using System.Buffers.Binary;
using System.Text;

namespace Tessera.Core;

/// <summary>
/// Reads the XPress9-compressed <c>DataModel</c> stream inside a .pbix. The decoder is a port of
/// Microsoft's MIT-licensed XPress9 reference implementation (LZ77 with move-to-front offsets and
/// canonical Huffman codes); the stream framing follows pbixray (MIT).
/// </summary>
public static class Xpress9
{
    const int SignatureBytes = 102;
    const string SingleThreaded = "This backup was created using XPress9 compression.";
    const string MultiThreaded = "This backup was created using multithreaded XPrs9.";
    const string Uncompressed = "STREAM_STORAGE_SIGNATURE_)!@#$%^&*(";

    /// <summary>Decompresses a DataModel stream into <paramref name="output"/> (an uncompressed ABF backup).</summary>
    public static void DecompressDataModel(Stream input, Stream output)
    {
        var signature = new byte[SignatureBytes];
        input.ReadExactly(signature);
        var text = Encoding.Unicode.GetString(signature);

        if (text.Contains(Uncompressed))
        {
            output.Write(signature);
            input.CopyTo(output);
        }
        else if (text.Contains(SingleThreaded))
        {
            var decoder = new Xpress9Decoder();
            while (DecompressChunk(input, output, decoder, allowEof: true)) { }
        }
        else if (text.Contains(MultiThreaded))
        {
            Span<byte> header = stackalloc byte[40];
            input.ReadExactly(header);
            var mainChunksPerThread = BinaryPrimitives.ReadInt64LittleEndian(header);
            var prefixChunksPerThread = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
            var prefixThreads = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
            var mainThreads = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);

            // Each thread's chunks form an independent session (own history window); prefix groups come first.
            foreach (var (threads, chunks) in new[] { (prefixThreads, prefixChunksPerThread), (mainThreads, mainChunksPerThread) })
            {
                for (var t = 0; t < threads; t++)
                {
                    var decoder = new Xpress9Decoder();
                    for (var c = 0; c < chunks; c++) DecompressChunk(input, output, decoder, allowEof: false);
                }
            }
        }
        else
        {
            throw new InvalidDataException("The data model uses an unknown compression format.");
        }
    }

    /// <summary>Reads one length-prefixed chunk and writes its decompressed bytes. Returns false at end of input.</summary>
    static bool DecompressChunk(Stream input, Stream output, Xpress9Decoder decoder, bool allowEof)
    {
        Span<byte> sizes = stackalloc byte[8];
        var read = input.ReadAtLeast(sizes, 8, throwOnEndOfStream: false);
        if (read == 0 && allowEof) return false;
        if (read < 8) throw new InvalidDataException("The data model ends in the middle of a chunk header.");

        var originalSize = BinaryPrimitives.ReadInt32LittleEndian(sizes);
        var compressedSize = BinaryPrimitives.ReadInt32LittleEndian(sizes[4..]);
        if (originalSize < 0 || compressedSize < 0) throw new InvalidDataException("Bad chunk size in the data model.");

        var compressed = new byte[compressedSize];
        input.ReadExactly(compressed);
        output.Write(decoder.Decompress(compressed, originalSize));
        return true;
    }
}

/// <summary>
/// One XPress9 decoding session. Blocks are decoded in order; later blocks may refer back into
/// earlier ones, so the tail of the output is kept as the LZ77 window.
/// </summary>
public sealed class Xpress9Decoder
{
    const uint Magic = 0x4e86d72a;
    const int HeaderBytes = 32;
    const int MaxShortLength = 16;          // low 4 bits of a pointer symbol hold the match length
    const int ShortAlphabetSize = 256 + ((24 + 4) << 4);
    const int LongAlphabetSize = 256;
    const int MaxLongLength = LongAlphabetSize - 24;
    const int MaxCodewordLength = 27;

    byte[] _buffer = [];
    int _historyLength;
    uint _blockIndex;
    uint _sessionSignature;
    int _windowLog2, _mtfCount, _ptrMinMatch, _mtfMinMatch;

    /// <summary>Decompresses one block (a chunk of the DataModel stream) of <paramref name="originalSize"/> bytes.</summary>
    public byte[] Decompress(ReadOnlySpan<byte> block, int originalSize)
    {
        if (block.Length < HeaderBytes) throw Corrupt("block is shorter than its header");
        Span<uint> h = stackalloc uint[8];
        for (var i = 0; i < 8; i++) h[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]);

        if (h[0] != Magic) throw Corrupt("bad block magic");
        if (_blockIndex == 0) _sessionSignature = h[5];
        if (h[6] != _blockIndex || h[5] != _sessionSignature || h[4] != 0) throw Corrupt("block is out of sequence");
        if (h[1] != originalSize) throw Corrupt("block size disagrees with the chunk header");

        //  13 bits  length of the Huffman tables in bits
        //   3 bits  log2(window size) - 16
        //   2 bits  number of MTF entries / 2
        //   1 bit   min pointer match length - 3
        //   1 bit   min MTF match length - 2
        var flags = h[3];
        if (flags >> 20 != 0) throw Corrupt("reserved flags are set");
        var huffmanBits = (int)(flags & 0x1fff);
        var windowLog2 = (int)((flags >> 13) & 7) + 16;
        var mtfCount = (int)((flags >> 16) & 3) << 1;
        var ptrMinMatch = (int)((flags >> 18) & 1) + 3;
        var mtfMinMatch = (int)((flags >> 19) & 1) + 2;
        if (mtfCount > 4) throw Corrupt("too many MTF entries");
        if (_blockIndex == 0)
            (_windowLog2, _mtfCount, _ptrMinMatch, _mtfMinMatch) = (windowLog2, mtfCount, ptrMinMatch, mtfMinMatch);
        else if ((_windowLog2, _mtfCount, _ptrMinMatch, _mtfMinMatch) != (windowLog2, mtfCount, ptrMinMatch, mtfMinMatch))
            throw Corrupt("block parameters changed mid-session");

        var encodedBits = (long)h[2];
        if (encodedBits <= huffmanBits + HeaderBytes * 8 || (encodedBits + 7) / 8 > block.Length)
            throw Corrupt("bad encoded size");

        var bits = new BitReader(block[HeaderBytes..(int)((encodedBits + 7) / 8)]);

        // Block state: MTF offsets, then the two Huffman tables.
        var lastWasPointer = false;
        Span<long> mtf = stackalloc long[4];
        if (_mtfCount > 0)
        {
            lastWasPointer = bits.Read(1) != 0;
            for (var i = 0; i < _mtfCount; i++)
            {
                var msb = (int)bits.Read(5);
                if (msb >= _windowLog2) throw Corrupt("MTF offset exceeds the window");
                mtf[i] = bits.Read(msb) + (1L << msb);
            }
        }
        var shortSymbols = new Huffman(DecodeLengthTable(ref bits, ShortAlphabetSize));
        var longLengths = new Huffman(DecodeLengthTable(ref bits, LongAlphabetSize));
        if (bits.Consumed != huffmanBits) throw Corrupt("Huffman table size mismatch");

        // Decode into the buffer after the retained history so matches can reach back into it.
        var start = _historyLength;
        var end = start + originalSize;
        if (_buffer.Length < end) Array.Resize(ref _buffer, Math.Max(end, _buffer.Length * 2));
        var buf = _buffer;
        var pos = start;
        while (pos < end)
        {
            var symbol = shortSymbols.Decode(ref bits);
            if (symbol < 256)
            {
                buf[pos++] = (byte)symbol;
                lastWasPointer = false;
                continue;
            }

            symbol -= 256;
            long length = symbol & (MaxShortLength - 1);
            var slot = symbol >> 4;
            if (length == MaxShortLength - 1)
            {
                length = longLengths.Decode(ref bits);
                if (length >= MaxLongLength)
                {
                    var extra = (int)length - MaxLongLength;
                    length = bits.Read(extra) + (1L << extra) + MaxLongLength - 1;
                }
                length += MaxShortLength - 1;
            }

            long offset;
            if (slot < _mtfCount)
            {
                length += _mtfMinMatch;
                if (lastWasPointer)
                {
                    // Right after a pointer, repeating offset 0 is pointless, so slots shift down by one.
                    switch (slot)
                    {
                        case 0: offset = mtf[1]; break;
                        case 1 when _mtfCount > 2: offset = mtf[2]; mtf[2] = mtf[1]; break;
                        case 2 when _mtfCount > 2: offset = mtf[3]; mtf[3] = mtf[2]; mtf[2] = mtf[1]; break;
                        default: throw Corrupt("bad MTF slot");
                    }
                    mtf[1] = mtf[0];
                    mtf[0] = offset;
                }
                else
                {
                    offset = mtf[slot];
                    for (var i = slot; i > 0; i--) mtf[i] = mtf[i - 1];
                    mtf[0] = offset;
                }
            }
            else
            {
                length += _ptrMinMatch;
                var msb = slot - _mtfCount;
                offset = bits.Read(msb) + (1L << msb);
                for (var i = _mtfCount - 1; i > 0; i--) mtf[i] = mtf[i - 1];
                if (_mtfCount > 0) mtf[0] = offset;
            }

            if (offset > pos) throw Corrupt("match reaches before the start of the stream");
            if (pos + length > end) throw Corrupt("match runs past the end of the block");
            lastWasPointer = true;

            // Byte by byte: matches may overlap their own output.
            var src = pos - (int)offset;
            for (var stop = pos + (int)length; pos < stop;) buf[pos++] = buf[src++];
        }

        if (bits.Consumed != encodedBits - HeaderBytes * 8) throw Corrupt("block did not end where its header said");

        var result = buf.AsSpan(start, originalSize).ToArray();
        var keep = Math.Min(end, 1 << _windowLog2);
        Buffer.BlockCopy(buf, end - keep, buf, 0, keep);
        _historyLength = keep;
        _blockIndex++;
        return result;
    }

    // Small-table symbols beyond the literal code lengths 0..27.
    const int Fill = MaxCodewordLength + 1;       // zeros up to the next row boundary
    const int ZeroRepeat = MaxCodewordLength + 2; // a counted run of zeros
    const int Previous = MaxCodewordLength + 3;   // repeat the last non-zero length
    const int Row0 = MaxCodewordLength + 4;       // same as one row up
    const int Row1 = MaxCodewordLength + 5;       // one more than one row up
    const int SmallAlphabetSize = MaxCodewordLength + 6;
    const int RowSize = MaxShortLength;

    /// <summary>Reads the code lengths of a Huffman table, which are themselves Huffman-coded.</summary>
    static byte[] DecodeLengthTable(ref BitReader bits, int alphabetSize)
    {
        var lengths = new byte[alphabetSize];
        var kind = bits.Read(3);
        if (kind == 0)
        {
            // "Stored": a near-uniform code where the first symbols get one bit fewer than the rest.
            var msb = 31 - int.LeadingZeroCount(alphabetSize);
            var shorter = (1 << (msb + 1)) - alphabetSize;
            for (var i = 0; i < alphabetSize; i++) lengths[i] = (byte)(i < shorter ? msb : msb + 1);
            return lengths;
        }
        if (kind != 1) throw Corrupt("unknown Huffman table encoding");

        var small = new byte[SmallAlphabetSize];
        var prev = 4;
        for (var i = 0; i < small.Length; i++)
        {
            if (bits.Read(1) == 0) { small[i] = (byte)prev; continue; }
            var k = (int)bits.Read(3);
            if (k >= prev && ++k > 8) throw Corrupt("bad small table");
            small[i] = (byte)k;
            prev = k;
        }
        var smallCode = new Huffman(small);

        prev = 8;
        for (var i = 0; i < alphabetSize;)
        {
            var symbol = smallCode.Decode(ref bits);
            switch (symbol)
            {
                case Fill:
                    do lengths[i++] = 0; while ((i & (RowSize - 1)) != 0 && i < alphabetSize);
                    break;

                case ZeroRepeat:
                    var count = (int)bits.Read(2) + 5;
                    if (count == 8)
                    {
                        uint more;
                        do count += (int)(more = bits.Read(3)); while (more == 7);
                    }
                    if (i + count > alphabetSize || (i ^ (i + count)) >= RowSize) throw Corrupt("zero run crosses a row");
                    while (count-- > 0) lengths[i++] = 0;
                    break;

                case Previous:
                    lengths[i++] = (byte)prev;
                    break;

                case Row0 or Row1:
                    if (i < RowSize) throw Corrupt("row reference in the first row");
                    var length = lengths[i - RowSize] + (symbol == Row1 ? 1 : 0);
                    if (length == 0 || length > MaxCodewordLength) throw Corrupt("bad row reference");
                    lengths[i++] = (byte)length;
                    prev = length;
                    break;

                default:
                    lengths[i++] = (byte)symbol;
                    if (symbol != 0) prev = symbol;
                    break;
            }
        }
        return lengths;
    }

    static InvalidDataException Corrupt(string detail) => new($"The data model is corrupt ({detail}).");

    /// <summary>Canonical Huffman code read least-significant bit first (the first bit read is the code's top bit).</summary>
    sealed class Huffman
    {
        const int FastBits = 12;
        readonly int[] _fast = new int[1 << FastBits];   // (symbol << 5) | length, or 0 to take the slow path
        readonly int[] _countByLength = new int[MaxCodewordLength + 1];
        readonly int[] _symbolsByCode;
        readonly int _maxLength;
        readonly int _onlySymbol = -1;

        public Huffman(byte[] lengths)
        {
            var used = 0;
            foreach (var l in lengths)
            {
                if (l == 0) continue;
                _countByLength[l]++;
                _maxLength = Math.Max(_maxLength, l);
                used++;
            }
            if (used == 0) throw Corrupt("empty Huffman table");

            _symbolsByCode = new int[used];
            var n = 0;
            for (var l = 1; l <= _maxLength; l++)
                for (var s = 0; s < lengths.Length; s++)
                    if (lengths[s] == l) _symbolsByCode[n++] = s;

            if (used == 1)
            {
                // A lone symbol still costs its stated number of bits.
                _onlySymbol = _symbolsByCode[0];
                _maxLength = lengths[_onlySymbol];
                return;
            }

            // The code must be complete (Kraft sum exactly 1).
            long left = 1;
            for (var l = 1; l <= _maxLength; l++)
            {
                left = (left << 1) - _countByLength[l];
                if (left < 0) throw Corrupt("over-subscribed Huffman table");
            }
            if (left != 0) throw Corrupt("incomplete Huffman table");

            var code = 0;
            n = 0;
            for (var l = 1; l <= _maxLength; l++, code <<= 1)
            {
                for (var k = 0; k < _countByLength[l]; k++, code++)
                {
                    var symbol = _symbolsByCode[n++];
                    if (l > FastBits) continue;
                    var reversed = (int)(ReverseBits((uint)code) >> (32 - l));
                    for (var i = reversed; i < _fast.Length; i += 1 << l) _fast[i] = (symbol << 5) | l;
                }
            }
        }

        static uint ReverseBits(uint v)
        {
            v = ((v >> 1) & 0x55555555) | ((v & 0x55555555) << 1);
            v = ((v >> 2) & 0x33333333) | ((v & 0x33333333) << 2);
            v = ((v >> 4) & 0x0F0F0F0F) | ((v & 0x0F0F0F0F) << 4);
            return BinaryPrimitives.ReverseEndianness(v);
        }

        public int Decode(ref BitReader bits)
        {
            if (_onlySymbol >= 0)
            {
                bits.Skip(_maxLength);
                return _onlySymbol;
            }

            var entry = _fast[bits.Peek(FastBits)];
            if (entry != 0)
            {
                bits.Skip(entry & 31);
                return entry >> 5;
            }

            // Longer than the fast table: walk the canonical code one bit at a time.
            int code = 0, first = 0, index = 0;
            for (var l = 1; l <= _maxLength; l++)
            {
                code |= (int)bits.Read(1);
                var count = _countByLength[l];
                if (code - first < count) return _symbolsByCode[index + code - first];
                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }
            throw Corrupt("bad Huffman code");
        }
    }

    /// <summary>Least-significant-bit-first reader; reads past the end yield zeros (caught by the size checks).</summary>
    ref struct BitReader(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> _data = data;
        int _pos;
        ulong _buffer;
        int _count;

        public long Consumed { get; private set; }

        void Refill()
        {
            if (_pos + 8 <= _data.Length)
            {
                _buffer |= BinaryPrimitives.ReadUInt64LittleEndian(_data[_pos..]) << _count;
                _pos += (63 - _count) >> 3;
                _count |= 56;
                return;
            }
            while (_count <= 56)
            {
                ulong b = _pos < _data.Length ? _data[_pos] : 0u;
                _pos++;
                _buffer |= b << _count;
                _count += 8;
            }
        }

        public uint Peek(int n)
        {
            if (_count < n) Refill();
            return (uint)(_buffer & ((1UL << n) - 1));
        }

        public void Skip(int n)
        {
            if (_count < n) Refill();
            _buffer >>= n;
            _count -= n;
            Consumed += n;
        }

        public uint Read(int n)
        {
            if (n == 0) return 0;
            var v = Peek(n);
            Skip(n);
            return v;
        }
    }
}
