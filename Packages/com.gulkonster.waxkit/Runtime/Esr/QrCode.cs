using System;
using System.Collections.Generic;
using System.Text;

namespace Gulkonster.WaxKit.Esr
{
    /// <summary>
    /// QR code, byte mode, error correction level M, smallest version that fits. Version and mask choice follow
    /// qrcode-generator (the encoder the WebGL bundle used before), so codes are identical.
    /// </summary>
    internal sealed class QrCode
    {
        static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 };
        static readonly int[] Blocks = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 };
        // Format bits use 0 for level M.
        const int LevelBits = 0;

        readonly bool?[,] modules;

        /// <summary>Modules (squares) per side.</summary>
        public int Size { get; }

        QrCode(int version)
        {
            Size = version * 4 + 17;
            modules = new bool?[Size, Size];
        }

        /// <summary>True when the module at row / column is dark.</summary>
        public bool IsDark(int row, int col) => modules[row, col] == true;

        /// <summary>Row by row, top-left first, '1' = dark.</summary>
        public string ToModuleString()
        {
            var builder = new StringBuilder(Size * Size);
            for (var r = 0; r < Size; r++)
            for (var c = 0; c < Size; c++)
                builder.Append(IsDark(r, c) ? '1' : '0');
            return builder.ToString();
        }

        /// <summary>Encodes the text (byte mode, error correction level M, as WharfKit).</summary>
        public static QrCode Encode(string text)
        {
            var data = Encoding.UTF8.GetBytes(text);
            var version = 1;
            for (; version < 40; version++)
            {
                var lengthBits = version < 10 ? 8 : 16;
                if (4 + lengthBits + data.Length * 8 <= DataCodewords(version) * 8) break;
            }
            if (4 + (version < 10 ? 8 : 16) + data.Length * 8 > DataCodewords(version) * 8) throw new ArgumentException("Text is too long for a QR code.");

            var codewords = BuildCodewords(data, version);

            QrCode best = null;
            double bestPoints = 0;
            for (var mask = 0; mask < 8; mask++)
            {
                var candidate = Build(version, codewords, mask, true);
                var points = candidate.LostPoints();
                if (mask == 0 || bestPoints > points)
                {
                    bestPoints = points;
                    best = Build(version, codewords, mask, false);
                }
            }
            return best;
        }

        // ---- Codewords ----

        static int RawModules(int version)
        {
            var result = (16 * version + 128) * version + 64;
            if (version >= 2)
            {
                var align = version / 7 + 2;
                result -= (25 * align - 10) * align - 55;
                if (version >= 7) result -= 36;
            }
            return result;
        }

        static int DataCodewords(int version) => RawModules(version) / 8 - EccPerBlock[version] * Blocks[version];

        static byte[] BuildCodewords(byte[] data, int version)
        {
            var bits = new List<bool>();
            void Put(int value, int length)
            {
                for (var i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) == 1);
            }

            Put(4, 4); // byte mode
            Put(data.Length, version < 10 ? 8 : 16);
            foreach (var b in data) Put(b, 8);

            var capacity = DataCodewords(version) * 8;
            if (bits.Count + 4 <= capacity) Put(0, 4);
            while (bits.Count % 8 != 0) bits.Add(false);
            for (var pad = 0xec; bits.Count < capacity; pad ^= 0xec ^ 0x11) Put(pad, 8);

            var dataBytes = new byte[bits.Count / 8];
            for (var i = 0; i < bits.Count; i++)
                if (bits[i]) dataBytes[i >> 3] |= (byte)(0x80 >> (i & 7));

            // Split into blocks (short blocks first), add Reed-Solomon codes, interleave.
            var blockCount = Blocks[version];
            var ecc = EccPerBlock[version];
            var rawCodewords = RawModules(version) / 8;
            var shortBlocks = blockCount - rawCodewords % blockCount;
            var shortLength = rawCodewords / blockCount;
            var divisor = Generator(ecc);

            var dataBlocks = new List<byte[]>();
            var eccBlocks = new List<byte[]>();
            for (int i = 0, offset = 0; i < blockCount; i++)
            {
                var length = shortLength - ecc + (i < shortBlocks ? 0 : 1);
                var block = new byte[length];
                Array.Copy(dataBytes, offset, block, 0, length);
                offset += length;
                dataBlocks.Add(block);
                eccBlocks.Add(Remainder(block, divisor));
            }

            var result = new List<byte>(rawCodewords);
            for (var i = 0; i <= shortLength - ecc; i++)
                foreach (var block in dataBlocks)
                    if (i < block.Length) result.Add(block[i]);
            for (var i = 0; i < ecc; i++)
                foreach (var block in eccBlocks)
                    result.Add(block[i]);
            return result.ToArray();
        }

        static byte[] Generator(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            var root = 1;
            for (var i = 0; i < degree; i++)
            {
                for (var j = 0; j < result.Length; j++)
                {
                    result[j] = Multiply(result[j], root);
                    if (j + 1 < result.Length) result[j] ^= result[j + 1];
                }
                root = Multiply(root, 0x02);
            }
            return result;
        }

        static byte[] Remainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (var b in data)
            {
                var factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (var i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
            }
            return result;
        }

        static byte Multiply(int x, int y)
        {
            var z = 0;
            for (var i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11d);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        // ---- Matrix ----

        static QrCode Build(int version, byte[] codewords, int mask, bool test)
        {
            var qr = new QrCode(version);
            qr.FinderPattern(0, 0);
            qr.FinderPattern(qr.Size - 7, 0);
            qr.FinderPattern(0, qr.Size - 7);
            qr.AlignmentPatterns(version);
            qr.TimingPatterns();
            qr.FormatBits(mask, test);
            if (version >= 7) qr.VersionBits(version, test);
            qr.MapData(codewords, mask);
            return qr;
        }

        void FinderPattern(int row, int col)
        {
            for (var r = -1; r <= 7; r++)
            for (var c = -1; c <= 7; c++)
            {
                if (row + r < 0 || row + r >= Size || col + c < 0 || col + c >= Size) continue;
                modules[row + r, col + c] = (r >= 0 && r <= 6 && (c == 0 || c == 6))
                                            || (c >= 0 && c <= 6 && (r == 0 || r == 6))
                                            || (r >= 2 && r <= 4 && c >= 2 && c <= 4);
            }
        }

        void AlignmentPatterns(int version)
        {
            if (version == 1) return;
            var count = version / 7 + 2;
            var step = (version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
            var positions = new List<int> { 6 };
            for (int i = 0, pos = Size - 7; i < count - 1; i++, pos -= step) positions.Insert(1, pos);

            foreach (var row in positions)
            foreach (var col in positions)
            {
                if (modules[row, col] != null) continue;
                for (var r = -2; r <= 2; r++)
                for (var c = -2; c <= 2; c++)
                    modules[row + r, col + c] = r == -2 || r == 2 || c == -2 || c == 2 || (r == 0 && c == 0);
            }
        }

        void TimingPatterns()
        {
            for (var i = 8; i < Size - 8; i++)
            {
                if (modules[i, 6] == null) modules[i, 6] = i % 2 == 0;
                if (modules[6, i] == null) modules[6, i] = i % 2 == 0;
            }
        }

        void FormatBits(int mask, bool test)
        {
            var data = (LevelBits << 3) | mask;
            var rem = data;
            for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            var bits = ((data << 10) | rem) ^ 0x5412;

            for (var i = 0; i < 15; i++)
            {
                var dark = !test && ((bits >> i) & 1) == 1;
                if (i < 6) modules[i, 8] = dark;
                else if (i < 8) modules[i + 1, 8] = dark;
                else modules[Size - 15 + i, 8] = dark;

                if (i < 8) modules[8, Size - i - 1] = dark;
                else if (i < 9) modules[8, 15 - i] = dark;
                else modules[8, 14 - i] = dark;
            }
            modules[Size - 8, 8] = !test;
        }

        void VersionBits(int version, bool test)
        {
            var rem = version;
            for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1f25);
            var bits = (version << 12) | rem;
            for (var i = 0; i < 18; i++)
            {
                var dark = !test && ((bits >> i) & 1) == 1;
                modules[i / 3, i % 3 + Size - 11] = dark;
                modules[i % 3 + Size - 11, i / 3] = dark;
            }
        }

        void MapData(byte[] data, int mask)
        {
            var inc = -1;
            var row = Size - 1;
            var bitIndex = 7;
            var byteIndex = 0;
            for (var col = Size - 1; col > 0; col -= 2)
            {
                if (col == 6) col--;
                while (true)
                {
                    for (var c = 0; c < 2; c++)
                    {
                        if (modules[row, col - c] != null) continue;
                        var dark = byteIndex < data.Length && ((data[byteIndex] >> bitIndex) & 1) == 1;
                        if (Mask(mask, row, col - c)) dark = !dark;
                        modules[row, col - c] = dark;
                        if (--bitIndex == -1)
                        {
                            byteIndex++;
                            bitIndex = 7;
                        }
                    }
                    row += inc;
                    if (row < 0 || row >= Size)
                    {
                        row -= inc;
                        inc = -inc;
                        break;
                    }
                }
            }
        }

        static bool Mask(int mask, int i, int j)
        {
            switch (mask)
            {
                case 0: return (i + j) % 2 == 0;
                case 1: return i % 2 == 0;
                case 2: return j % 3 == 0;
                case 3: return (i + j) % 3 == 0;
                case 4: return (i / 2 + j / 3) % 2 == 0;
                case 5: return i * j % 2 + i * j % 3 == 0;
                case 6: return (i * j % 2 + i * j % 3) % 2 == 0;
                default: return (i * j % 3 + (i + j) % 2) % 2 == 0;
            }
        }

        /// <summary>Mask penalty as qrcode-generator's getLostPoint.</summary>
        double LostPoints()
        {
            double points = 0;
            for (var row = 0; row < Size; row++)
            for (var col = 0; col < Size; col++)
            {
                var same = 0;
                var dark = IsDark(row, col);
                for (var r = -1; r <= 1; r++)
                {
                    if (row + r < 0 || row + r >= Size) continue;
                    for (var c = -1; c <= 1; c++)
                    {
                        if (col + c < 0 || col + c >= Size || (r == 0 && c == 0)) continue;
                        if (dark == IsDark(row + r, col + c)) same++;
                    }
                }
                if (same > 5) points += 3 + same - 5;
            }

            for (var row = 0; row < Size - 1; row++)
            for (var col = 0; col < Size - 1; col++)
            {
                var count = (IsDark(row, col) ? 1 : 0) + (IsDark(row + 1, col) ? 1 : 0) + (IsDark(row, col + 1) ? 1 : 0) + (IsDark(row + 1, col + 1) ? 1 : 0);
                if (count == 0 || count == 4) points += 3;
            }

            for (var row = 0; row < Size; row++)
            for (var col = 0; col < Size - 6; col++)
                if (FinderLike(i => IsDark(row, col + i))) points += 40;
            for (var col = 0; col < Size; col++)
            for (var row = 0; row < Size - 6; row++)
                if (FinderLike(i => IsDark(row + i, col))) points += 40;

            var darkCount = 0;
            for (var row = 0; row < Size; row++)
            for (var col = 0; col < Size; col++)
                if (IsDark(row, col)) darkCount++;
            points += Math.Abs(100.0 * darkCount / Size / Size - 50) / 5 * 10;
            return points;
        }

        static bool FinderLike(Func<int, bool> dark) =>
            dark(0) && !dark(1) && dark(2) && dark(3) && dark(4) && !dark(5) && dark(6);
    }
}
