using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityWaxKit
{
    /// <summary>
    /// Where a media reference points: an IPFS file (CID and path; any gateway can serve it) or a plain https URL (fetched
    /// as it is, never rewritten). http and every other scheme are refused.
    /// </summary>
    internal sealed class WaxMediaSource
    {
        static readonly Regex CidV0 = new Regex("^Qm[1-9A-HJ-NP-Za-km-z]{44}$");
        static readonly Regex CidV1 = new Regex("^b[a-z2-7]{50,}$");

        /// <summary>The IPFS content id, or null for a plain URL.</summary>
        public string Cid { get; private set; }
        /// <summary>The path inside the CID ("" or "/a/b.png"), each segment escaped.</summary>
        public string Path { get; private set; } = "";
        /// <summary>The plain https URL, or null for IPFS.</summary>
        public string Url { get; private set; }

        public bool IsIpfs => Cid != null;

        /// <summary>The cache key: the same file always has the same key, whatever gateway served it.</summary>
        public string Key => IsIpfs ? "ipfs:" + Cid + Path : Url;

        /// <summary>The URLs to try, in order.</summary>
        public List<string> Urls(IEnumerable<string> gateways) =>
            IsIpfs ? gateways.Select(g => g + "/ipfs/" + Cid + Path).ToList() : new List<string> { Url };

        public static bool IsCid(string text) => text != null && (CidV0.IsMatch(text) || CidV1.IsMatch(text));

        /// <summary>
        /// Reads a reference: a bare CID (optionally with a path), "ipfs://CID/path" (also "ipfs://ipfs/CID"), a gateway URL
        /// "https://host/ipfs/CID/path" (the CID and path are kept, the host and query are not), or another https URL.
        /// </summary>
        public static WaxMediaSource Parse(string reference)
        {
            var text = (reference ?? "").Trim();
            if (text.Length == 0) throw Refused("The NFT has no media reference.");

            if (text.StartsWith("ipfs://", StringComparison.OrdinalIgnoreCase))
            {
                var rest = text.Substring(7);
                if (rest.StartsWith("ipfs/", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(5);
                return Ipfs(rest, reference);
            }
            if (!text.Contains("://")) return Ipfs(text, reference);

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) throw Refused($"'{Short(reference)}' is not a valid link.");
            if (uri.Scheme == Uri.UriSchemeHttp) throw Refused($"'{Short(reference)}' is a plain http link; the kit loads https only.");
            if (uri.Scheme != Uri.UriSchemeHttps) throw Refused($"'{Short(reference)}' is not an https or IPFS link.");

            var path = uri.AbsolutePath;
            if (path.StartsWith("/ipfs/", StringComparison.Ordinal))
            {
                var cidAndPath = path.Substring(6);
                var cid = cidAndPath.Split('/')[0];
                if (IsCid(cid)) return Ipfs(cidAndPath, reference);
            }
            return new WaxMediaSource { Url = uri.AbsoluteUri };
        }

        static WaxMediaSource Ipfs(string cidAndPath, string reference)
        {
            var cut = cidAndPath.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) cidAndPath = cidAndPath.Substring(0, cut);
            var slash = cidAndPath.IndexOf('/');
            var cid = slash < 0 ? cidAndPath : cidAndPath.Substring(0, slash);
            if (!IsCid(cid)) throw Refused($"'{Short(reference)}' is not an IPFS content id or an https link.");

            var path = new StringBuilder();
            if (slash >= 0)
            {
                foreach (var segment in cidAndPath.Substring(slash + 1).Split('/'))
                {
                    if (segment.Length == 0) continue;
                    var plain = Uri.UnescapeDataString(segment);
                    if (plain == "." || plain == ".." || plain.IndexOfAny(new[] { '/', '\\' }) >= 0 || plain.Any(char.IsControl))
                        throw Refused($"'{Short(reference)}' has a path the kit does not follow.");
                    path.Append('/').Append(Uri.EscapeDataString(plain));
                }
            }
            return new WaxMediaSource { Cid = cid, Path = path.ToString() };
        }

        static WaxKitException Refused(string message) => new WaxKitException(WaxErrorKind.NotSupported, message);

        static string Short(string text) => text == null ? "" : text.Length > 120 ? text.Substring(0, 120) + "…" : text;
    }

    /// <summary>File kind from the first bytes, and the pixel size from the header (before anything is decoded).</summary>
    internal static class WaxMediaFormat
    {
        /// <summary>The file kind; <see cref="WaxMediaType.Unknown"/> for anything else (HTML, SVG, XML, text, …).</summary>
        public static WaxMediaType Detect(byte[] data)
        {
            if (data == null || data.Length < 4) return WaxMediaType.Unknown;
            if (Starts(data, 0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return WaxMediaType.Png;
            if (Starts(data, 0, 0xFF, 0xD8, 0xFF)) return WaxMediaType.Jpeg;
            if (Starts(data, 0, 0x47, 0x49, 0x46, 0x38) && data.Length >= 6 && (data[4] == 0x37 || data[4] == 0x39) && data[5] == 0x61) return WaxMediaType.Gif;
            if (data.Length >= 12 && Ascii(data, 0, "RIFF") && Ascii(data, 8, "WEBP")) return WaxMediaType.WebP;
            if (data.Length >= 12 && Ascii(data, 4, "ftyp")) return WaxMediaType.Mp4;
            if (Starts(data, 0, 0x1A, 0x45, 0xDF, 0xA3)) return WaxMediaType.WebM;
            return WaxMediaType.Unknown;
        }

        public static bool IsVideo(WaxMediaType type) => type == WaxMediaType.Mp4 || type == WaxMediaType.WebM;
        public static bool IsImage(WaxMediaType type) => type == WaxMediaType.Png || type == WaxMediaType.Jpeg || type == WaxMediaType.Gif || type == WaxMediaType.WebP;

        /// <summary>A WebP with an animation (VP8X flag).</summary>
        public static bool IsAnimatedWebP(byte[] data) =>
            data != null && data.Length >= 21 && Ascii(data, 12, "VP8X") && (data[20] & 0x02) != 0;

        /// <summary>Width and height from the header, or null when the header cannot be read.</summary>
        public static (int width, int height)? ReadSize(byte[] data, WaxMediaType type)
        {
            try
            {
                switch (type)
                {
                    case WaxMediaType.Png:
                        if (data.Length < 24 || !Ascii(data, 12, "IHDR")) return null;
                        return (BigEndian(data, 16), BigEndian(data, 20));
                    case WaxMediaType.Gif:
                        return data.Length < 10 ? ((int, int)?)null : (data[6] | data[7] << 8, data[8] | data[9] << 8);
                    case WaxMediaType.Jpeg:
                        return JpegSize(data);
                    case WaxMediaType.WebP:
                        return WebPSize(data);
                    default:
                        return null;
                }
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        static (int, int)? JpegSize(byte[] d)
        {
            var i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) return null;
                var marker = d[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                var length = d[i + 2] << 8 | d[i + 3];
                // SOF0-SOF15 except DHT (C4), JPG (C8) and DAC (CC).
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    return (d[i + 7] << 8 | d[i + 8], d[i + 5] << 8 | d[i + 6]);
                if (length < 2) return null;
                i += 2 + length;
            }
            return null;
        }

        static (int, int)? WebPSize(byte[] d)
        {
            if (d.Length < 30) return null;
            if (Ascii(d, 12, "VP8X")) return (1 + (d[24] | d[25] << 8 | d[26] << 16), 1 + (d[27] | d[28] << 8 | d[29] << 16));
            if (Ascii(d, 12, "VP8L") && d[20] == 0x2F)
            {
                var bits = (uint)(d[21] | d[22] << 8 | d[23] << 16 | d[24] << 24);
                return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }
            if (Ascii(d, 12, "VP8 ") && d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A)
                return ((d[26] | d[27] << 8) & 0x3FFF, (d[28] | d[29] << 8) & 0x3FFF);
            return null;
        }

        static int BigEndian(byte[] d, int i) => d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3];

        static bool Starts(byte[] d, int offset, params byte[] magic)
        {
            if (d.Length < offset + magic.Length) return false;
            for (var i = 0; i < magic.Length; i++) if (d[offset + i] != magic[i]) return false;
            return true;
        }

        static bool Ascii(byte[] d, int offset, string text)
        {
            if (d.Length < offset + text.Length) return false;
            for (var i = 0; i < text.Length; i++) if (d[offset + i] != text[i]) return false;
            return true;
        }
    }
}
