using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Gulkonster.WaxKit.Antelope
{
    /// <summary>Antelope account / action names: up to 13 characters packed into a uint64.</summary>
    internal static class AntelopeName
    {
        const string Charmap = ".12345abcdefghijklmnopqrstuvwxyz";

        /// <summary>Account-style name to its 64-bit value.</summary>
        public static ulong Encode(string name)
        {
            if (name == null) throw new FormatException("Name is null.");
            if (name.Length > 13) throw new FormatException($"Invalid name '{name}': longer than 13 characters.");

            ulong value = 0;
            for (var i = 0; i <= 12; i++)
            {
                ulong c = i < name.Length ? Symbol(name, i) : 0UL;
                if (i < 12)
                {
                    c &= 0x1f;
                    c <<= 64 - 5 * (i + 1);
                }
                else
                {
                    if (c > 0x0f) throw new FormatException($"Invalid name '{name}': bad 13th character.");
                    c &= 0x0f;
                }
                value |= c;
            }
            return value;
        }

        /// <summary>A readable valid name from free text: lowercase, other characters become '.', at most 12 characters.</summary>
        public static string FromText(string text)
        {
            var builder = new StringBuilder();
            foreach (var raw in (text ?? "").ToLowerInvariant())
            {
                var c = (raw >= 'a' && raw <= 'z') || (raw >= '1' && raw <= '5') ? raw : '.';
                if (c == '.' && (builder.Length == 0 || builder[builder.Length - 1] == '.')) continue;
                builder.Append(c);
                if (builder.Length == 12) break;
            }
            var name = builder.ToString().Trim('.');
            return name.Length == 0 ? "game" : name;
        }

        /// <summary>64-bit value to the account-style name.</summary>
        public static string Decode(ulong value)
        {
            var chars = new char[13];
            var tmp = value;
            for (var i = 0; i <= 12; i++)
            {
                chars[12 - i] = Charmap[(int)(tmp & (i == 0 ? 0x0fUL : 0x1fUL))];
                tmp >>= i == 0 ? 4 : 5;
            }
            return new string(chars).TrimEnd('.');
        }

        static ulong Symbol(string name, int index)
        {
            var c = name[index];
            if (c >= 'a' && c <= 'z') return (ulong)(c - 'a' + 6);
            if (c >= '1' && c <= '5') return (ulong)(c - '1' + 1);
            if (c == '.') return 0;
            throw new FormatException($"Invalid name '{name}': character '{c}' is not allowed (a-z, 1-5, '.').");
        }
    }

    /// <summary>Token symbol ("4,WAX") and symbol code ("WAX").</summary>
    internal static class AntelopeSymbol
    {
        /// <summary>Token code ("WAX") to its value.</summary>
        public static ulong EncodeCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length > 7) throw new FormatException($"Invalid symbol code '{code}'.");
            ulong value = 0;
            for (var i = 0; i < code.Length; i++)
            {
                var c = code[i];
                if (c < 'A' || c > 'Z') throw new FormatException($"Invalid symbol code '{code}': only A-Z are allowed.");
                value |= (ulong)c << (8 * i);
            }
            return value;
        }

        /// <summary>Value to the token code.</summary>
        public static string DecodeCode(ulong value)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < 8; i++)
            {
                var c = (char)((value >> (8 * i)) & 0xff);
                if (c == 0) break;
                builder.Append(c);
            }
            return builder.ToString();
        }

        /// <summary>Symbol (decimals + code) to its value.</summary>
        public static ulong Encode(int precision, string code)
        {
            if (precision < 0 || precision > 18) throw new FormatException($"Invalid symbol precision {precision}.");
            return (EncodeCode(code) << 8) | (uint)precision;
        }

        /// <summary>"4,WAX" -> raw value.</summary>
        public static ulong Parse(string text)
        {
            var parts = (text ?? "").Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var precision))
                throw new FormatException($"Invalid symbol '{text}', expected 'precision,CODE'.");
            return Encode(precision, parts[1].Trim());
        }

        /// <summary>"8,WAX" form of a symbol.</summary>
        public static string Format(ulong raw) => $"{raw & 0xff},{DecodeCode(raw >> 8)}";
    }

    /// <summary>Asset "1.00000000 WAX": int64 amount + symbol.</summary>
    internal static class AntelopeAsset
    {
        /// <summary>Reads "1.00000000 WAX" into amount and symbol.</summary>
        public static void Parse(string text, out long amount, out ulong symbol)
        {
            var trimmed = (text ?? "").Trim();
            var space = trimmed.IndexOf(' ');
            if (space <= 0) throw new FormatException($"Invalid asset '{text}', expected '1.0000 CODE'.");

            var number = trimmed.Substring(0, space);
            var code = trimmed.Substring(space + 1).Trim();
            var dot = number.IndexOf('.');
            var precision = dot < 0 ? 0 : number.Length - dot - 1;
            var digits = dot < 0 ? number : number.Remove(dot, 1);

            if (!BigInteger.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                || value > long.MaxValue || value < long.MinValue)
                throw new FormatException($"Invalid asset amount in '{text}'.");

            amount = (long)value;
            symbol = AntelopeSymbol.Encode(precision, code);
        }

        /// <summary>Writes "1.00000000 WAX".</summary>
        public static string Format(long amount, ulong symbol)
        {
            var precision = (int)(symbol & 0xff);
            var negative = amount < 0;
            var digits = BigInteger.Abs(new BigInteger(amount)).ToString(CultureInfo.InvariantCulture).PadLeft(precision + 1, '0');
            var number = precision == 0 ? digits : digits.Insert(digits.Length - precision, ".");
            return $"{(negative ? "-" : "")}{number} {AntelopeSymbol.DecodeCode(symbol >> 8)}";
        }
    }

    internal static class AntelopeTime
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly DateTime BlockEpoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Reads a chain time (UTC).</summary>
        public static DateTime Parse(string text)
        {
            var value = (text ?? "").Trim();
            if (!value.EndsWith("Z", StringComparison.Ordinal)) value += "Z";
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }

        /// <summary>Seconds since 1970.</summary>
        public static uint ToSeconds(DateTime utc) => checked((uint)Math.Floor((utc - Epoch).TotalSeconds));
        /// <summary>From seconds since 1970.</summary>
        public static DateTime FromSeconds(uint seconds) => Epoch.AddSeconds(seconds);

        /// <summary>Microseconds since 1970.</summary>
        public static long ToMicroseconds(DateTime utc) => (utc - Epoch).Ticks / 10;
        /// <summary>From microseconds since 1970.</summary>
        public static DateTime FromMicroseconds(long micros) => Epoch.AddTicks(micros * 10);

        /// <summary>Half-second block slot since 2000.</summary>
        public static uint ToBlockSlot(DateTime utc) => checked((uint)Math.Round((utc - BlockEpoch).TotalMilliseconds / 500));
        /// <summary>From a block slot.</summary>
        public static DateTime FromBlockSlot(uint slot) => BlockEpoch.AddMilliseconds(slot * 500.0);

        /// <summary>Chain time text without fractions.</summary>
        public static string FormatSeconds(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        /// <summary>Chain time text with milliseconds.</summary>
        public static string FormatMillis(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    internal static class Hex
    {
        /// <summary>Bytes to lowercase hex.</summary>
        public static string Encode(byte[] data)
        {
            var builder = new StringBuilder(data.Length * 2);
            foreach (var b in data) builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        /// <summary>Hex (any case) to bytes.</summary>
        public static byte[] Decode(string hex)
        {
            hex = hex ?? "";
            if (hex.Length % 2 != 0) throw new FormatException("Hex string has an odd length.");
            var result = new byte[hex.Length / 2];
            for (var i = 0; i < result.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
                    throw new FormatException($"Invalid hex string '{hex}'.");
            }
            return result;
        }
    }

    internal static class Base58
    {
        const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        /// <summary>Bytes to Base58 (Bitcoin alphabet).</summary>
        public static string Encode(byte[] data)
        {
            // Little-endian with a zero sign byte on top, so the value is never negative.
            var value = new BigInteger(Concat(Reverse(data), new byte[] { 0 }));
            var builder = new StringBuilder();
            while (value > 0)
            {
                value = BigInteger.DivRem(value, 58, out var remainder);
                builder.Insert(0, Alphabet[(int)remainder]);
            }
            for (var i = 0; i < data.Length && data[i] == 0; i++) builder.Insert(0, '1');
            return builder.ToString();
        }

        /// <summary>Base58 to bytes.</summary>
        public static byte[] Decode(string text)
        {
            BigInteger value = 0;
            foreach (var c in text)
            {
                var digit = Alphabet.IndexOf(c);
                if (digit < 0) throw new FormatException($"Invalid base58 character '{c}'.");
                value = value * 58 + digit;
            }

            var bytes = value.IsZero ? new byte[0] : Reverse(value.ToByteArray());
            // ToByteArray adds a sign byte when the top bit is set.
            if (bytes.Length > 0 && bytes[0] == 0) bytes = Slice(bytes, 1, bytes.Length - 1);

            var zeros = 0;
            while (zeros < text.Length && text[zeros] == '1') zeros++;
            return Concat(new byte[zeros], bytes);
        }

        static byte[] Reverse(byte[] data)
        {
            var copy = (byte[])data.Clone();
            Array.Reverse(copy);
            return copy;
        }

        internal static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        internal static byte[] Slice(byte[] data, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(data, offset, result, 0, count);
            return result;
        }
    }
}
