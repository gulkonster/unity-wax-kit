using System;

namespace UnityWaxKit
{
    /// <summary>
    /// GIF87a / GIF89a decoder written for the kit (no third-party code). Composites every frame onto the canvas as browsers
    /// do: global and local palettes, LZW with code-size growth and deferred clear, interlaced rows, transparency, disposal
    /// 0-3 (3 = restore the previous canvas), frames offset in or larger than the canvas (clipped), NETSCAPE loop count,
    /// delays below 20 ms shown as 100 ms (as browsers). A truncated file keeps the frames that finished. Limits (canvas size,
    /// frame count, decoded bytes) are checked before anything is allocated.
    /// </summary>
    internal static class WaxGifDecoder
    {
        const int MaxCodes = 4096;

        public static WaxDecodedImage Decode(byte[] data, WaxMediaLimits limits, bool firstFrameOnly)
        {
            if (data == null || data.Length < 13 || data[0] != 'G' || data[1] != 'I' || data[2] != 'F')
                throw Bad("The file is not a GIF.");
            var r = new Reader(data, 6);
            int width = r.U16(), height = r.U16();
            int packed = r.U8();
            r.U8(); // background index: browsers start from a transparent canvas
            r.U8(); // aspect ratio
            limits.CheckSize(width, height);

            var image = new WaxDecodedImage { Width = width, Height = height };
            var frameBytes = (long)width * height * 4;
            byte[] globalPalette = null;
            byte[] canvas = null;
            int delay = 0, disposal = 0, transparent = -1;

            try
            {
                if ((packed & 0x80) != 0) globalPalette = r.Bytes(3 << ((packed & 7) + 1));
                canvas = new byte[width * height * 4];
                while (true)
                {
                    var block = r.U8();
                    if (block == 0x3B) break; // trailer
                    if (block == 0x21)
                    {
                        var label = r.U8();
                        if (label == 0xF9)
                        {
                            var size = r.U8();
                            var start = r.Position;
                            var flags = r.U8();
                            delay = r.U16();
                            var index = r.U8();
                            disposal = (flags >> 2) & 7;
                            transparent = (flags & 1) != 0 ? index : -1;
                            r.Position = start + size;
                            r.SkipSubBlocks();
                        }
                        else if (label == 0xFF)
                        {
                            var size = r.U8();
                            var id = r.Ascii(size);
                            if ((id == "NETSCAPE2.0" || id == "ANIMEXTS1.0"))
                            {
                                while (true)
                                {
                                    var sub = r.U8();
                                    if (sub == 0) break;
                                    var subStart = r.Position;
                                    if (sub >= 3 && r.U8() == 1) image.LoopCount = r.U16();
                                    r.Position = subStart + sub;
                                }
                            }
                            else r.SkipSubBlocks();
                        }
                        else r.SkipSubBlocks();
                        continue;
                    }
                    if (block != 0x2C) throw new Truncated(); // unknown block: the rest cannot be trusted

                    int fx = r.U16(), fy = r.U16(), fw = r.U16(), fh = r.U16();
                    int fpacked = r.U8();
                    var palette = (fpacked & 0x80) != 0 ? r.Bytes(3 << ((fpacked & 7) + 1)) : globalPalette;
                    var interlaced = (fpacked & 0x40) != 0;
                    if (palette == null) throw Bad("A GIF frame has no color table.");
                    if (image.Frames.Count >= limits.MaxFrames)
                        throw new WaxKitException(WaxErrorKind.NotSupported, $"The GIF has more than {limits.MaxFrames} frames (MaxFrames).");
                    if ((image.Frames.Count + 1) * frameBytes > limits.MaxDecodedBytes)
                        throw new WaxKitException(WaxErrorKind.NotSupported, $"The GIF needs more than {WaxMediaLimits.Size(limits.MaxDecodedBytes)} decoded (MaxDecodedBytes).");
                    if ((long)fw * fh > (long)limits.MaxWidth * limits.MaxHeight)
                        throw new WaxKitException(WaxErrorKind.NotSupported, $"A GIF frame is {fw} x {fh} pixels, larger than the limits.");

                    var previous = disposal == 3 ? (byte[])canvas.Clone() : null;
                    var minCodeSize = r.U8();
                    var complete = DecodeFrame(r, minCodeSize, fw * fh, out var indices);
                    Draw(canvas, width, height, indices, fx, fy, fw, fh, interlaced, palette, transparent);

                    if (!complete && image.Frames.Count > 0) throw new Truncated();
                    image.Frames.Add((byte[])canvas.Clone());
                    image.DelaysMs.Add(delay < 2 ? 100 : delay * 10);
                    if (!complete) throw new Truncated();
                    if (firstFrameOnly) break;

                    if (disposal == 2) Clear(canvas, width, height, fx, fy, fw, fh);
                    else if (disposal == 3 && previous != null) canvas = previous;
                    delay = 0;
                    disposal = 0;
                    transparent = -1;
                }
            }
            catch (Truncated)
            {
                image.Truncated = true;
            }

            if (image.Frames.Count == 0) throw Bad("The GIF is broken or cut off before its first frame.");
            return image;
        }

