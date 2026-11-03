using System;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace UnityWaxKit.Crypto
{
    /// <summary>
    /// secp256k1 for Antelope K1 keys. Signing matches @wharfkit/antelope (elliptic): RFC 6979 HMAC-DRBG nonces
    /// with the attempt number as personalization, low S, retried until the signature is Antelope-canonical.
    /// </summary>
    internal static class K1
    {
        static readonly X9ECParameters Curve = CustomNamedCurves.GetByName("secp256k1");
        static readonly BigInteger N = Curve.N;
        static readonly BigInteger HalfN = N.ShiftRight(1);

        /// <summary>A new random secp256k1 private key.</summary>
        public static byte[] GeneratePrivateKey()
        {
            while (true)
            {
                var candidate = WaxCrypto.RandomBytes(32);
                var d = new BigInteger(1, candidate);
                if (d.SignValue > 0 && d.CompareTo(N) < 0) return candidate;
            }
        }

        /// <summary>Compressed 33-byte public key.</summary>
        public static byte[] GetPublicKey(byte[] privateKey) =>
            Curve.G.Multiply(ToScalar(privateKey)).Normalize().GetEncoded(true);

        /// <summary>SHA-512 of the ECDH x coordinate (big-endian, no leading zeros), as PrivateKey.sharedSecret.</summary>
        public static byte[] SharedSecret(byte[] privateKey, byte[] publicKey)
        {
            var point = DecodePoint(publicKey).Multiply(ToScalar(privateKey)).Normalize();
            return WaxCrypto.Sha512(point.AffineXCoord.ToBigInteger().ToByteArrayUnsigned());
        }

        /// <summary>65 bytes: header (recovery id + 31), r, s.</summary>
        public static byte[] Sign(byte[] privateKey, byte[] digest)
        {
            if (digest.Length != 32) throw new ArgumentException("Digest must be 32 bytes.", nameof(digest));

            var d = ToScalar(privateKey);
            var e = new BigInteger(1, digest);
            if (e.CompareTo(N) >= 0) e = e.Subtract(N);

            for (var attempt = 1; ; attempt++)
            {
                var signature = SignOnce(d, e, privateKey, digest, (byte)attempt);
                if (IsCanonical(signature)) return signature;
            }
        }

        /// <summary>The compressed public key that made the signature.</summary>
        public static byte[] Recover(byte[] signature, byte[] digest)
        {
            if (signature.Length != 65) throw new FormatException("K1 signature must be 65 bytes.");
            var recid = (signature[0] - 27) & 3;
            var r = new BigInteger(1, signature, 1, 32);
            var s = new BigInteger(1, signature, 33, 32);
            var e = new BigInteger(1, digest);

            var x = (recid & 2) != 0 ? r.Add(N) : r;
            var compressed = new byte[33];
            compressed[0] = (byte)((recid & 1) != 0 ? 0x03 : 0x02);
            var xBytes = x.ToByteArrayUnsigned();
            if (xBytes.Length > 32) throw new FormatException("Invalid signature.");
            Buffer.BlockCopy(xBytes, 0, compressed, 33 - xBytes.Length, xBytes.Length);

            var point = DecodePoint(compressed);
            var rInv = r.ModInverse(N);
            var eNeg = N.Subtract(e).Mod(N);
            var q = ECAlgorithms.SumOfTwoMultiplies(Curve.G, eNeg.Multiply(rInv).Mod(N), point, s.Multiply(rInv).Mod(N));
            return q.Normalize().GetEncoded(true);
        }

        static byte[] SignOnce(BigInteger d, BigInteger e, byte[] privateKey, byte[] digest, byte attempt)
        {
            var drbg = new HmacDrbg(Pad32(privateKey), Pad32(e.ToByteArrayUnsigned()), new[] { attempt });
            var nMinus1 = N.Subtract(BigInteger.One);

            while (true)
            {
                var k = new BigInteger(1, drbg.Generate(32));
                if (k.CompareTo(BigInteger.One) <= 0 || k.CompareTo(nMinus1) >= 0) continue;

                var kp = Curve.G.Multiply(k).Normalize();
                if (kp.IsInfinity) continue;

                var kpX = kp.AffineXCoord.ToBigInteger();
                var r = kpX.Mod(N);
                if (r.SignValue == 0) continue;

                var s = k.ModInverse(N).Multiply(r.Multiply(d).Add(e)).Mod(N);
                if (s.SignValue == 0) continue;

                var recid = (kpX.CompareTo(r) != 0 ? 2 : 0) | (kp.AffineYCoord.ToBigInteger().TestBit(0) ? 1 : 0);
                if (s.CompareTo(HalfN) > 0)
                {
                    s = N.Subtract(s);
                    recid ^= 1;
                }

                var result = new byte[65];
                result[0] = (byte)(recid + 31);
                Buffer.BlockCopy(Pad32(r.ToByteArrayUnsigned()), 0, result, 1, 32);
                Buffer.BlockCopy(Pad32(s.ToByteArrayUnsigned()), 0, result, 33, 32);
                return result;
            }
        }

        // https://github.com/EOSIO/eos/issues/6699
        static bool IsCanonical(byte[] sig) =>
            (sig[1] & 0x80) == 0 && !(sig[1] == 0 && (sig[2] & 0x80) == 0) &&
            (sig[33] & 0x80) == 0 && !(sig[33] == 0 && (sig[34] & 0x80) == 0);

        static BigInteger ToScalar(byte[] privateKey)
        {
            var d = new BigInteger(1, privateKey);
            if (d.SignValue <= 0 || d.CompareTo(N) >= 0) throw new FormatException("Invalid private key.");
            return d;
        }

        static ECPoint DecodePoint(byte[] publicKey)
        {
            try
            {
                return Curve.Curve.DecodePoint(publicKey);
            }
            catch (ArgumentException e)
            {
                throw new FormatException("Invalid public key: " + e.Message);
            }
        }

        static byte[] Pad32(byte[] value)
        {
            if (value.Length == 32) return value;
            if (value.Length > 32) throw new FormatException("Value is longer than 32 bytes.");
            var result = new byte[32];
            Buffer.BlockCopy(value, 0, result, 32 - value.Length, value.Length);
            return result;
        }

        /// <summary>HMAC-SHA256 DRBG (NIST SP 800-90A, no reseed), as the hmac-drbg package used by elliptic.</summary>
        sealed class HmacDrbg
        {
            byte[] k = new byte[32];
            byte[] v = new byte[32];

            /// <summary>Deterministic random generator (RFC 6979) for signature nonces.</summary>
            public HmacDrbg(byte[] entropy, byte[] nonce, byte[] personalization)
            {
                for (var i = 0; i < v.Length; i++) v[i] = 1;
                Update(Concat(entropy, nonce, personalization));
            }

            /// <summary>The next <paramref name="length"/> bytes.</summary>
            public byte[] Generate(int length)
            {
                var result = new byte[length];
                var filled = 0;
                while (filled < length)
                {
                    v = WaxCrypto.HmacSha256(k, v);
                    var count = Math.Min(v.Length, length - filled);
                    Buffer.BlockCopy(v, 0, result, filled, count);
                    filled += count;
                }
                Update(null);
                return result;
            }

            void Update(byte[] seed)
            {
                k = WaxCrypto.HmacSha256(k, Concat(v, new byte[] { 0 }, seed ?? new byte[0]));
                v = WaxCrypto.HmacSha256(k, v);
                if (seed == null || seed.Length == 0) return;
                k = WaxCrypto.HmacSha256(k, Concat(v, new byte[] { 1 }, seed));
                v = WaxCrypto.HmacSha256(k, v);
            }

            static byte[] Concat(params byte[][] parts)
            {
                var length = 0;
                foreach (var part in parts) length += part.Length;
                var result = new byte[length];
                var offset = 0;
                foreach (var part in parts)
                {
                    Buffer.BlockCopy(part, 0, result, offset, part.Length);
                    offset += part.Length;
                }
                return result;
            }
        }
    }
}
