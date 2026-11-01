using System;
using System.IO;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Zlib;

namespace Gulkonster.WaxKit.Crypto
{
    /// <summary>Hashes, AES, deflate and random bytes. All managed (BouncyCastle), so they run on WebGL too.</summary>
    internal static class WaxCrypto
    {
        static readonly SecureRandom Random = new SecureRandom();

        /// <summary>SHA-256.</summary>
        public static byte[] Sha256(byte[] data) => Digest(new Sha256Digest(), data);
        /// <summary>SHA-512.</summary>
        public static byte[] Sha512(byte[] data) => Digest(new Sha512Digest(), data);
        /// <summary>RIPEMD-160.</summary>
        public static byte[] Ripemd160(byte[] data) => Digest(new RipeMD160Digest(), data);

        /// <summary>HMAC-SHA-256.</summary>
        public static byte[] HmacSha256(byte[] key, byte[] data)
        {
            var mac = new HMac(new Sha256Digest());
            mac.Init(new KeyParameter(key));
            mac.BlockUpdate(data, 0, data.Length);
            var result = new byte[mac.GetMacSize()];
            mac.DoFinal(result, 0);
            return result;
        }

        /// <summary>Cryptographically secure random bytes.</summary>
        public static byte[] RandomBytes(int count)
        {
            var result = new byte[count];
            Random.NextBytes(result);
            return result;
        }

        /// <summary>AES-256-CBC with PKCS#7 padding.</summary>
        public static byte[] AesCbc(bool encrypt, byte[] key, byte[] iv, byte[] data)
        {
            var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(new AesEngine()), new Pkcs7Padding());
            cipher.Init(encrypt, new ParametersWithIV(new KeyParameter(key), iv));
            try
            {
                return cipher.DoFinal(data);
            }
            catch (InvalidCipherTextException e)
            {
                throw new FormatException("Could not decrypt the message: " + e.Message);
            }
        }

        /// <summary>Raw deflate (no zlib header) at level 6, the pako.deflateRaw default.</summary>
        public static byte[] DeflateRaw(byte[] data)
        {
            using var output = new MemoryStream();
            using (var deflate = new ZOutputStream(output, 6, true))
            {
                deflate.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        /// <summary>Raw inflate (compressed ESR links).</summary>
        public static byte[] InflateRaw(byte[] data)
        {
            using var input = new ZInputStream(new MemoryStream(data), true);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }

        static byte[] Digest(IDigest digest, byte[] data)
        {
            digest.BlockUpdate(data, 0, data.Length);
            var result = new byte[digest.GetDigestSize()];
            digest.DoFinal(result, 0);
            return result;
        }
    }
}