        /// <summary>LZW data of one frame into palette indices; false when the data ends early or is broken.</summary>
        static bool DecodeFrame(Reader r, int minCodeSize, int pixelCount, out byte[] indices)
        {
            indices = new byte[pixelCount];
            if (minCodeSize < 2 || minCodeSize > 8)
            {
                r.SkipSubBlocks();
                return false;
            }
            int clear = 1 << minCodeSize, end = clear + 1;
            var prefix = new short[MaxCodes];
            var suffix = new byte[MaxCodes];
            var stack = new byte[MaxCodes + 1];
            int codeSize = minCodeSize + 1, next = end + 1, old = -1, first = 0, written = 0;
            int bits = 0, buffer = 0, blockLeft = 0;
            var complete = false;

            for (var i = 0; i < clear; i++) suffix[i] = (byte)i;
            try
            {
                while (true)
                {
                    while (bits < codeSize)
                    {
                        if (blockLeft == 0)
                        {
                            blockLeft = r.U8();
                            if (blockLeft == 0) return written >= pixelCount; // data ended (no end code)
                        }
                        buffer |= r.U8() << bits;
                        bits += 8;
                        blockLeft--;
                    }
                    var code = buffer & ((1 << codeSize) - 1);
                    buffer >>= codeSize;
                    bits -= codeSize;

                    if (code == clear)
                    {
                        codeSize = minCodeSize + 1;
                        next = end + 1;
                        old = -1;
                        continue;
                    }
                    if (code == end)
                    {
                        complete = written >= pixelCount;
                        break;
                    }

                    int top = 0, current;
                    if (old == -1)
                    {
                        if (code >= clear) break; // broken data
                        stack[top++] = (byte)code;
                        first = code;
                        current = code;
                    }
                    else
                    {
                        if (code > next) break; // broken data
                        current = code;
                        if (code == next)
                        {
                            stack[top++] = (byte)first;
                            code = old;
                        }
                        while (code >= clear)
                        {
                            stack[top++] = suffix[code];
                            code = prefix[code];
                        }
                        first = suffix[code];
                        stack[top++] = (byte)first;
                        if (next < MaxCodes)
                        {
                            prefix[next] = (short)old;
                            suffix[next] = (byte)first;
                            next++;
                            if (next == 1 << codeSize && codeSize < 12) codeSize++;
                        }
                    }
                    old = current;
                    while (top > 0 && written < pixelCount) indices[written++] = stack[--top];
                    top = 0;
                }
                // Skip the rest of this frame's data.
                while (blockLeft > 0) { r.U8(); blockLeft--; }
                r.SkipSubBlocks();
                return complete || written >= pixelCount;
            }
            catch (Truncated)
            {
                return false;
            }
        }

        static void Draw(byte[] canvas, int width, int height, byte[] indices, int fx, int fy, int fw, int fh, bool interlaced,
            byte[] palette, int transparent)
        {
            var colors = palette.Length / 3;
            for (var row = 0; row < fh; row++)
            {
                var y = fy + (interlaced ? InterlacedRow(row, fh) : row);
                if (y < 0 || y >= height) continue;
                for (var col = 0; col < fw; col++)
                {
                    var x = fx + col;
                    if (x >= width) break;
                    int index = indices[row * fw + col];
                    if (index == transparent || index >= colors) continue;
                    var o = (y * width + x) * 4;
                    canvas[o] = palette[index * 3];
                    canvas[o + 1] = palette[index * 3 + 1];
                    canvas[o + 2] = palette[index * 3 + 2];
                    canvas[o + 3] = 255;
                }
            }
        }

        /// <summary>The image row the n-th stored row of an interlaced frame goes to (passes: every 8th from 0, every 8th from 4, every 4th from 2, every 2nd from 1).</summary>
        static int InterlacedRow(int n, int height)
        {
            var pass1 = (height + 7) / 8;
            if (n < pass1) return n * 8;
            n -= pass1;
            var pass2 = (height + 3) / 8;
            if (n < pass2) return 4 + n * 8;
            n -= pass2;
            var pass3 = (height + 1) / 4;
            if (n < pass3) return 2 + n * 4;
            n -= pass3;
            return 1 + n * 2;
        }

        static void Clear(byte[] canvas, int width, int height, int fx, int fy, int fw, int fh)
        {
            for (var y = Math.Max(0, fy); y < Math.Min(height, fy + fh); y++)
                for (var x = Math.Max(0, fx); x < Math.Min(width, fx + fw); x++)
                    Array.Clear(canvas, (y * width + x) * 4, 4);
        }

        static WaxKitException Bad(string message) => new WaxKitException(WaxErrorKind.NotSupported, message);

        sealed class Truncated : Exception
        {
        }

        sealed class Reader
        {
            readonly byte[] data;
            public int Position;

            public Reader(byte[] data, int position)
            {
                this.data = data;
                Position = position;
            }

            public int U8() => Position < data.Length ? data[Position++] : throw new Truncated();

            public int U16() => U8() | U8() << 8;

            public byte[] Bytes(int count)
            {
                if (Position + count > data.Length) throw new Truncated();
                var bytes = new byte[count];
                Buffer.BlockCopy(data, Position, bytes, 0, count);
                Position += count;
                return bytes;
            }

            public string Ascii(int count)
            {
                var bytes = Bytes(count);
                var chars = new char[count];
                for (var i = 0; i < count; i++) chars[i] = (char)bytes[i];
                return new string(chars);
            }

            public void SkipSubBlocks()
            {
                while (true)
                {
                    var size = U8();
                    if (size == 0) return;
                    if (Position + size > data.Length) throw new Truncated();
                    Position += size;
                }
            }
        }
    }
}
