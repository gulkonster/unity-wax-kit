using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityWaxKit
{
    /// <summary>One media download.</summary>
    internal sealed class WaxMediaRequest
    {
        public string Url;
        /// <summary>Stop and fail above this many bytes (the server's Content-Length first, where the platform can see it).</summary>
        public long MaxBytes;
        public int TimeoutSeconds;
        /// <summary>Ask only for bytes 0..RangeEnd (video checks); the server may still send everything.</summary>
        public int? RangeEnd;
    }

    internal sealed class WaxMediaResponse
    {
        /// <summary>HTTP status, or 0 for a network error / timeout.</summary>
        public long Status;
        public byte[] Data;
        public string Error;
        /// <summary>True when the download was stopped for being larger than <see cref="WaxMediaRequest.MaxBytes"/>.</summary>
        public bool TooLarge;
    }

    internal delegate Task<WaxMediaResponse> WaxMediaFetch(WaxMediaRequest request, CancellationToken cancellation);

    /// <summary>A platform image decoder (WebP on WebGL and Android); null where there is none.</summary>
    internal delegate Task<WaxDecodedImage> WaxPlatformDecode(byte[] data, WaxMediaType type, WaxMediaLimits limits, bool firstFrameOnly, CancellationToken cancellation);

    /// <summary>A downloaded file with its kind (from its first bytes) and the URL that served it.</summary>
    internal sealed class WaxMediaFile
    {
        public byte[] Data;
        public WaxMediaType Type;
        public string Url;
    }

    /// <summary>An image ready for textures: PNG / JPG bytes for LoadImage (size already checked), or decoded frames.</summary>
    internal sealed class WaxLoadedImage
    {
        public WaxMediaType Type;
        public string Url;
        public int Width;
        public int Height;
        public byte[] Encoded;
        public WaxDecodedImage Decoded;
    }

    /// <summary>
    /// Downloads and checks NFT media without Unity types: IPFS gateways in order with a timeout each (only for IPFS
    /// references; other https URLs are fetched as they are), the kind from the first bytes, the size limits, a memory and
    /// a disk cache, one download per file at a time, then decoding (GIF here; WebP through the platform).
    /// </summary>
    internal sealed class WaxMediaLoader
    {
        const int VideoProbeBytes = 64;

        readonly WaxMediaSettings settings;
        readonly WaxMediaFetch fetch;
        readonly WaxPlatformDecode platformDecode;
        readonly string platformName;
        readonly Func<Func<object>, Task<object>> background;
        readonly MemoryCache memory;
        readonly DiskCache disk;
        readonly Dictionary<string, Task<WaxMediaFile>> inFlight = new Dictionary<string, Task<WaxMediaFile>>();
        readonly Dictionary<string, string> videoUrls = new Dictionary<string, string>();

        /// <param name="diskDirectory">Folder of the disk cache, or null for none (WebGL).</param>
        /// <param name="background">Runs CPU work off the main thread where the platform has threads; null: inline.</param>
        public WaxMediaLoader(WaxMediaSettings settings, WaxMediaFetch fetch, WaxPlatformDecode platformDecode, string platformName,
            string diskDirectory, Func<Func<object>, Task<object>> background = null)
        {
            this.settings = settings;
            this.fetch = fetch;
            this.platformDecode = platformDecode;
            this.platformName = platformName;
            this.background = background ?? (work => Task.FromResult(work()));
            memory = new MemoryCache(() => settings.MemoryCacheBytes);
            disk = diskDirectory == null ? null : new DiskCache(diskDirectory, () => settings.DiskCacheBytes);
        }

        /// <summary>Forgets the files kept in memory (and on disk).</summary>
        public void ClearCache(bool diskToo)
        {
            memory.Clear();
            lock (videoUrls) videoUrls.Clear();
            if (diskToo) disk?.Clear();
        }

        /// <summary>The file behind a reference: from the caches, or downloaded (IPFS: gateway after gateway).</summary>
        public async Task<WaxMediaFile> Fetch(string reference, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var source = WaxMediaSource.Parse(reference);
            var key = source.Key;
            var cached = memory.Get(key);
            if (cached != null) return cached;

            Task<WaxMediaFile> task;
            lock (inFlight)
            {
                if (!inFlight.TryGetValue(key, out task))
                {
                    task = Download(source);
                    inFlight[key] = task;
                    task.ContinueWith(_ => { lock (inFlight) inFlight.Remove(key); }, TaskContinuationOptions.ExecuteSynchronously);
                }
            }
            return await WithCancellation(task, cancellation);
        }

        async Task<WaxMediaFile> Download(WaxMediaSource source)
        {
            var key = source.Key;
            if (disk != null)
            {
                var stored = (byte[])await background(() => disk.Read(key));
                var storedType = stored == null ? WaxMediaType.Unknown : WaxMediaFormat.Detect(stored);
                if (storedType != WaxMediaType.Unknown && stored.Length <= settings.MaxDownloadBytes)
                {
                    var file = new WaxMediaFile { Data = stored, Type = storedType, Url = "cache:" + key };
                    memory.Put(key, file);
                    return file;
                }
            }

            var urls = source.Urls(settings.GatewayList());
            if (urls.Count == 0) throw new WaxKitException(WaxErrorKind.NotSupported, "No IPFS gateway is set (WaxKit.MediaSettings.Gateways).");
            string lastError = null;
            foreach (var url in urls)
            {
                var response = await fetch(new WaxMediaRequest
                {
                    Url = url, MaxBytes = settings.MaxDownloadBytes, TimeoutSeconds = settings.GatewayTimeoutSeconds,
                }, CancellationToken.None);
                if (response.TooLarge)
                    throw new WaxKitException(WaxErrorKind.NotSupported,
                        $"The file is larger than {WaxMediaLimits.Size(settings.MaxDownloadBytes)} (WaxKit.MediaSettings.MaxDownloadBytes).");
                if (response.Status / 100 != 2 || response.Data == null)
                {
                    lastError = $"{url}: {(response.Status == 0 ? response.Error : "HTTP " + response.Status)}";
                    continue;
                }
                var type = WaxMediaFormat.Detect(response.Data);
                if (type == WaxMediaType.Unknown)
                {
                    // A gateway's error or notice page, or a file the kit never opens (SVG, HTML, …).
                    lastError = $"{url}: not an image or video file ({Describe(response.Data)})";
                    if (!source.IsIpfs) throw new WaxKitException(WaxErrorKind.NotSupported, $"{url} is not a supported image or video file ({Describe(response.Data)}).");
                    continue;
                }
                var file = new WaxMediaFile { Data = response.Data, Type = type, Url = url };
                memory.Put(key, file);
                if (disk != null) await background(() => { disk.Write(key, response.Data); return null; });
                return file;
            }
            throw new WaxKitException(WaxErrorKind.Network,
                source.IsIpfs ? $"No IPFS gateway delivered the file. Last error: {lastError}" : $"The file could not be downloaded: {lastError}");
        }

        /// <summary>An image ready for textures, within the limits. Static mode decodes the first frame only.</summary>
        public async Task<WaxLoadedImage> LoadImage(string reference, WaxMediaMode mode, CancellationToken cancellation)
        {
            var file = await Fetch(reference, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (WaxMediaFormat.IsVideo(file.Type))
                throw new WaxKitException(WaxErrorKind.NotSupported, "The file is a video, not an image (use the video field or GetVideoUrl).");
            var limits = WaxMediaLimits.From(settings);
            var size = WaxMediaFormat.ReadSize(file.Data, file.Type)
                       ?? throw new WaxKitException(WaxErrorKind.NotSupported, $"The {file.Type} header cannot be read.");
            limits.CheckSize(size.width, size.height);
            var image = new WaxLoadedImage { Type = file.Type, Url = file.Url, Width = size.width, Height = size.height };
            var firstFrameOnly = mode == WaxMediaMode.Static;

            switch (file.Type)
            {
                case WaxMediaType.Png:
                case WaxMediaType.Jpeg:
                    image.Encoded = file.Data;
                    return image;
                case WaxMediaType.Gif:
                    image.Decoded = (WaxDecodedImage)await background(() => WaxGifDecoder.Decode(file.Data, limits, firstFrameOnly));
                    cancellation.ThrowIfCancellationRequested();
                    return image;
                case WaxMediaType.WebP:
                    if (platformDecode == null)
                        throw new WaxKitException(WaxErrorKind.NotSupported, $"WebP images are not supported on {platformName} yet.");
                    image.Decoded = await platformDecode(file.Data, file.Type, limits, firstFrameOnly, cancellation);
                    return image;
                default:
                    throw new WaxKitException(WaxErrorKind.NotSupported, "Not a supported image file.");
            }
        }

        /// <summary>
        /// A URL that serves the video: each gateway is asked for the first bytes, and the first that answers with an MP4 or
        /// WebM file is used (the player then streams from it).
        /// </summary>
        public async Task<string> ResolveVideoUrl(string reference, CancellationToken cancellation)
        {
            var source = WaxMediaSource.Parse(reference);
            lock (videoUrls)
            {
                if (videoUrls.TryGetValue(source.Key, out var known)) return known;
            }
            string lastError = null;
            foreach (var url in source.Urls(settings.GatewayList()))
            {
                cancellation.ThrowIfCancellationRequested();
                var response = await WithCancellation(fetch(new WaxMediaRequest
                {
                    Url = url, MaxBytes = Math.Min(settings.MaxDownloadBytes, 1 << 20), TimeoutSeconds = settings.GatewayTimeoutSeconds,
                    RangeEnd = VideoProbeBytes - 1,
                }, CancellationToken.None), cancellation);
                // A server that ignores the range and sends more than the probe allows is still a working server.
                if (response.TooLarge && response.Data == null) { lastError = $"{url}: no range support"; continue; }
                if (response.Status / 100 != 2 || response.Data == null)
                {
                    lastError = $"{url}: {(response.Status == 0 ? response.Error : "HTTP " + response.Status)}";
                    continue;
                }
                var type = WaxMediaFormat.Detect(response.Data);
                if (!WaxMediaFormat.IsVideo(type))
                {
                    lastError = $"{url}: not a video ({(type == WaxMediaType.Unknown ? Describe(response.Data) : type.ToString())})";
                    if (!source.IsIpfs || WaxMediaFormat.IsImage(type))
                        throw new WaxKitException(WaxErrorKind.NotSupported, $"The file is not an MP4 or WebM video ({lastError}).");
                    continue;
                }
                lock (videoUrls) videoUrls[source.Key] = url;
                return url;
            }
            throw new WaxKitException(WaxErrorKind.Network, $"No IPFS gateway delivered the video. Last error: {lastError}");
        }

        static string Describe(byte[] data)
        {
            var text = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 40)).Replace('\n', ' ').Replace('\r', ' ');
            return text.All(c => !char.IsControl(c)) ? "starts with \"" + text.Trim() + "\"" : data.Length + " bytes of an unknown kind";
        }

        static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken cancellation)
        {
            if (!cancellation.CanBeCanceled || task.IsCompleted) return await task;
            var cancelled = new TaskCompletionSource<bool>();
            using (cancellation.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task) != task)
                {
                    _ = task.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    throw new OperationCanceledException(cancellation);
                }
            }
            return await task;
        }

        /// <summary>Downloaded files in memory, least recently used dropped first.</summary>
        sealed class MemoryCache
        {
            readonly Func<long> capacity;
            readonly Dictionary<string, LinkedListNode<(string key, WaxMediaFile file)>> map = new Dictionary<string, LinkedListNode<(string, WaxMediaFile)>>();
            readonly LinkedList<(string key, WaxMediaFile file)> order = new LinkedList<(string, WaxMediaFile)>();
            long size;

            public MemoryCache(Func<long> capacity) => this.capacity = capacity;

            public WaxMediaFile Get(string key)
            {
                lock (map)
                {
                    if (!map.TryGetValue(key, out var node)) return null;
                    order.Remove(node);
                    order.AddFirst(node);
                    return node.Value.file;
                }
            }

            public void Put(string key, WaxMediaFile file)
            {
                lock (map)
                {
                    if (map.TryGetValue(key, out var old))
                    {
                        order.Remove(old);
                        size -= old.Value.file.Data.Length;
                        map.Remove(key);
                    }
                    var cap = capacity();
                    if (file.Data.Length > cap) return;
                    map[key] = order.AddFirst((key, file));
                    size += file.Data.Length;
                    while (size > cap && order.Last != null)
                    {
                        var last = order.Last;
                        order.RemoveLast();
                        map.Remove(last.Value.key);
                        size -= last.Value.file.Data.Length;
                    }
                }
            }

            public void Clear()
            {
                lock (map)
                {
                    map.Clear();
                    order.Clear();
                    size = 0;
                }
            }
        }

        /// <summary>Downloaded files on disk (one file per key, named by its SHA-256), oldest used dropped first.</summary>
        sealed class DiskCache
        {
            readonly string directory;
            readonly Func<long> capacity;

            public DiskCache(string directory, Func<long> capacity)
            {
                this.directory = directory;
                this.capacity = capacity;
            }

            string PathOf(string key)
            {
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return System.IO.Path.Combine(directory, string.Concat(hash.Select(b => b.ToString("x2"))) + ".bin");
            }

            public byte[] Read(string key)
            {
                if (capacity() == 0) return null;
                try
                {
                    var path = PathOf(key);
                    if (!File.Exists(path)) return null;
                    var data = File.ReadAllBytes(path);
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    return data;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    return null;
                }
            }

            public void Write(string key, byte[] data)
            {
                var cap = capacity();
                if (cap == 0 || data.Length > cap) return;
                try
                {
                    Directory.CreateDirectory(directory);
                    var path = PathOf(key);
                    var temp = path + ".part";
                    File.WriteAllBytes(temp, data);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temp, path);
                    var files = new DirectoryInfo(directory).GetFiles("*.bin").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                    long total = 0;
                    foreach (var f in files)
                    {
                        total += f.Length;
                        if (total > cap) f.Delete();
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // A full or read-only disk only means no disk cache.
                }
            }

            public void Clear()
            {
                try
                {
                    if (Directory.Exists(directory))
                        foreach (var f in new DirectoryInfo(directory).GetFiles("*.bin")) f.Delete();
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
