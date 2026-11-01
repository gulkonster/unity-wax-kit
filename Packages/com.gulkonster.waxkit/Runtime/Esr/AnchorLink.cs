using System;
using System.Linq;
using System.Text;
using Gulkonster.WaxKit.Antelope;
using Gulkonster.WaxKit.Crypto;
using Newtonsoft.Json.Linq;

namespace Gulkonster.WaxKit.Esr
{
    /// <summary>Encrypted messages to an Anchor link channel, ported from @wharfkit/sealed-messages.</summary>
    internal static class SealedMessage
    {
        /// <summary>The binary sealed_message for <paramref name="message"/>, from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static byte[] Seal(string message, PrivateKey from, PublicKey to, ulong? nonce = null)
        {
            var n = nonce ?? BitConverter.ToUInt64(WaxCrypto.RandomBytes(8), 0);
            var keys = Keys(from.SharedSecret(to), n, out var iv);
            var ciphertext = WaxCrypto.AesCbc(true, keys, Base58.Slice(iv, 32, 16), Encoding.UTF8.GetBytes(message));
            var checksum = WaxCrypto.Sha256(iv);

            return BuiltinAbi.Instance.Encode("sealed_message", new JObject
            {
                ["from"] = from.ToPublic().ToString(),
                ["nonce"] = n.ToString(),
                ["ciphertext"] = Hex.Encode(ciphertext),
                ["checksum"] = BitConverter.ToUInt32(checksum, 0),
            });
        }

        /// <summary>Opens a sealed_message sent to <paramref name="to"/>. Checks the checksum.</summary>
        public static string Unseal(byte[] sealedMessage, PrivateKey to)
        {
            var json = (JObject)BuiltinAbi.Instance.Decode("sealed_message", sealedMessage);
            var from = PublicKey.Parse((string)json["from"]);
            var nonce = ulong.Parse((string)json["nonce"]);
            var key = Keys(to.SharedSecret(from), nonce, out var iv);
            if (BitConverter.ToUInt32(WaxCrypto.Sha256(iv), 0) != (uint)json["checksum"])
                throw new FormatException("Sealed message checksum does not match.");
            var plain = WaxCrypto.AesCbc(false, key, Base58.Slice(iv, 32, 16), Hex.Decode((string)json["ciphertext"]));
            return Encoding.UTF8.GetString(plain);
        }

        /// <summary>key = sha512(nonce + secret)[0..32]; the same hash is the IV source.</summary>
        static byte[] Keys(byte[] secret, ulong nonce, out byte[] iv)
        {
            var writer = new AbiWriter();
            writer.WriteUInt64(nonce);
            writer.WriteBytes(secret);
            iv = WaxCrypto.Sha512(writer.ToArray());
            return Base58.Slice(iv, 0, 32);
        }
    }

    /// <summary>Login proof from an identity request (ESR v3), ported from @wharfkit/signing-request IdentityProof.</summary>
    internal sealed class IdentityProof
    {
        /// <summary>Chain the proof was signed for.</summary>
        public string ChainId;
        /// <summary>The app name (account-style) the login was for.</summary>
        public string Scope;
        /// <summary>UTC time after which the proof is no longer accepted.</summary>
        public DateTime Expiration;
        /// <summary>Account that logged in.</summary>
        public string Actor;
        /// <summary>Permission that signed.</summary>
        public string Permission;
        /// <summary>The signature (SIG_K1_...).</summary>
        public string Signature;

        /// <summary>Reads the proof from a wallet's login answer.</summary>
        public static IdentityProof FromPayload(JObject payload)
        {
            var request = SigningRequest.FromUri((string)payload["req"]);
            if (request.Version < 3 || !request.IsIdentity) throw new FormatException("Not an identity request.");
            return new IdentityProof
            {
                ChainId = (string)payload["cid"] ?? request.ChainId,
                Scope = request.IdentityScope,
                Expiration = AntelopeTime.Parse((string)payload["ex"]),
                Actor = (string)payload["sa"],
                Permission = (string)payload["sp"],
                Signature = (string)payload["sig"],
            };
        }

        /// <summary>The digest the wallet signed: an identity action in an empty transaction.</summary>
        public byte[] SigningDigest()
        {
            var level = new JObject { ["actor"] = Actor, ["permission"] = Permission };
            var transaction = SigningRequest.EmptyTransaction();
            transaction["expiration"] = AntelopeTime.FormatSeconds(Expiration);
            ((JArray)transaction["actions"]).Add(new JObject
            {
                ["account"] = "",
                ["name"] = "identity",
                ["authorization"] = new JArray(level),
                ["data"] = Hex.Encode(BuiltinAbi.Instance.Encode("identity_v3", new JObject { ["scope"] = Scope, ["permission"] = level })),
            });
            return AntelopeTransaction.SigningDigest(ChainId, AntelopeTransaction.Encode(transaction));
        }

        /// <summary>The key that signed the proof.</summary>
        public PublicKey Recover() => Antelope.Signature.Parse(Signature).RecoverDigest(SigningDigest());

        /// <summary>
        /// True when the proof is not expired and the recovered key alone satisfies <paramref name="requiredAuth"/>
        /// (the permission's required_auth from get_account), as Authority.hasPermission.
        /// </summary>
        public bool Verify(JToken requiredAuth, DateTime now)
        {
            if (now >= Expiration) return false;
            var key = Recover();
            var threshold = (long)requiredAuth["threshold"];
            var weight = (requiredAuth["keys"] as JArray ?? new JArray())
                .Where(k => PublicKey.Parse((string)k["key"]).Equals(key))
                .Sum(k => (long)k["weight"]);
            return weight >= threshold;
        }

        /// <summary>"EOSIO base64" header form, as IdentityProof.toString.</summary>
        public override string ToString()
        {
            var data = BuiltinAbi.Instance.Encode("identity_proof", new JObject
            {
                ["chainId"] = ChainId,
                ["scope"] = Scope,
                ["expiration"] = AntelopeTime.FormatSeconds(Expiration),
                ["signer"] = new JObject { ["actor"] = Actor, ["permission"] = Permission },
                ["signature"] = Signature,
            });
            return "EOSIO " + Convert.ToBase64String(data).TrimEnd('=');
        }
    }
}
