using System;
using System.IO;
using System.Text;

namespace Gulkonster.WaxKit.Antelope
{
    /// <summary>Little-endian Antelope binary writer.</summary>
    internal sealed class AbiWriter
    {
        readonly MemoryStream stream = new MemoryStream();

        /// <summary>Writes one byte.</summary>
        public void WriteByte(byte value) => stream.WriteByte(value);
        /// <summary>Writes bytes as they are.</summary>
        public void WriteBytes(byte[] value) => stream.Write(value, 0, value.Length);

        /// <summary>Little-endian 16-bit.</summary>
        public void WriteUInt16(ushort value)
        {
            WriteByte((byte)value);
            WriteByte((byte)(value >> 8));
        }

        /// <summary>Little-endian 32-bit.</summary>
        public void WriteUInt32(uint value)
        {
            for (var i = 0; i < 4; i++) WriteByte((byte)(value >> (8 * i)));
        }

        /// <summary>Little-endian 64-bit.</summary>
        public void WriteUInt64(ulong value)
        {
            for (var i = 0; i < 8; i++) WriteByte((byte)(value >> (8 * i)));
        }

        /// <summary>Variable-length unsigned integer (7 bits per byte), used for lengths.</summary>
        public void WriteVarUInt32(uint value)
        {
            do
            {
                var b = (byte)(value & 0x7f);
                value >>= 7;
                if (value != 0) b |= 0x80;
                WriteByte(b);
            } while (value != 0);
        }

        /// <summary>Variable-length signed integer (zigzag).</summary>
        public void WriteVarInt32(int value) => WriteVarUInt32((uint)((value << 1) ^ (value >> 31)));

        /// <summary>Length prefix, then the bytes.</summary>
        public void WriteSizedBytes(byte[] value)
        {
            WriteVarUInt32((uint)value.Length);
            WriteBytes(value);
        }

        /// <summary>UTF-8 string with a length prefix.</summary>
        public void WriteString(string value) => WriteSizedBytes(Encoding.UTF8.GetBytes(value ?? ""));

        /// <summary>Everything written so far.</summary>
        public byte[] ToArray() => stream.ToArray();
    }

    /// <summary>Little-endian Antelope binary reader.</summary>
    internal sealed class AbiReader
    {
        readonly byte[] data;
        int position;

        /// <summary>Reads from <paramref name="data"/>.</summary>
        public AbiReader(byte[] data) => this.data = data ?? throw new ArgumentNullException(nameof(data));

        /// <summary>True while bytes are left.</summary>
        public bool CanRead => position < data.Length;
        /// <summary>Bytes read so far.</summary>
        public int Position => position;

        /// <summary>Reads one byte (throws at the end).</summary>
        public byte ReadByte()
        {
            if (position >= data.Length) throw new FormatException("Unexpected end of data.");
            return data[position++];
        }

        /// <summary>Reads <paramref name="count"/> bytes.</summary>
        public byte[] ReadBytes(int count)
        {
            if (count < 0 || position + count > data.Length) throw new FormatException("Unexpected end of data.");
            var result = new byte[count];
            Buffer.BlockCopy(data, position, result, 0, count);
            position += count;
            return result;
        }

        /// <summary>Little-endian 16-bit.</summary>
        public ushort ReadUInt16() => (ushort)(ReadByte() | (ReadByte() << 8));

        /// <summary>Little-endian 32-bit.</summary>
        public uint ReadUInt32()
        {
            uint value = 0;
            for (var i = 0; i < 4; i++) value |= (uint)ReadByte() << (8 * i);
            return value;
        }

        /// <summary>Little-endian 64-bit.</summary>
        public ulong ReadUInt64()
        {
            ulong value = 0;
            for (var i = 0; i < 8; i++) value |= (ulong)ReadByte() << (8 * i);
            return value;
        }

        /// <summary>Variable-length unsigned integer.</summary>
        public uint ReadVarUInt32()
        {
            uint value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = ReadByte();
                value |= (uint)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return value;
            }
            throw new FormatException("varuint32 is too long.");
        }

        /// <summary>Variable-length signed integer (zigzag).</summary>
        public int ReadVarInt32()
        {
            var raw = ReadVarUInt32();
            return (int)(raw >> 1) ^ -(int)(raw & 1);
        }

        /// <summary>Length prefix, then the bytes.</summary>
        public byte[] ReadSizedBytes() => ReadBytes((int)ReadVarUInt32());

        /// <summary>UTF-8 string with a length prefix.</summary>
        public string ReadString() => Encoding.UTF8.GetString(ReadSizedBytes());

        /// <summary>All bytes that are left.</summary>
        public byte[] ReadRemaining() => ReadBytes(data.Length - position);
    }
}
