using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace UnityWaxKit
{
    /// <summary>
    /// A loaded NFT image: one texture, or the frames of an animation with their display times. <b>You own it</b>: call
    /// <see cref="Dispose"/> when it is no longer shown, which destroys the textures (the <see cref="WaxNftMedia"/> component
    /// does this for its own images). The textures are GPU-only (not readable).
    /// </summary>
    public sealed class WaxNftImage : IDisposable
    {
        readonly List<Texture2D> frames;

        internal WaxNftImage(List<Texture2D> frames, List<float> delays, int loopCount, WaxMediaType type, string url, bool truncated)
        {
            this.frames = frames;
            Delays = delays;
            LoopCount = loopCount;
            Type = type;
            Url = url;
            Truncated = truncated;
        }

        /// <summary>The frames (one for a still image).</summary>
        public IReadOnlyList<Texture2D> Frames => frames;
        /// <summary>The first frame.</summary>
        public Texture2D Texture => frames.Count > 0 ? frames[0] : null;
        /// <summary>Display time of each frame in seconds.</summary>
        public IReadOnlyList<float> Delays { get; }
        /// <summary>GIF loop count: 0 = forever, n = repeat n times, -1 = play once. Other formats: 0.</summary>
        public int LoopCount { get; }
        /// <summary>More than one frame.</summary>
        public bool IsAnimated => frames.Count > 1;
        /// <summary>Width in pixels.</summary>
        public int Width => Texture != null ? Texture.width : 0;
        /// <summary>Height in pixels.</summary>
        public int Height => Texture != null ? Texture.height : 0;
        /// <summary>The file kind (from its first bytes).</summary>
        public WaxMediaType Type { get; }
        /// <summary>The URL that served it ("cache:…" when it came from the cache).</summary>
        public string Url { get; }
        /// <summary>The file was cut off; the frames that finished are shown.</summary>
        public bool Truncated { get; }
        /// <summary>True after <see cref="Dispose"/>.</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>Destroys the textures.</summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            foreach (var texture in frames)
                if (texture != null) Free(texture);
            frames.Clear();
        }

        /// <summary>Destroys a texture (immediately outside Play mode in the Editor).</summary>
        internal static void Free(Texture2D texture)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    /// <summary>
    /// NFT media (get it from <see cref="WaxKit.NftMedia"/>): reads an NFT's image and video references with the field names
    /// of <see cref="WaxKit.MediaSettings"/>, downloads them (IPFS through the gateways in order), checks the kind from the
    /// first bytes and the size limits, and makes textures. Errors are <see cref="WaxKitException"/> per item (NotSupported:
    /// kind, platform or limit; Network: no gateway or server delivered); cancelling stops the call.
    /// </summary>
    public sealed class WaxNftMediaClient
    {
        readonly WaxMediaSettings settings;
        readonly WaxMediaLoader loader;

        internal WaxNftMediaClient(WaxMediaSettings settings, WaxMediaLoader loader)
        {
            this.settings = settings;
            this.loader = loader;
        }

        internal WaxMediaLoader Loader => loader;

        /// <summary>The media references of an asset (one AtomicAssets read on the current network).</summary>
        public async Task<WaxNftMediaInfo> GetAssetMedia(string assetId, CancellationToken cancellation = default)
        {
            var asset = await WaxKit.AtomicAssets.GetAsset(assetId);
            cancellation.ThrowIfCancellationRequested();
            if (asset == null) throw new WaxKitException(WaxErrorKind.Network, $"Asset {assetId} was not found.");
            return WaxNftMediaData.FromAsset(settings, asset);
        }

        /// <summary>
        /// The media references of a template. With the collection, one single-template read; without it, the template list
        /// filtered by this id.
        /// </summary>
        public async Task<WaxNftMediaInfo> GetTemplateMedia(string templateId, string collection = null, CancellationToken cancellation = default)
        {
            if (string.IsNullOrWhiteSpace(templateId)) throw new ArgumentException("A template id is required.", nameof(templateId));
            var template = await WaxNftMediaData.GetTemplate(WaxKit.AtomicAssets, templateId.Trim(), string.IsNullOrWhiteSpace(collection) ? null : collection.Trim());
            cancellation.ThrowIfCancellationRequested();
            if (template == null) throw new WaxKitException(WaxErrorKind.Network, $"Template {templateId} was not found.");
            return WaxNftMediaData.FromTemplate(settings, template);
        }

        /// <summary>
        /// Loads an image (an IPFS hash, ipfs:// link or https URL): PNG, JPG and GIF everywhere, WebP on WebGL and Android.
        /// <paramref name="mode"/> null uses <see cref="WaxMediaSettings.Mode"/>; static loads the first frame only.
        /// The caller owns the result (<see cref="WaxNftImage.Dispose"/>).
        /// </summary>
        public async Task<WaxNftImage> LoadImage(string reference, WaxMediaMode? mode = null, CancellationToken cancellation = default)
        {
            var loaded = await loader.LoadImage(reference, mode ?? settings.Mode, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return ToTextures(loaded);
        }

        /// <summary>
        /// A URL a <see cref="UnityEngine.Video.VideoPlayer"/> can play: the first gateway (or the https server) that answers
        /// with an MP4 or WebM file. On WebGL the server must allow cross-origin reads (CORS).
        /// </summary>
        public Task<string> GetVideoUrl(string reference, CancellationToken cancellation = default) => loader.ResolveVideoUrl(reference, cancellation);

        /// <summary>Forgets the downloaded files kept in memory, and on disk when <paramref name="disk"/> is true.</summary>
        public void ClearCache(bool disk = false) => loader.ClearCache(disk);

        internal static WaxNftImage ToTextures(WaxLoadedImage loaded)
        {
            var textures = new List<Texture2D>();
            var delays = new List<float>();
            if (loaded.Encoded != null)
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "NFT image" };
                if (!texture.LoadImage(loaded.Encoded, true) || texture.width != loaded.Width || texture.height != loaded.Height)
                {
                    WaxNftImage.Free(texture);
                    throw new WaxKitException(WaxErrorKind.NotSupported, $"The {loaded.Type} file is broken.");
                }
                textures.Add(texture);
                delays.Add(0);
                return new WaxNftImage(textures, delays, 0, loaded.Type, loaded.Url, false);
            }

            var decoded = loaded.Decoded;
            var row = decoded.Width * 4;
            var flipped = new byte[row * decoded.Height];
            try
            {
                for (var i = 0; i < decoded.Frames.Count; i++)
                {
                    var frame = decoded.Frames[i];
                    // Unity textures start at the bottom row.
                    for (var y = 0; y < decoded.Height; y++)
                        Buffer.BlockCopy(frame, y * row, flipped, (decoded.Height - 1 - y) * row, row);
                    var texture = new Texture2D(decoded.Width, decoded.Height, TextureFormat.RGBA32, false) { name = "NFT frame " + i };
                    texture.LoadRawTextureData(flipped);
                    texture.Apply(false, true);
                    textures.Add(texture);
                    delays.Add(decoded.DelaysMs[i] / 1000f);
                    decoded.Frames[i] = null; // the CPU copy can go
                }
            }
            catch
            {
                foreach (var t in textures) WaxNftImage.Free(t);
                throw;
            }
            return new WaxNftImage(textures, delays, decoded.LoopCount, loaded.Type, loaded.Url, decoded.Truncated);
        }

        // ---- Unity side of the loader ----

        internal static WaxNftMediaClient Create(WaxMediaSettings settings)
        {
            var webgl = Application.platform == RuntimePlatform.WebGLPlayer;
            var platform = Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor ? "Windows"
                : Application.platform == RuntimePlatform.Android ? "Android"
                : webgl ? "WebGL" : Application.platform.ToString();
            var loader = new WaxMediaLoader(settings, Fetch, WaxMediaPlatformDecoder.Decoder, platform,
                webgl ? null : Path.Combine(Application.temporaryCachePath, "WaxKitMedia"),
                webgl ? (Func<Func<object>, Task<object>>)null : work => Task.Run(work));
            return new WaxNftMediaClient(settings, loader);
        }

        /// <summary>
        /// One GET through UnityWebRequest. Stops as soon as the server's Content-Length or the bytes received pass the limit.
        /// On WebGL the browser reports neither until the end when the server sends no Content-Length; the size is then checked
        /// after the download.
        /// </summary>
        static async Task<WaxMediaResponse> Fetch(WaxMediaRequest request, CancellationToken cancellation)
        {
            using var web = UnityWebRequest.Get(request.Url);
            web.timeout = request.TimeoutSeconds;
            if (request.RangeEnd != null) web.SetRequestHeader("Range", "bytes=0-" + request.RangeEnd.Value);
            var operation = web.SendWebRequest();
            var tooLarge = false;
            while (!operation.isDone)
            {
                if (cancellation.IsCancellationRequested)
                {
                    web.Abort();
                    break;
                }
                if ((long)web.downloadedBytes > request.MaxBytes ||
                    (long.TryParse(web.GetResponseHeader("Content-Length"), out var length) && length > request.MaxBytes))
                {
                    tooLarge = true;
                    web.Abort();
                    break;
                }
                await NextFrame();
            }
            cancellation.ThrowIfCancellationRequested();
            if (tooLarge) return new WaxMediaResponse { TooLarge = true };
            if (web.result == UnityWebRequest.Result.ConnectionError)
                return new WaxMediaResponse { Status = 0, Error = web.error };
            var data = web.downloadHandler?.data;
            if (data != null && data.Length > request.MaxBytes) return new WaxMediaResponse { TooLarge = true };
            return new WaxMediaResponse { Status = web.responseCode, Data = data, Error = web.error };
        }

        static async Task NextFrame()
        {
            if (SynchronizationContext.Current != null) await Task.Yield();
            else await Task.Delay(16);
        }
    }
}
