using System;
using System.Linq;
using System.Text;
using UnityWaxKit.Crypto;

namespace UnityWaxKit.Antelope
{
    internal enum KeyType : byte
    {
        K1 = 0,
        R1 = 1,
        WA = 2,
    }

    /// <summary>Public key: "PUB_K1_…" (also reads legacy "EOS…"). Binary: type byte + key data.</summary>
    internal sealed class PublicKey
    {
        /// <summary>Curve of the key or signature (K1 = secp256k1).</summary>
        public KeyType Type { get; }
        /// <summary>33 bytes for K1 / R1; WA keys carry extra data.</summary>
        public byte[] Data { get; }

        /// <summary>A key from its type and compressed bytes.</summary>
        public PublicKey(KeyType type, byte[] data)
        {
            Type = type;
            Data = data;
        }

        /// <summary>Reads "PUB_K1_..." or the legacy "EOS..." form.</summary>
        public static PublicKey Parse(string text)
        {
            if (text == null) throw new FormatException("Public key is null.");
            if (text.StartsWith("PUB_", StringComparison.Ordinal))
            {
                var parts = text.Split('_');
                if (parts.Length != 3) throw new FormatException($"Invalid public key '{text}'.");
                var type = ParseType(parts[1]);
                return new PublicKey(type, KeyString.Decode(parts[2], parts[1]));
            }

            // Legacy "EOS" (or any 3-letter chain prefix) + base58(key + ripemd160(key)[0..4]).
            if (text.Length < 50) throw new FormatException($"Invalid public key '{text}'.");
            return new PublicKey(KeyType.K1, KeyString.Decode(text.Substring(3), null));
        }

        /// <summary>Reads the binary form.</summary>
        public static PublicKey Read(AbiReader reader)
        {
            var type = (KeyType)reader.ReadByte();
            if (type != KeyType.WA) return new PublicKey(type, reader.ReadBytes(33));

            var writer = new AbiWriter();
            writer.WriteBytes(reader.ReadBytes(34));
            writer.WriteSizedBytes(reader.ReadSizedBytes());
            return new PublicKey(type, writer.ToArray());
        }

        /// <summary>Writes the binary form.</summary>
        public void Write(AbiWriter writer)
        {
            writer.WriteByte((byte)Type);
            writer.WriteBytes(Data);
        }

        public override string ToString() => $"PUB_{Type}_{KeyString.Encode(Data, Type.ToString())}";

        /// <summary>The legacy "EOS..." form.</summary>
        public string ToLegacyString(string prefix = "EOS")
        {
            if (Type != KeyType.K1) throw new InvalidOperationException("Only K1 keys have a legacy format.");
            return prefix + KeyString.Encode(Data, null);
        }

        /// <summary>Same type and bytes.</summary>
        public bool Equals(PublicKey other) => other != null && other.Type == Type && other.Data.SequenceEqual(Data);

        internal static KeyType ParseType(string type)
        {
            switch (type)
            {
                case "K1": return KeyType.K1;
                case "R1": return KeyType.R1;
                case "WA": return KeyType.WA;
                default: throw new FormatException($"Unknown key type '{type}'.");
            }
        }
    }

    /// <summary>K1 private key: "PVT_K1_…" or legacy WIF. Never logged.</summary>
    internal sealed class PrivateKey
    {
        readonly byte[] data;

        /// <summary>A key from its 32 bytes.</summary>
        public PrivateKey(byte[] data)
        {
            if (data == null || data.Length != 32) throw new FormatException("A K1 private key has 32 bytes.");
            this.data = (byte[])data.Clone();
        }

        /// <summary>A new random key (used only for the temporary Anchor link key).</summary>
        public static PrivateKey Generate() => new PrivateKey(K1.GeneratePrivateKey());

        /// <summary>Reads WIF or "PVT_K1_..." text.</summary>
        public static PrivateKey Parse(string text)
        {
            if (text == null) throw new FormatException("Private key is null.");
            if (text.StartsWith("PVT_", StringComparison.Ordinal))
            {
                var parts = text.Split('_');
                if (parts.Length != 3 || parts[1] != "K1") throw new FormatException("Only PVT_K1 private keys are supported.");
                return new PrivateKey(KeyString.Decode(parts[2], "K1"));
            }

            // WIF: base58(0x80 + key + sha256d(0x80 + key)[0..4]).
            var raw = Base58.Decode(text);
            if (raw.Length != 37 || raw[0] != 0x80) throw new FormatException("Invalid WIF private key.");
            var body = Base58.Slice(raw, 0, 33);
            var check = WaxCrypto.Sha256(WaxCrypto.Sha256(body));
            for (var i = 0; i < 4; i++)
                if (check[i] != raw[33 + i]) throw new FormatException("Invalid WIF private key checksum.");
            return new PrivateKey(Base58.Slice(raw, 1, 32));
        }

