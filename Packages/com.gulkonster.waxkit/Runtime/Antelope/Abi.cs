using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit.Antelope
{
    /// <summary>A contract ABI (the parts serialization needs).</summary>
    internal sealed class Abi
    {
        internal sealed class Field
        {
            /// <summary>Field or struct name.</summary>
            public string Name;
            /// <summary>Field type, for example "name", "asset", "uint64[]", "string?".</summary>
            public string Type;
        }

        internal sealed class Struct
        {
            /// <summary>Field or struct name.</summary>
            public string Name;
            /// <summary>Base struct whose fields come first, or "".</summary>
            public string Base;
            /// <summary>Fields in order.</summary>
            public List<Field> Fields = new List<Field>();
        }

        readonly Dictionary<string, string> typedefs = new Dictionary<string, string>();
        readonly Dictionary<string, Struct> structs = new Dictionary<string, Struct>();
        readonly Dictionary<string, List<string>> variants = new Dictionary<string, List<string>>();
        readonly Dictionary<string, string> actions = new Dictionary<string, string>();

        /// <summary>Reads a contract ABI (the abi object of /v1/chain/get_abi).</summary>
        public static Abi FromJson(JObject json)
        {
            var abi = new Abi();
            foreach (var type in json["types"] as JArray ?? new JArray())
                abi.typedefs[(string)type["new_type_name"]] = (string)type["type"];

            foreach (var s in json["structs"] as JArray ?? new JArray())
            {
                var definition = new Struct { Name = (string)s["name"], Base = (string)s["base"] ?? "" };
                foreach (var f in s["fields"] as JArray ?? new JArray())
                    definition.Fields.Add(new Field { Name = (string)f["name"], Type = (string)f["type"] });
                abi.structs[definition.Name] = definition;
            }

            foreach (var v in json["variants"] as JArray ?? new JArray())
                abi.variants[(string)v["name"]] = v["types"].Select(t => (string)t).ToList();

            foreach (var a in json["actions"] as JArray ?? new JArray())
                abi.actions[(string)a["name"]] = (string)a["type"];

            return abi;
        }

        /// <summary>The data type of an action, or null.</summary>
        public string ActionType(string action) => actions.TryGetValue(action, out var type) ? type : null;

        /// <summary>Serializes a JSON value of <paramref name="type"/> to the binary form the chain expects.</summary>
        public byte[] Encode(string type, JToken value)
        {
            var writer = new AbiWriter();
            Write(writer, type, value, type);
            return writer.ToArray();
        }

        /// <summary>Reads binary data of <paramref name="type"/> back to JSON.</summary>
        public JToken Decode(string type, byte[] data)
        {
            var reader = new AbiReader(data);
            var result = Read(reader, type);
            if (reader.CanRead) throw new FormatException($"{data.Length - reader.Position} unread bytes after '{type}'.");
            return result;
        }

        internal void Write(AbiWriter writer, string type, JToken value, string path)
        {
            if (type.EndsWith("$", StringComparison.Ordinal)) type = type.Substring(0, type.Length - 1);

            if (type.EndsWith("?", StringComparison.Ordinal))
            {
                if (IsNull(value))
                {
                    writer.WriteByte(0);
                    return;
                }
                writer.WriteByte(1);
                Write(writer, type.Substring(0, type.Length - 1), value, path);
                return;
            }

            if (type.EndsWith("[]", StringComparison.Ordinal))
            {
                if (!(value is JArray array)) throw Error(path, $"expected an array for '{type}'");
                writer.WriteVarUInt32((uint)array.Count);
                var element = type.Substring(0, type.Length - 2);
                for (var i = 0; i < array.Count; i++) Write(writer, element, array[i], $"{path}[{i}]");
                return;
            }

            if (typedefs.TryGetValue(type, out var alias))
            {
                Write(writer, alias, value, path);
                return;
            }

            if (Builtins.TryWrite(writer, type, value, path)) return;

            if (structs.TryGetValue(type, out var definition))
            {
                WriteStruct(writer, definition, value, path);
                return;
            }

            if (variants.TryGetValue(type, out var options))
            {
                WriteVariant(writer, type, options, value, path);
                return;
            }

            throw Error(path, $"unknown type '{type}'");
        }

        void WriteStruct(AbiWriter writer, Struct definition, JToken value, string path)
        {
            if (!(value is JObject obj)) throw Error(path, $"expected an object for struct '{definition.Name}'");

            if (!string.IsNullOrEmpty(definition.Base))
            {
                if (!structs.TryGetValue(ResolveAlias(definition.Base), out var baseStruct)) throw Error(path, $"unknown base '{definition.Base}'");
                WriteStruct(writer, baseStruct, value, path);
            }

            var extensionMissing = false;
            foreach (var field in definition.Fields)
            {
                var fieldValue = FindField(obj, field.Name);
                var isExtension = field.Type.EndsWith("$", StringComparison.Ordinal);
                var fieldPath = $"{path}.{field.Name}";

                if (isExtension)
                {
                    if (IsNull(fieldValue))
                    {
                        extensionMissing = true;
                        continue;
                    }
                    if (extensionMissing) throw Error(fieldPath, "binary extensions must be set in order");
                }
                else if (fieldValue == null && !field.Type.EndsWith("?", StringComparison.Ordinal))
                {
                    throw Error(fieldPath, $"missing field '{field.Name}' of type '{field.Type}'");
                }

                Write(writer, field.Type, fieldValue, fieldPath);
            }
        }

        void WriteVariant(AbiWriter writer, string type, List<string> options, JToken value, string path)
        {
            string name;
            JToken inner;
            if (value is JArray pair && pair.Count == 2 && pair[0].Type == JTokenType.String)
            {
                name = (string)pair[0];
                inner = pair[1];
            }
            else if (value is JObject obj && obj["type"] != null)
            {
                name = (string)obj["type"];
                inner = obj["value"];
            }
            else
            {
                throw Error(path, $"variant '{type}' needs [\"type\", value]");
            }

            var index = options.IndexOf(name);
            if (index < 0) throw Error(path, $"'{name}' is not a type of variant '{type}'");
            writer.WriteVarUInt32((uint)index);
            Write(writer, name, inner, path);
        }

        internal JToken Read(AbiReader reader, string type)
        {
            if (type.EndsWith("$", StringComparison.Ordinal)) type = type.Substring(0, type.Length - 1);

            if (type.EndsWith("?", StringComparison.Ordinal))
                return reader.ReadByte() == 0 ? JValue.CreateNull() : Read(reader, type.Substring(0, type.Length - 1));

            if (type.EndsWith("[]", StringComparison.Ordinal))
            {
                var count = reader.ReadVarUInt32();
                var array = new JArray();
                var element = type.Substring(0, type.Length - 2);
                for (var i = 0; i < count; i++) array.Add(Read(reader, element));
                return array;
            }

            if (typedefs.TryGetValue(type, out var alias)) return Read(reader, alias);

            if (Builtins.TryRead(reader, type, out var builtin)) return builtin;

            if (structs.TryGetValue(type, out var definition))
            {
                var obj = new JObject();
                ReadStruct(reader, definition, obj);
                return obj;
            }

            if (variants.TryGetValue(type, out var options))
            {
                var index = (int)reader.ReadVarUInt32();
                if (index >= options.Count) throw new FormatException($"Variant index {index} out of range for '{type}'.");
                return new JArray(options[index], Read(reader, options[index]));
            }

            throw new FormatException($"Unknown type '{type}'.");
        }

        void ReadStruct(AbiReader reader, Struct definition, JObject obj)
        {
            if (!string.IsNullOrEmpty(definition.Base)) ReadStruct(reader, structs[ResolveAlias(definition.Base)], obj);

            foreach (var field in definition.Fields)
            {
                if (field.Type.EndsWith("$", StringComparison.Ordinal) && !reader.CanRead)
                {
                    obj[field.Name] = JValue.CreateNull();
                    continue;
                }
                obj[field.Name] = Read(reader, field.Type);
            }
        }

        string ResolveAlias(string type)
        {
            while (typedefs.TryGetValue(type, out var alias)) type = alias;
            return type;
        }

        static JToken FindField(JObject obj, string name)
        {
            var exact = obj[name];
            if (exact != null) return exact;
            // C# objects often use PascalCase properties.
            var property = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, name.Replace("_", ""), StringComparison.OrdinalIgnoreCase)
                                                                 || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            return property?.Value;
        }

        static bool IsNull(JToken value) => value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined;

        static FormatException Error(string path, string message) => new FormatException($"Cannot serialize '{path}': {message}.");

        /// <summary>The built-in Antelope types.</summary>
        static class Builtins
        {
            static readonly BigInteger UInt128Max = (BigInteger.One << 128) - 1;
            static readonly BigInteger Int128Min = -(BigInteger.One << 127);
            static readonly BigInteger Int128Max = (BigInteger.One << 127) - 1;

            /// <summary>Writes a built-in type (name, asset, int, ...); false when the type is not built in.</summary>
            public static bool TryWrite(AbiWriter w, string type, JToken v, string path)
            {
                try
                {
                    switch (type)
                    {
                        case "bool": w.WriteByte(ToBool(v) ? (byte)1 : (byte)0); return true;
                        case "int8": w.WriteByte((byte)checked((sbyte)ToInteger(v))); return true;
                        case "uint8": w.WriteByte(checked((byte)ToInteger(v))); return true;
                        case "int16": w.WriteUInt16((ushort)checked((short)ToInteger(v))); return true;
                        case "uint16": w.WriteUInt16(checked((ushort)ToInteger(v))); return true;
                        case "int32": w.WriteUInt32((uint)checked((int)ToInteger(v))); return true;
                        case "uint32": w.WriteUInt32(checked((uint)ToInteger(v))); return true;
                        case "int64": w.WriteUInt64((ulong)checked((long)ToInteger(v))); return true;
                        case "uint64": w.WriteUInt64(checked((ulong)ToInteger(v))); return true;
                        case "int128": WriteInt128(w, ToInteger(v), true); return true;
                        case "uint128": WriteInt128(w, ToInteger(v), false); return true;
                        case "varint32": w.WriteVarInt32(checked((int)ToInteger(v))); return true;
                        case "varuint32": w.WriteVarUInt32(checked((uint)ToInteger(v))); return true;
                        case "float32": w.WriteBytes(BitConverterLE(BitConverter.GetBytes((float)ToDouble(v)))); return true;
                        case "float64": w.WriteBytes(BitConverterLE(BitConverter.GetBytes(ToDouble(v)))); return true;
                        case "float128": w.WriteBytes(FixedHex(v, 16)); return true;
                        case "time_point": w.WriteUInt64((ulong)AntelopeTime.ToMicroseconds(ToTime(v))); return true;
                        case "time_point_sec": w.WriteUInt32(AntelopeTime.ToSeconds(ToTime(v))); return true;
                        case "block_timestamp_type": w.WriteUInt32(AntelopeTime.ToBlockSlot(ToTime(v))); return true;
                        case "name": w.WriteUInt64(AntelopeName.Encode(ToStr(v))); return true;
                        case "bytes": w.WriteSizedBytes(Hex.Decode(ToStr(v))); return true;
                        case "string": w.WriteString(ToStr(v)); return true;
                        case "checksum160": w.WriteBytes(FixedHex(v, 20)); return true;
                        case "checksum256": w.WriteBytes(FixedHex(v, 32)); return true;
                        case "checksum512": w.WriteBytes(FixedHex(v, 64)); return true;
                        case "public_key": PublicKey.Parse(ToStr(v)).Write(w); return true;
                        case "signature": Signature.Parse(ToStr(v)).Write(w); return true;
                        case "symbol": w.WriteUInt64(AntelopeSymbol.Parse(ToStr(v))); return true;
                        case "symbol_code": w.WriteUInt64(AntelopeSymbol.EncodeCode(ToStr(v))); return true;
                        case "asset":
                            AntelopeAsset.Parse(ToStr(v), out var amount, out var symbol);
                            w.WriteUInt64((ulong)amount);
                            w.WriteUInt64(symbol);
                            return true;
                        case "extended_asset":
                            if (!(v is JObject ext)) throw new FormatException("expected { quantity, contract }");
                            TryWrite(w, "asset", ext["quantity"], path);
                            TryWrite(w, "name", ext["contract"], path);
                            return true;
                        default:
                            return false;
                    }
                }
                catch (Exception e) when (e is FormatException || e is OverflowException || e is InvalidCastException || e is ArgumentException)
                {
                    if (e.Message.StartsWith("Cannot serialize", StringComparison.Ordinal)) throw;
                    throw Error(path, $"invalid {type} value '{v}' ({e.Message.TrimEnd('.')})");
                }
            }

            /// <summary>Reads a built-in type; false when the type is not built in.</summary>
            public static bool TryRead(AbiReader r, string type, out JToken value)
            {
                switch (type)
                {
                    case "bool": value = r.ReadByte() != 0; return true;
                    case "int8": value = (sbyte)r.ReadByte(); return true;
                    case "uint8": value = r.ReadByte(); return true;
                    case "int16": value = (short)r.ReadUInt16(); return true;
                    case "uint16": value = r.ReadUInt16(); return true;
                    case "int32": value = (int)r.ReadUInt32(); return true;
                    case "uint32": value = r.ReadUInt32(); return true;
                    case "int64": value = Wide((long)r.ReadUInt64()); return true;
                    case "uint64": value = Wide(r.ReadUInt64()); return true;
                    case "int128": value = Wide(ReadInt128(r, true)); return true;
                    case "uint128": value = Wide(ReadInt128(r, false)); return true;
                    case "varint32": value = r.ReadVarInt32(); return true;
                    case "varuint32": value = r.ReadVarUInt32(); return true;
                    case "float32": value = BitConverter.ToSingle(BitConverterLE(r.ReadBytes(4)), 0); return true;
                    case "float64": value = BitConverter.ToDouble(BitConverterLE(r.ReadBytes(8)), 0); return true;
                    case "float128": value = Hex.Encode(r.ReadBytes(16)); return true;
                    case "time_point": value = AntelopeTime.FormatMillis(AntelopeTime.FromMicroseconds((long)r.ReadUInt64())); return true;
                    case "time_point_sec": value = AntelopeTime.FormatSeconds(AntelopeTime.FromSeconds(r.ReadUInt32())); return true;
                    case "block_timestamp_type": value = AntelopeTime.FormatMillis(AntelopeTime.FromBlockSlot(r.ReadUInt32())); return true;
                    case "name": value = AntelopeName.Decode(r.ReadUInt64()); return true;
                    case "bytes": value = Hex.Encode(r.ReadSizedBytes()); return true;
                    case "string": value = r.ReadString(); return true;
                    case "checksum160": value = Hex.Encode(r.ReadBytes(20)); return true;
                    case "checksum256": value = Hex.Encode(r.ReadBytes(32)); return true;
                    case "checksum512": value = Hex.Encode(r.ReadBytes(64)); return true;
                    case "public_key": value = PublicKey.Read(r).ToString(); return true;
                    case "signature": value = Signature.Read(r).ToString(); return true;
                    case "symbol": value = AntelopeSymbol.Format(r.ReadUInt64()); return true;
                    case "symbol_code": value = AntelopeSymbol.DecodeCode(r.ReadUInt64()); return true;
                    case "asset":
                        var amount = (long)r.ReadUInt64();
                        value = AntelopeAsset.Format(amount, r.ReadUInt64());
                        return true;
                    case "extended_asset":
                        TryRead(r, "asset", out var quantity);
                        value = new JObject { ["quantity"] = quantity, ["contract"] = AntelopeName.Decode(r.ReadUInt64()) };
                        return true;
                    default:
                        value = null;
                        return false;
                }
            }

            // 64- and 128-bit integers are JSON strings unless they fit in 32 bits (as @wharfkit/antelope).
            static JToken Wide(BigInteger value) =>
                BigInteger.Abs(value) <= uint.MaxValue ? (JToken)(long)value : value.ToString(CultureInfo.InvariantCulture);

            static void WriteInt128(AbiWriter w, BigInteger value, bool signed)
            {
                if (signed ? value < Int128Min || value > Int128Max : value.Sign < 0 || value > UInt128Max)
                    throw new OverflowException("out of range");
                var bytes = (signed && value.Sign < 0 ? value + (BigInteger.One << 128) : value).ToByteArray();
                var result = new byte[16];
                Buffer.BlockCopy(bytes, 0, result, 0, Math.Min(16, bytes.Length));
                w.WriteBytes(result);
            }

            static BigInteger ReadInt128(AbiReader r, bool signed)
            {
                var bytes = r.ReadBytes(16);
                var value = new BigInteger(Base58.Concat(bytes, new byte[] { 0 }));
                if (signed && (bytes[15] & 0x80) != 0) value -= BigInteger.One << 128;
                return value;
            }

            static BigInteger ToInteger(JToken v)
            {
                switch (v?.Type)
                {
                    case JTokenType.Integer:
                        var raw = ((JValue)v).Value;
                        return raw is BigInteger big ? big : raw is ulong u ? new BigInteger(u) : new BigInteger(Convert.ToInt64(raw, CultureInfo.InvariantCulture));
                    case JTokenType.Float:
                        var d = (double)v;
                        if (Math.Floor(d) != d) throw new FormatException("not an integer");
                        return new BigInteger(d);
                    case JTokenType.Boolean: return (bool)v ? 1 : 0;
                    case JTokenType.String:
                        if (BigInteger.TryParse((string)v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                        throw new FormatException("not an integer");
                    default: throw new FormatException("expected a number");
                }
            }

            static double ToDouble(JToken v)
            {
                if (v?.Type == JTokenType.String) return double.Parse((string)v, CultureInfo.InvariantCulture);
                if (v?.Type == JTokenType.Integer || v?.Type == JTokenType.Float) return (double)v;
                throw new FormatException("expected a number");
            }

            static bool ToBool(JToken v)
            {
                if (v?.Type == JTokenType.Boolean) return (bool)v;
                if (v?.Type == JTokenType.Integer) return (long)v != 0;
                if (v?.Type == JTokenType.String && bool.TryParse((string)v, out var b)) return b;
                throw new FormatException("expected true or false");
            }

            static string ToStr(JToken v)
            {
                if (v == null || v.Type == JTokenType.Null) throw new FormatException("expected a string");
                return v.Type == JTokenType.String ? (string)v : v.ToString();
            }

            static DateTime ToTime(JToken v) =>
                v?.Type == JTokenType.Date ? ((DateTime)v).ToUniversalTime() : AntelopeTime.Parse(ToStr(v));

            static byte[] FixedHex(JToken v, int length)
            {
                var bytes = Hex.Decode(ToStr(v));
                if (bytes.Length != length) throw new FormatException($"expected {length} bytes");
                return bytes;
            }

            static byte[] BitConverterLE(byte[] bytes)
            {
                if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
                return bytes;
            }
        }
    }
}
