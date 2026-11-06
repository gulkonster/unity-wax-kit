using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityWaxKit
{
    /// <summary>Animated or still NFT media.</summary>
    public enum WaxMediaMode
    {
        /// <summary>GIFs animate and videos play.</summary>
        Animated,
        /// <summary>
        /// Light: one still image. A video shows its cover image, else its first frame when
        /// <see cref="WaxMediaSettings.FirstFrameForVideo"/> is on, else the placeholder. Nothing animates, decodes more
        /// frames or plays sound.
        /// </summary>
        Static,
    }

    /// <summary>
    /// Settings of NFT media (<see cref="WaxKit.MediaSettings"/>; the WAX Kit Manager sets them too): which data fields hold
    /// the image and video, IPFS gateways, animated or still, and the size and cache limits.
    /// </summary>
    public sealed class WaxMediaSettings
    {
        /// <summary>Public IPFS gateways tried after <see cref="Gateways"/>. They are rate-limited and change. You can start with them; choose your own gateway when your traffic or uptime needs it.</summary>
        public static readonly IReadOnlyList<string> DefaultGateways = new[] { "https://gateway.pinata.cloud", "https://ipfs.filebase.io" };

        readonly Dictionary<string, (string image, string video, string cover)> fields = new Dictionary<string, (string, string, string)>();
        string imageField = "img", videoField = "video", coverField = "img";
        int gatewayTimeoutSeconds = 15, videoStartTimeoutSeconds = 30, maxWidth = 4096, maxHeight = 4096, maxFrames = 500;
        long maxDownloadBytes = 32L << 20, maxDecodedBytes = 128L << 20, memoryCacheBytes = 64L << 20, diskCacheBytes = 256L << 20;

        /// <summary>Data field of the image (default "img").</summary>
        public string ImageField { get => imageField; set => imageField = Field(value, nameof(ImageField)); }
        /// <summary>Data field of the video (default "video").</summary>
        public string VideoField { get => videoField; set => videoField = Field(value, nameof(VideoField)); }
        /// <summary>Data field of the still image shown for a video in <see cref="WaxMediaMode.Static"/> (default "img").</summary>
        public string CoverField { get => coverField; set => coverField = Field(value, nameof(CoverField)); }

        /// <summary>
        /// Field names for one collection, or one schema of it (<paramref name="schema"/> null: the whole collection). A schema
        /// entry wins over its collection's, which wins over the global names. A null name keeps the next level's.
        /// </summary>
        public void SetFields(string collection, string schema, string imageField, string videoField = null, string coverField = null)
        {
            if (!WaxToken.IsAccountName(collection)) throw new ArgumentException($"'{collection}' is not a collection name.", nameof(collection));
            if (schema != null && !WaxToken.IsAccountName(schema)) throw new ArgumentException($"'{schema}' is not a schema name.", nameof(schema));
            fields[Key(collection, schema)] = (Optional(imageField), Optional(videoField), Optional(coverField));
        }

        /// <summary>Removes the names set with <see cref="SetFields"/> for this collection / schema.</summary>
        public void ClearFields(string collection, string schema = null) => fields.Remove(Key(collection, schema));

        /// <summary>
        /// Your own IPFS gateways ("https://my-gateway.example"), tried first and in order, then
        /// <see cref="DefaultGateways"/> when <see cref="UseDefaultGateways"/> is on. A gateway URL is public once the game
        /// ships: never put a secret key in it.
        /// </summary>
        public List<string> Gateways { get; } = new List<string>();

        /// <summary>Also try <see cref="DefaultGateways"/> after <see cref="Gateways"/>. Default on.</summary>
        public bool UseDefaultGateways { get; set; } = true;

        /// <summary>Seconds one gateway may take before the next is tried. Default 15.</summary>
        public int GatewayTimeoutSeconds { get => gatewayTimeoutSeconds; set => gatewayTimeoutSeconds = Positive(value, nameof(GatewayTimeoutSeconds)); }

        /// <summary>
        /// Seconds a video may take to start (prepared and its first frame shown) in <see cref="WaxNftMedia"/>; later, the load
        /// fails. Default 30.
        /// </summary>
        public int VideoStartTimeoutSeconds { get => videoStartTimeoutSeconds; set => videoStartTimeoutSeconds = Positive(value, nameof(VideoStartTimeoutSeconds)); }

        /// <summary>Animated or still, for calls and components that do not choose. Default animated.</summary>
        public WaxMediaMode Mode { get; set; } = WaxMediaMode.Animated;

        /// <summary>Videos play their sound. Default off.</summary>
        public bool VideoAudio { get; set; }

        /// <summary>
        /// In <see cref="WaxMediaMode.Static"/>, a video without a cover image shows its first frame (it downloads the start of
        /// the video and decodes one frame). Default off: the placeholder.
        /// </summary>
        public bool FirstFrameForVideo { get; set; }

        /// <summary>Largest file the kit downloads (bytes). Default 32 MB. Videos stream and are not limited by it.</summary>
        public long MaxDownloadBytes { get => maxDownloadBytes; set => maxDownloadBytes = Positive(value, nameof(MaxDownloadBytes)); }
        /// <summary>Largest image width in pixels. Default 4096.</summary>
        public int MaxWidth { get => maxWidth; set => maxWidth = Positive(value, nameof(MaxWidth)); }
        /// <summary>Largest image height in pixels. Default 4096.</summary>
        public int MaxHeight { get => maxHeight; set => maxHeight = Positive(value, nameof(MaxHeight)); }
        /// <summary>Most frames of an animation. Default 500.</summary>
        public int MaxFrames { get => maxFrames; set => maxFrames = Positive(value, nameof(MaxFrames)); }
        /// <summary>Most memory the decoded frames of one image may use (width x height x 4 x frames). Default 128 MB.</summary>
        public long MaxDecodedBytes { get => maxDecodedBytes; set => maxDecodedBytes = Positive(value, nameof(MaxDecodedBytes)); }
        /// <summary>Downloaded files kept in memory (bytes). Default 64 MB; 0 = none.</summary>
        public long MemoryCacheBytes { get => memoryCacheBytes; set => memoryCacheBytes = NotNegative(value, nameof(MemoryCacheBytes)); }
        /// <summary>Downloaded files kept on disk on Windows and Android (bytes). Default 256 MB; 0 = none. WebGL keeps no disk cache.</summary>
        public long DiskCacheBytes { get => diskCacheBytes; set => diskCacheBytes = NotNegative(value, nameof(DiskCacheBytes)); }

        /// <summary>The field names for an NFT of this collection and schema.</summary>
        internal (string image, string video, string cover) FieldsFor(string collection, string schema)
        {
            string image = imageField, video = videoField, cover = coverField;
            if (collection != null && fields.TryGetValue(Key(collection, null), out var c))
            {
                image = c.image ?? image;
                video = c.video ?? video;
                cover = c.cover ?? cover;
            }
            if (collection != null && schema != null && fields.TryGetValue(Key(collection, schema), out var s))
            {
                image = s.image ?? image;
                video = s.video ?? video;
                cover = s.cover ?? cover;
            }
            return (image, video, cover);
        }

        /// <summary>The gateways to try, in order, without duplicates (each "https://host[/prefix]" without a trailing slash).</summary>
        internal List<string> GatewayList()
        {
            var list = new List<string>();
            foreach (var g in Gateways.Concat(UseDefaultGateways ? DefaultGateways : Enumerable.Empty<string>()))
            {
                var url = CheckGateway(g);
                if (url != null && !list.Contains(url)) list.Add(url);
            }
            return list;
        }

        /// <summary>A gateway as the kit uses it, or null when it is not an absolute https URL.</summary>
        internal static string CheckGateway(string gateway)
        {
            var url = (gateway ?? "").Trim().TrimEnd('/');
            if (url.EndsWith("/ipfs", StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - 5);
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Query) ? url : null;
        }

        static string Key(string collection, string schema) => collection + "/" + (schema ?? "");

        static string Field(string value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A field name is required.", name) : value.Trim();

        static string Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        static int Positive(int value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name, "Must be positive.");
        static long Positive(long value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name, "Must be positive.");
        static long NotNegative(long value, string name) => value >= 0 ? value : throw new ArgumentOutOfRangeException(name, "Cannot be negative.");
    }

    /// <summary>The media references of one NFT, read from its data with the field names of <see cref="WaxMediaSettings"/>.</summary>
    public sealed class WaxNftMediaInfo
    {
        /// <summary>Asset id, or null for a template.</summary>
        public string AssetId { get; internal set; }
        /// <summary>Template id, or null.</summary>
        public string TemplateId { get; internal set; }
        /// <summary>Collection name.</summary>
        public string Collection { get; internal set; }
        /// <summary>Schema name.</summary>
        public string Schema { get; internal set; }
        /// <summary>The NFT's name ("name" field), or null.</summary>
        public string Name { get; internal set; }
        /// <summary>The image reference (an IPFS hash, ipfs:// link or https URL), or null.</summary>
        public string Image { get; internal set; }
        /// <summary>The video reference, or null.</summary>
        public string Video { get; internal set; }
        /// <summary>The still image shown for the video in static mode, or null.</summary>
        public string Cover { get; internal set; }
        /// <summary>True when the NFT has a video.</summary>
        public bool HasVideo => !string.IsNullOrEmpty(Video);
    }

    /// <summary>The kind of a media file, read from its first bytes (never from its name or the server's header).</summary>
    public enum WaxMediaType
    {
        /// <summary>Not a supported file. SVG, HTML and other script-capable files are never opened.</summary>
        Unknown,
        /// <summary>PNG.</summary>
        Png,
        /// <summary>JPEG.</summary>
        Jpeg,
        /// <summary>GIF.</summary>
        Gif,
        /// <summary>WebP.</summary>
        WebP,
        /// <summary>MP4 video.</summary>
        Mp4,
        /// <summary>WebM video.</summary>
        WebM,
    }

    /// <summary>Decoded frames: RGBA32, rows top to bottom (internal; textures are made from it).</summary>
    internal sealed class WaxDecodedImage
    {
        public int Width;
        public int Height;
        public readonly List<byte[]> Frames = new List<byte[]>();
        /// <summary>Display time of each frame in milliseconds.</summary>
        public readonly List<int> DelaysMs = new List<int>();
        /// <summary>GIF NETSCAPE loop count: 0 = forever, n = repeat n times, -1 = no loop entry (play once).</summary>
        public int LoopCount = -1;
        /// <summary>The file ended early; the frames that finished are kept.</summary>
        public bool Truncated;
    }

    /// <summary>The size and memory limits one decode applies.</summary>
    internal struct WaxMediaLimits
    {
        public int MaxWidth;
        public int MaxHeight;
        public int MaxFrames;
        public long MaxDecodedBytes;

        public static WaxMediaLimits From(WaxMediaSettings s) => new WaxMediaLimits
        {
            MaxWidth = s.MaxWidth, MaxHeight = s.MaxHeight, MaxFrames = s.MaxFrames, MaxDecodedBytes = s.MaxDecodedBytes,
        };

        /// <summary>Throws when an image of this size cannot be decoded within the limits (one frame).</summary>
        public void CheckSize(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new WaxKitException(WaxErrorKind.NotSupported, "The image has no size.");
            if (width > MaxWidth || height > MaxHeight)
                throw new WaxKitException(WaxErrorKind.NotSupported, $"The image is {width} x {height} pixels, larger than the limit {MaxWidth} x {MaxHeight} (WaxKit.MediaSettings).");
            if ((long)width * height * 4 > MaxDecodedBytes)
                throw new WaxKitException(WaxErrorKind.NotSupported, $"The image needs {Size((long)width * height * 4)} decoded, more than the limit {Size(MaxDecodedBytes)} (MaxDecodedBytes).");
        }

        /// <summary>"32 MB", or "1000 bytes" below one megabyte.</summary>
        public static string Size(long bytes) => bytes >= 1 << 20 && bytes % (1 << 20) == 0 ? (bytes >> 20) + " MB"
            : bytes >= 1 << 20 ? (bytes / 1048576.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB"
            : bytes + " bytes";
    }
}