        /// <summary>The matching public key.</summary>
        public PublicKey ToPublic() => new PublicKey(KeyType.K1, K1.GetPublicKey(data));

        /// <summary>Signs a 32-byte digest.</summary>
        public Signature SignDigest(byte[] digest) => new Signature(KeyType.K1, K1.Sign(data, digest));

        /// <summary>ECDH shared secret with another key (for sealed messages).</summary>
        public byte[] SharedSecret(PublicKey publicKey)
        {
            if (publicKey.Type != KeyType.K1) throw new NotSupportedException("Shared secrets need a K1 public key.");
            return K1.SharedSecret(data, publicKey.Data);
        }

        /// <summary>The "PVT_K1_..." form. Secret: only for the saved link key, never for account keys.</summary>
        public string ToSecretString() => "PVT_K1_" + KeyString.Encode(data, "K1");

        public override string ToString() => "PVT_K1_<hidden>";
    }

    /// <summary>Signature: "SIG_K1_…". Binary: type byte + data.</summary>
    internal sealed class Signature
    {
        /// <summary>Curve of the key or signature (K1 = secp256k1).</summary>
        public KeyType Type { get; }
        /// <summary>Raw bytes.</summary>
        public byte[] Data { get; }

        /// <summary>A signature from its type and 65 bytes.</summary>
        public Signature(KeyType type, byte[] data)
        {
            Type = type;
            Data = data;
        }

        /// <summary>Reads "SIG_K1_..." text.</summary>
        public static Signature Parse(string text)
        {
            var parts = (text ?? "").Split('_');
            if (parts.Length != 3 || parts[0] != "SIG") throw new FormatException($"Invalid signature '{text}'.");
            return new Signature(PublicKey.ParseType(parts[1]), KeyString.Decode(parts[2], parts[1]));
        }

        /// <summary>Reads the binary form.</summary>
        public static Signature Read(AbiReader reader)
        {
            var type = (KeyType)reader.ReadByte();
            if (type != KeyType.WA) return new Signature(type, reader.ReadBytes(65));

            var writer = new AbiWriter();
            writer.WriteBytes(reader.ReadBytes(65));
            writer.WriteSizedBytes(reader.ReadSizedBytes());
            writer.WriteSizedBytes(reader.ReadSizedBytes());
            return new Signature(type, writer.ToArray());
        }

        /// <summary>Writes the binary form.</summary>
        public void Write(AbiWriter writer)
        {
            writer.WriteByte((byte)Type);
            writer.WriteBytes(Data);
        }

        /// <summary>The public key that signed <paramref name="digest"/>.</summary>
        public PublicKey RecoverDigest(byte[] digest)
        {
            if (Type != KeyType.K1) throw new NotSupportedException("Only K1 signatures can be recovered.");
            return new PublicKey(KeyType.K1, K1.Recover(Data, digest));
        }

        public override string ToString() => $"SIG_{Type}_{KeyString.Encode(Data, Type.ToString())}";
    }

    /// <summary>Base58 with a RIPEMD-160 checksum, suffixed with the key type for the new formats.</summary>
    internal static class KeyString
    {
        /// <summary>Base58 with a RIPEMD-160 checksum (the key text format).</summary>
        public static string Encode(byte[] data, string suffix) => Base58.Encode(Base58.Concat(data, Checksum(data, suffix)));

        /// <summary>Reads that format and checks the checksum.</summary>
        public static byte[] Decode(string text, string suffix)
        {
            var raw = Base58.Decode(text);
            if (raw.Length < 5) throw new FormatException("Key string is too short.");
            var data = Base58.Slice(raw, 0, raw.Length - 4);
            var expected = Checksum(data, suffix);
            for (var i = 0; i < 4; i++)
                if (raw[data.Length + i] != expected[i]) throw new FormatException("Key checksum does not match.");
            return data;
        }

        static byte[] Checksum(byte[] data, string suffix)
        {
            var input = suffix == null ? data : Base58.Concat(data, Encoding.UTF8.GetBytes(suffix));
            return Base58.Slice(WaxCrypto.Ripemd160(input), 0, 4);
        }
    }
}
