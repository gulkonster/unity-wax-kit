using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityWaxKit.Tests
{
    /// <summary>NFT media: references, kinds, header limits, the GIF decoder, gateways, caches and textures (no network).</summary>
    public class MediaTests
    {
        const string Cid = "QmYm1FG7LxhF3mFUaVmVEVqRztEmByVbHwL6ZWXwVY2dvb";
        const string CidV1 = "bafybeie22id3kuwane4dcg2j3m33mq5eevse2gty6em7n3kgkejpwg2hjq";

        // Lossless WebP samples (32 x 24, red / blue patterns; made by LocalTests/nft-media/gen.js, checked with Windows' WebP decoder).
        static readonly byte[] WebPStatic = Convert.FromBase64String("UklGRtoAAABXRUJQVlA4TM0AAAAvH8AFAIgD/B/gv/9RpVqlWqVapVqlWqVapVqlWqVapVqlWqVapVqlWqVapapapVqlWqVapVqlWqVapVqlWqVapVqlWqVapVqlWqVaVaVapVqlWqVapVqlWqVapVqlWqVapVqlWqVapVqlWqWqWqVapVqlWqVapVqlWqVapVqlWqVapVqlWqVapVqlWlWlWqVapVqlWqVapVqlWqVapVqlWqVapVqlWqVapVqlqlqlWqVapVqlWqVapVqlWqVapVqlWqVapVqlWqVapVoFAA==");
        static readonly byte[] WebPAnimatedHeader = Convert.FromBase64String("UklGRu4CAABXRUJQVlA4WAoAAAACAAAAHwAAFwAAQU5JTQYAAAAAAAAAAAA=");

        SynchronizationContext previousContext;

        [SetUp]
        public void SetUp()
        {
            previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown() => SynchronizationContext.SetSynchronizationContext(previousContext);

        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        static byte[] Png(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var bytes = t.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(t);
            return bytes;
        }

        static byte[] Jpg(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGB24, false);
            var bytes = t.EncodeToJPG();
            UnityEngine.Object.DestroyImmediate(t);
            return bytes;
        }

        static readonly byte[] Mp4Head = { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0 };
        static readonly byte[] WebMHead = { 0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01 };

        // ---- Kinds and header sizes ----

        [Test]
        public void Detect_FromFirstBytes()
        {
            Assert.AreEqual(WaxMediaType.Png, WaxMediaFormat.Detect(Png(4, 3)));
            Assert.AreEqual(WaxMediaType.Jpeg, WaxMediaFormat.Detect(Jpg(4, 3)));
            Assert.AreEqual(WaxMediaType.Gif, WaxMediaFormat.Detect(new GifBuilder(2, 2).Frame(new byte[4]).Build()));
            Assert.AreEqual(WaxMediaType.WebP, WaxMediaFormat.Detect(WebPStatic));
            Assert.AreEqual(WaxMediaType.Mp4, WaxMediaFormat.Detect(Mp4Head));
            Assert.AreEqual(WaxMediaType.WebM, WaxMediaFormat.Detect(WebMHead));
            foreach (var text in new[] { "<!DOCTYPE html><html></html>", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script/></svg>", "<?xml version=\"1.0\"?>", "This IPFS gateway is switching", "" })
                Assert.AreEqual(WaxMediaType.Unknown, WaxMediaFormat.Detect(System.Text.Encoding.UTF8.GetBytes(text)), text);
            Assert.IsFalse(WaxMediaFormat.IsAnimatedWebP(WebPStatic));
            Assert.IsTrue(WaxMediaFormat.IsAnimatedWebP(WebPAnimatedHeader));
        }

        [Test]
        public void ReadSize_FromHeader()
        {
            Assert.AreEqual((64, 48), WaxMediaFormat.ReadSize(Png(64, 48), WaxMediaType.Png));
            Assert.AreEqual((40, 30), WaxMediaFormat.ReadSize(Jpg(40, 30), WaxMediaType.Jpeg));
            Assert.AreEqual((7, 5), WaxMediaFormat.ReadSize(new GifBuilder(7, 5).Frame(new byte[35]).Build(), WaxMediaType.Gif));
            Assert.AreEqual((32, 24), WaxMediaFormat.ReadSize(WebPStatic, WaxMediaType.WebP));
            Assert.AreEqual((32, 24), WaxMediaFormat.ReadSize(WebPAnimatedHeader, WaxMediaType.WebP));
        }

        // ---- References ----

        [TestCase(Cid, "")]
        [TestCase(Cid + "/", "")]
        [TestCase(Cid + "/a/card 1.png", "/a/card%201.png")]
        [TestCase("ipfs://" + Cid + "/x.gif", "/x.gif")]
        [TestCase("ipfs://ipfs/" + Cid, "")]
        [TestCase("https://gateway.example.com/ipfs/" + Cid + "/x.gif?filename=y", "/x.gif")]
        [TestCase("https://ipfs.io/ipfs/" + CidV1, "")]
        public void Ipfs_CidAndPathKept(string reference, string path)
        {
            var source = WaxMediaSource.Parse(reference);
            Assert.IsTrue(source.IsIpfs);
            Assert.AreEqual(path, source.Path);
            var urls = source.Urls(new[] { "https://one.example", "https://two.example/prefix" });
            Assert.AreEqual($"https://one.example/ipfs/{source.Cid}{path}", urls[0]);
            Assert.AreEqual($"https://two.example/prefix/ipfs/{source.Cid}{path}", urls[1]);
        }

        [Test]
        public void PlainHttps_IsNeverRewritten()
        {
            var source = WaxMediaSource.Parse("https://cdn.example.com/nft/7.png?v=2");
            Assert.IsFalse(source.IsIpfs);
            CollectionAssert.AreEqual(new[] { "https://cdn.example.com/nft/7.png?v=2" }, source.Urls(new[] { "https://one.example" }));
            // A path that only looks like IPFS (no valid CID) is a plain URL too.
            Assert.IsFalse(WaxMediaSource.Parse("https://cdn.example.com/ipfs/not-a-cid/x.png").IsIpfs);
        }

        [TestCase("http://ipfs.io/ipfs/" + Cid)]
        [TestCase("http://cdn.example.com/a.png")]
        [TestCase("data:image/png;base64,AAAA")]
        [TestCase("ftp://example.com/a.png")]
        [TestCase("javascript:alert(1)")]
        [TestCase(Cid + "/../secret")]
        [TestCase(Cid + "/a%2F..%2Fb")]
        [TestCase("not a cid")]
        [TestCase("")]
        public void Refused(string reference)
        {
            var e = Assert.Throws<WaxKitException>(() => WaxMediaSource.Parse(reference));
            Assert.AreEqual(WaxErrorKind.NotSupported, e.Kind);
        }

        // ---- GIF decoder ----

        static readonly byte[] Palette4 = { 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0 }; // red, green, blue, black

        static WaxMediaLimits Limits(int maxSide = 4096, int maxFrames = 500, long maxBytes = 128L << 20) =>
            new WaxMediaLimits { MaxWidth = maxSide, MaxHeight = maxSide, MaxFrames = maxFrames, MaxDecodedBytes = maxBytes };

        static (int r, int g, int b, int a) Pixel(WaxDecodedImage image, int frame, int x, int y)
        {
            var f = image.Frames[frame];
            var o = (y * image.Width + x) * 4;
            return (f[o], f[o + 1], f[o + 2], f[o + 3]);
        }

        [Test]
        public void Gif_DisposalBackground_ClearsToTransparent()
        {
            var gif = new GifBuilder(4, 4, Palette4).Loop(0)
                .Frame(Enumerable.Repeat((byte)0, 16).ToArray(), disposal: 2, delayCs: 10)
                .Frame(new byte[] { 1, 3 }, x: 1, y: 1, w: 2, h: 1, transparent: 3, delayCs: 0)
                .Build();
            var image = WaxGifDecoder.Decode(gif, Limits(), false);
            Assert.AreEqual(2, image.Frames.Count);
            Assert.AreEqual((255, 0, 0, 255), Pixel(image, 0, 0, 0));
            Assert.AreEqual((0, 0, 0, 0), Pixel(image, 1, 0, 0), "disposal 2 cleared frame 1's area");
            Assert.AreEqual((0, 255, 0, 255), Pixel(image, 1, 1, 1));
            Assert.AreEqual((0, 0, 0, 0), Pixel(image, 1, 2, 1), "transparent index keeps the (cleared) canvas");
            CollectionAssert.AreEqual(new[] { 100, 100 }, image.DelaysMs, "10 cs = 100 ms; 0 cs is shown as 100 ms");
            Assert.AreEqual(0, image.LoopCount);
            Assert.IsFalse(image.Truncated);
        }

        [Test]
        public void Gif_DisposalNone_KeepsPixelsUnderTransparency()
        {
            var gif = new GifBuilder(2, 1, Palette4)
                .Frame(new byte[] { 0, 0 }, disposal: 1, delayCs: 5)
                .Frame(new byte[] { 3, 2 }, transparent: 3)
                .Build();
            var image = WaxGifDecoder.Decode(gif, Limits(), false);
            Assert.AreEqual((255, 0, 0, 255), Pixel(image, 1, 0, 0));
            Assert.AreEqual((0, 0, 255, 255), Pixel(image, 1, 1, 0));
            Assert.AreEqual(50, image.DelaysMs[0]);
            Assert.AreEqual(-1, image.LoopCount, "no NETSCAPE entry: play once");
        }

        [Test]
        public void Gif_DisposalPrevious_RestoresCanvas()
        {
            var gif = new GifBuilder(2, 1, Palette4)
                .Frame(new byte[] { 0, 0 })
                .Frame(new byte[] { 1, 1 }, disposal: 3)
                .Frame(new byte[] { 3, 2 }, transparent: 3)
                .Build();
            var image = WaxGifDecoder.Decode(gif, Limits(), false);
            Assert.AreEqual((0, 255, 0, 255), Pixel(image, 1, 0, 0));
            Assert.AreEqual((255, 0, 0, 255), Pixel(image, 2, 0, 0), "frame 2 was restored to frame 1's canvas");
            Assert.AreEqual((0, 0, 255, 255), Pixel(image, 2, 1, 0));
        }

        [Test]
        public void Gif_Interlaced_SameAsPlain()
        {
            var rng = new System.Random(5);
            var indices = new byte[9 * 11];
            for (var i = 0; i < indices.Length; i++) indices[i] = (byte)rng.Next(4);
            var plain = WaxGifDecoder.Decode(new GifBuilder(9, 11, Palette4).Frame(indices).Build(), Limits(), false);
            var interlaced = WaxGifDecoder.Decode(new GifBuilder(9, 11, Palette4).Frame(indices, interlaced: true).Build(), Limits(), false);
            CollectionAssert.AreEqual(plain.Frames[0], interlaced.Frames[0]);
        }

        [Test]
        public void Gif_LocalPalette_AndOffsetFrameClipped()
        {
            var local = new byte[] { 10, 20, 30, 40, 50, 60 };
            var gif = new GifBuilder(3, 2, Palette4)
                .Frame(new byte[6])
                .Frame(new byte[] { 1, 1, 1, 1 }, x: 2, y: 1, w: 2, h: 2, palette: local)
                .Build();
            var image = WaxGifDecoder.Decode(gif, Limits(), false);
            Assert.AreEqual((40, 50, 60, 255), Pixel(image, 1, 2, 1));
            Assert.AreEqual((255, 0, 0, 255), Pixel(image, 1, 1, 1), "outside the frame: unchanged");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Gif_LongLzw_CodeGrowthAndFullTable(bool deferredClear)
        {
            // 256 colors and noise: codes grow to 12 bits and the table fills (clear, or a deferred clear).
            var palette = new byte[256 * 3];
            for (var i = 0; i < palette.Length; i++) palette[i] = (byte)(i * 7);
            var rng = new System.Random(1);
            var indices = new byte[300 * 200];
            for (var i = 0; i < indices.Length; i++) indices[i] = (byte)(rng.Next(3) == 0 ? rng.Next(256) : i / 600 % 256);
            var gif = new GifBuilder(300, 200, palette) { DeferredClear = deferredClear }.Frame(indices).Build();
            var image = WaxGifDecoder.Decode(gif, Limits(), false);
            for (var i = 0; i < indices.Length; i += 997)
                Assert.AreEqual(palette[indices[i] * 3], image.Frames[0][i * 4], $"pixel {i}");
            Assert.IsFalse(image.Truncated);
        }

        [Test]
        public void Gif_Truncated_KeepsFinishedFrames()
        {
            var gif = new GifBuilder(4, 4, Palette4).Frame(new byte[16]).Frame(Enumerable.Repeat((byte)1, 16).ToArray()).Build();
            var cut = gif.Take(gif.Length - 6).ToArray();
            var image = WaxGifDecoder.Decode(cut, Limits(), false);
            Assert.AreEqual(1, image.Frames.Count);
            Assert.IsTrue(image.Truncated);
            Assert.Throws<WaxKitException>(() => WaxGifDecoder.Decode(gif.Take(20).ToArray(), Limits(), false), "cut before the first frame");
            Assert.Throws<WaxKitException>(() => WaxGifDecoder.Decode(System.Text.Encoding.ASCII.GetBytes("GIF89a<html>"), Limits(), false));
        }

        [Test]
        public void Gif_Limits_BeforeDecoding()
        {
            // A header claiming 60000 x 60000 with no data: refused before any allocation.
            var huge = new GifBuilder(2, 2, Palette4).Frame(new byte[4]).Build();
            huge[6] = 0x60; huge[7] = 0xEA; huge[8] = 0x60; huge[9] = 0xEA;
            StringAssert.Contains("larger than the limit", Assert.Throws<WaxKitException>(() => WaxGifDecoder.Decode(huge, Limits(), false)).Message);

            var three = new GifBuilder(10, 10, Palette4).Frame(new byte[100]).Frame(new byte[100]).Frame(new byte[100]).Build();
            StringAssert.Contains("frames", Assert.Throws<WaxKitException>(() => WaxGifDecoder.Decode(three, Limits(maxFrames: 2), false)).Message);
            StringAssert.Contains("MaxDecodedBytes", Assert.Throws<WaxKitException>(() => WaxGifDecoder.Decode(three, Limits(maxBytes: 1000), false)).Message);
            Assert.AreEqual(1, WaxGifDecoder.Decode(three, Limits(maxFrames: 1), true).Frames.Count, "static mode decodes only the first frame");
        }

        // ---- Loader: gateways, kinds, limits, caches ----

        sealed class FakeFetch
        {
            public readonly List<WaxMediaRequest> Requests = new List<WaxMediaRequest>();
            public Func<WaxMediaRequest, WaxMediaResponse> Answer;
            public TaskCompletionSource<WaxMediaResponse> Pending;

            public Task<WaxMediaResponse> Fetch(WaxMediaRequest request, CancellationToken cancellation)
            {
                Requests.Add(request);
                return Pending != null ? Pending.Task : Task.FromResult(Answer(request));
            }
        }

        static WaxMediaResponse Ok(byte[] data) => new WaxMediaResponse { Status = 200, Data = data };
        static WaxMediaResponse Text(long status, string text) => new WaxMediaResponse { Status = status, Data = System.Text.Encoding.UTF8.GetBytes(text) };

        static WaxMediaLoader Loader(FakeFetch fetch, WaxMediaSettings settings = null, string disk = null, WaxPlatformDecode decode = null) =>
            new WaxMediaLoader(settings ?? new WaxMediaSettings(), fetch.Fetch, decode, "Windows", disk);

        [Test]
        public void Gateways_InOrder_FailoverKeepsPath()
        {
            var settings = new WaxMediaSettings();
            settings.Gateways.Add("https://mine.example/");
            settings.Gateways.Add("http://plain.example"); // not https: skipped
            var png = Png(8, 8);
            var fetch = new FakeFetch
            {
                Answer = r => r.Url.StartsWith("https://mine.example") ? Text(429, "slow down")
                    : r.Url.StartsWith("https://gateway.pinata.cloud") ? Text(200, "<!DOCTYPE html><html>notice</html>")
                    : Ok(png),
            };
            var file = Run(Loader(fetch, settings).Fetch("ipfs://" + Cid + "/cards/1.png", default));
            CollectionAssert.AreEqual(new[]
            {
                $"https://mine.example/ipfs/{Cid}/cards/1.png",
                $"https://gateway.pinata.cloud/ipfs/{Cid}/cards/1.png",
                $"https://ipfs.filebase.io/ipfs/{Cid}/cards/1.png",
            }, fetch.Requests.Select(r => r.Url));
            Assert.AreEqual(WaxMediaType.Png, file.Type);
            Assert.AreEqual(15, fetch.Requests[0].TimeoutSeconds);
            Assert.AreEqual(32L << 20, fetch.Requests[0].MaxBytes);
        }

        [Test]
        public void Gateways_AllFail_NetworkError()
        {
            var fetch = new FakeFetch { Answer = r => new WaxMediaResponse { Status = 0, Error = "timeout" } };
            var e = Assert.Throws<WaxKitException>(() => Run(Loader(fetch).Fetch(Cid, default)));
            Assert.AreEqual(WaxErrorKind.Network, e.Kind);
            Assert.AreEqual(2, fetch.Requests.Count);
        }

        [Test]
        public void PlainHttps_OneRequest_ScriptRefused()
        {
            var fetch = new FakeFetch { Answer = r => Text(200, "<svg><script>alert(1)</script></svg>") };
            var e = Assert.Throws<WaxKitException>(() => Run(Loader(fetch).Fetch("https://cdn.example.com/a.svg", default)));
            Assert.AreEqual(WaxErrorKind.NotSupported, e.Kind);
            CollectionAssert.AreEqual(new[] { "https://cdn.example.com/a.svg" }, fetch.Requests.Select(r => r.Url));
        }

        [Test]
        public void TooLarge_IsAClearError()
        {
            var fetch = new FakeFetch { Answer = r => new WaxMediaResponse { TooLarge = true } };
            StringAssert.Contains("MaxDownloadBytes", Assert.Throws<WaxKitException>(() => Run(Loader(fetch).Fetch(Cid, default))).Message);
            Assert.AreEqual(1, fetch.Requests.Count, "no other gateway: the file itself is too large");
        }

        [Test]
        public void ImageLimits_FromHeader_BeforeDecoding()
        {
            var settings = new WaxMediaSettings { MaxWidth = 32, MaxHeight = 32 };
            var fetch = new FakeFetch { Answer = r => Ok(Png(64, 16)) };
            var e = Assert.Throws<WaxKitException>(() => Run(Loader(fetch, settings).LoadImage(Cid, WaxMediaMode.Animated, default)));
            StringAssert.Contains("64 x 16", e.Message);
        }

        [Test]
        public void WebP_WithoutAPlatformDecoder_IsAClearError()
        {
            var fetch = new FakeFetch { Answer = r => Ok(WebPStatic) };
            var e = Assert.Throws<WaxKitException>(() => Run(new WaxMediaLoader(new WaxMediaSettings(), fetch.Fetch, null, "Linux", null).LoadImage(Cid, WaxMediaMode.Animated, default)));
            Assert.AreEqual("WebP images are not supported on Linux yet.", e.Message);
        }

        [Test]
        public void WebP_UsesThePlatformDecoder()
        {
            var fetch = new FakeFetch { Answer = r => Ok(WebPStatic) };
            bool? firstOnly = null;
            WaxPlatformDecode decode = (data, type, limits, first, c) =>
            {
                firstOnly = first;
                var image = new WaxDecodedImage { Width = 32, Height = 24 };
                image.Frames.Add(new byte[32 * 24 * 4]);
                image.DelaysMs.Add(100);
                return Task.FromResult(image);
            };
            var loaded = Run(Loader(fetch, decode: decode).LoadImage(Cid, WaxMediaMode.Static, default));
            Assert.AreEqual(1, loaded.Decoded.Frames.Count);
            Assert.AreEqual(true, firstOnly);
        }

        [Test]
        public void Video_FirstGatewayWithAVideo()
        {
            var fetch = new FakeFetch { Answer = r => r.Url.Contains("pinata") ? Text(200, "<html>error</html>") : Ok(Mp4Head) };
            var url = Run(Loader(fetch).ResolveVideoUrl(Cid + "/v.mp4", default));
            Assert.AreEqual($"https://ipfs.filebase.io/ipfs/{Cid}/v.mp4", url);
            Assert.AreEqual(63, fetch.Requests[0].RangeEnd);
            Assert.AreEqual(url, Run(Loader(fetch).ResolveVideoUrl(Cid + "/v.mp4", default)), "same answer");

            var image = new FakeFetch { Answer = r => Ok(Png(2, 2)) };
            StringAssert.Contains("not an MP4 or WebM", Assert.Throws<WaxKitException>(() => Run(Loader(image).ResolveVideoUrl(Cid, default))).Message);
        }

        [Test]
        public void Cache_Memory_Disk_AndDedupe()
        {
            var png = Png(4, 4);
            var fetch = new FakeFetch { Answer = r => Ok(png) };
            var dir = Path.Combine(Path.GetTempPath(), "waxkit-media-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var loader = Loader(fetch, disk: dir);
                Run(loader.Fetch(Cid, default));
                Run(loader.Fetch("https://other.example/ipfs/" + Cid, default));
                Assert.AreEqual(1, fetch.Requests.Count, "same CID through another gateway: from memory");

                Run(Loader(fetch, disk: dir).Fetch(Cid, default));
                Assert.AreEqual(1, fetch.Requests.Count, "a new loader reads the disk cache");

                var noCache = new WaxMediaSettings { MemoryCacheBytes = 0, DiskCacheBytes = 0 };
                var plain = Loader(fetch, noCache, dir);
                Run(plain.Fetch(Cid, default));
                Run(plain.Fetch(Cid, default));
                Assert.AreEqual(3, fetch.Requests.Count, "caches off: downloaded each time");

                var pending = new FakeFetch { Pending = new TaskCompletionSource<WaxMediaResponse>() };
                var shared = Loader(pending);
                var a = shared.Fetch(Cid, default);
                var b = shared.Fetch(Cid, default);
                pending.Pending.SetResult(Ok(png));
                Assert.AreSame(Run(a), Run(b));
                Assert.AreEqual(1, pending.Requests.Count, "one download for two calls at once");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Cancelled_StopsTheCall()
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var fetch = new FakeFetch { Answer = r => Ok(Png(2, 2)) };
            Assert.Catch<OperationCanceledException>(() => Run(Loader(fetch).LoadImage(Cid, WaxMediaMode.Animated, cancel.Token)));
            Assert.AreEqual(0, fetch.Requests.Count);
        }

        // ---- Textures ----

        [Test]
        public void Textures_Png_And_StaticGifIsOneFrame()
        {
            var fetch = new FakeFetch();
            var loader = Loader(fetch);
            fetch.Answer = r => Ok(Png(16, 8));
            using (var png = WaxNftMediaClient.ToTextures(Run(loader.LoadImage(Cid, WaxMediaMode.Animated, default))))
            {
                Assert.AreEqual(16, png.Width);
                Assert.AreEqual(8, png.Height);
                Assert.IsFalse(png.IsAnimated);
            }

            var gif = new GifBuilder(3, 2, Palette4).Loop(0).Frame(new byte[6]).Frame(Enumerable.Repeat((byte)2, 6).ToArray()).Build();
            fetch.Answer = r => Ok(gif);
            var animated = WaxNftMediaClient.ToTextures(Run(loader.LoadImage("https://cdn.example.com/a.gif", WaxMediaMode.Animated, default)));
            Assert.AreEqual(2, animated.Frames.Count);
            Assert.IsTrue(animated.IsAnimated);
            Assert.AreEqual(0, animated.LoopCount);
            animated.Dispose();
            Assert.IsTrue(animated.IsDisposed);
            Assert.AreEqual(0, animated.Frames.Count);

            var still = WaxNftMediaClient.ToTextures(Run(loader.LoadImage("https://cdn.example.com/a.gif", WaxMediaMode.Static, default)));
            Assert.AreEqual(1, still.Frames.Count, "static mode never animates");
            still.Dispose();
        }

        [Test]
        public void PlatformResult_IsCheckedAgainstTheLimits()
        {
            byte[] Result(int w, int h, int frames, int extra = 0)
            {
                var data = new byte[16 + frames * 4 + frames * w * h * 4 + extra];
                void Put(int at, int v) { data[at] = (byte)(v >> 24); data[at + 1] = (byte)(v >> 16); data[at + 2] = (byte)(v >> 8); data[at + 3] = (byte)v; }
                Put(0, w); Put(4, h); Put(8, frames); Put(12, 0);
                for (var i = 0; i < frames; i++) Put(16 + i * 4, 80);
                return data;
            }
            var image = WaxMediaPlatformDecoder.ReadResult(Result(3, 2, 2), Limits());
            Assert.AreEqual(2, image.Frames.Count);
            Assert.AreEqual(80, image.DelaysMs[1]);
            Assert.Throws<WaxKitException>(() => WaxMediaPlatformDecoder.ReadResult(Result(3, 2, 2, extra: 1), Limits()));
            Assert.Throws<WaxKitException>(() => WaxMediaPlatformDecoder.ReadResult(Result(100, 2, 1), Limits(maxSide: 50)));
            Assert.Throws<WaxKitException>(() => WaxMediaPlatformDecoder.ReadResult(Result(3, 2, 3), Limits(maxFrames: 2)));
        }

        // ---- Fields ----

        [Test]
        public void Fields_GlobalCollectionSchema()
        {
            var settings = new WaxMediaSettings();
            settings.SetFields("mygame", null, "image", "clip");
            settings.SetFields("mygame", "heroes", null, null, "poster");
            var data = JObject.Parse("{\"name\":\"Hero\",\"img\":\"" + Cid + "\",\"image\":\"QmImage\",\"clip\":\"ipfs://clip\",\"poster\":\"QmPoster\",\"video\":\"\"}");

            var plain = WaxNftMediaData.Read(settings, data, "other", "x");
            Assert.AreEqual(Cid, plain.Image);
            Assert.IsNull(plain.Video, "an empty field is no video");
            Assert.AreEqual("Hero", plain.Name);

            var collection = WaxNftMediaData.Read(settings, data, "mygame", "items");
            Assert.AreEqual("QmImage", collection.Image);
            Assert.AreEqual("ipfs://clip", collection.Video);
            Assert.AreEqual(Cid, collection.Cover);

            var schema = WaxNftMediaData.Read(settings, data, "mygame", "heroes");
            Assert.AreEqual("QmImage", schema.Image);
            Assert.AreEqual("QmPoster", schema.Cover);
            CollectionAssert.AreEqual(new[] { "QmPoster", "QmImage" }, WaxNftMediaData.StillReferences(schema).ToArray());

            Assert.Throws<ArgumentException>(() => settings.SetFields("Bad Name", null, "img"));
            Assert.Throws<ArgumentException>(() => settings.ImageField = " ");
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.MaxWidth = 0);
            Assert.IsNull(WaxMediaSettings.CheckGateway("http://x.example"));
            Assert.IsNull(WaxMediaSettings.CheckGateway("https://x.example/?key=1"));
            Assert.AreEqual("https://x.example", WaxMediaSettings.CheckGateway("https://x.example/ipfs/"));
        }

        /// <summary>Writes GIFs for the tests: real LZW (code growth to 12 bits, clear or deferred clear when the table is full).</summary>
        sealed class GifBuilder
        {
            readonly int width, height;
            readonly byte[] palette;
            readonly List<byte> body = new List<byte>();
            int? loop;
            public bool DeferredClear;

            public GifBuilder(int width, int height, byte[] palette = null)
            {
                this.width = width;
                this.height = height;
                this.palette = palette ?? Palette4;
            }

            public GifBuilder Loop(int count)
            {
                loop = count;
                return this;
            }

            public GifBuilder Frame(byte[] indices, int x = 0, int y = 0, int w = -1, int h = -1, int disposal = 0, int delayCs = 0,
                int transparent = -1, byte[] palette = null, bool interlaced = false)
            {
                w = w < 0 ? width : w;
                h = h < 0 ? height : h;
                body.AddRange(new byte[] { 0x21, 0xF9, 4, (byte)(disposal << 2 | (transparent >= 0 ? 1 : 0)), (byte)delayCs, (byte)(delayCs >> 8), (byte)Math.Max(0, transparent), 0 });
                body.Add(0x2C);
                body.AddRange(U16(x)); body.AddRange(U16(y)); body.AddRange(U16(w)); body.AddRange(U16(h));
                body.Add((byte)((palette != null ? 0x80 | TableBits(palette) : 0) | (interlaced ? 0x40 : 0)));
                if (palette != null) body.AddRange(Padded(palette));
                var rows = Enumerable.Range(0, h).ToList();
                if (interlaced) rows = Enumerable.Range(0, h).Where(r => r % 8 == 0).Concat(Enumerable.Range(0, h).Where(r => r % 8 == 4))
                    .Concat(Enumerable.Range(0, h).Where(r => r % 4 == 2)).Concat(Enumerable.Range(0, h).Where(r => r % 2 == 1)).ToList();
                var ordered = rows.SelectMany(r => indices.Skip(r * w).Take(w)).ToArray();
                var bits = TableBits(palette ?? this.palette) + 1;
                var min = Math.Max(2, bits);
                body.Add((byte)min);
                var data = Lzw(ordered, min);
                for (var i = 0; i < data.Count; i += 255)
                {
                    var n = Math.Min(255, data.Count - i);
                    body.Add((byte)n);
                    body.AddRange(data.Skip(i).Take(n));
                }
                body.Add(0);
                return this;
            }

            public byte[] Build()
            {
                var gif = new List<byte>();
                gif.AddRange(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
                gif.AddRange(U16(width)); gif.AddRange(U16(height));
                gif.Add((byte)(0x80 | 0x70 | TableBits(palette)));
                gif.Add(0); gif.Add(0);
                gif.AddRange(Padded(palette));
                if (loop != null)
                {
                    gif.AddRange(new byte[] { 0x21, 0xFF, 11 });
                    gif.AddRange(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
                    gif.AddRange(new byte[] { 3, 1, (byte)loop.Value, (byte)(loop.Value >> 8), 0 });
                }
                gif.AddRange(body);
                gif.Add(0x3B);
                return gif.ToArray();
            }

            static int TableBits(byte[] palette)
            {
                var colors = palette.Length / 3;
                var bits = 0;
                while ((2 << bits) < colors) bits++;
                return bits;
            }

            static byte[] Padded(byte[] palette)
            {
                var size = 3 * (2 << TableBits(palette));
                var padded = new byte[size];
                Array.Copy(palette, padded, palette.Length);
                return padded;
            }

            static byte[] U16(int v) => new[] { (byte)v, (byte)(v >> 8) };

            List<byte> Lzw(byte[] indices, int min)
            {
                var output = new List<byte>();
                int buffer = 0, count = 0;
                int clear = 1 << min, end = clear + 1;
                int codeSize = min + 1, encNext = end + 1, decNext = end + 1;
                var firstAfterClear = true;
                var table = new Dictionary<int, int>();

                void Write(int code)
                {
                    buffer |= code << count;
                    count += codeSize;
                    while (count >= 8) { output.Add((byte)buffer); buffer >>= 8; count -= 8; }
                }
                void Emit(int code)
                {
                    Write(code);
                    if (firstAfterClear) { firstAfterClear = false; return; }
                    if (decNext < 4096)
                    {
                        decNext++;
                        if (decNext == 1 << codeSize && codeSize < 12) codeSize++;
                    }
                }
                void Clear()
                {
                    Write(clear);
                    codeSize = min + 1;
                    encNext = decNext = end + 1;
                    table.Clear();
                    firstAfterClear = true;
                }

                Clear();
                int prefix = indices[0];
                for (var i = 1; i < indices.Length; i++)
                {
                    int k = indices[i], key = prefix << 8 | k;
                    if (table.TryGetValue(key, out var code)) { prefix = code; continue; }
                    Emit(prefix);
                    if (encNext < 4096) table[key] = encNext++;
                    else if (!DeferredClear) Clear();
                    prefix = k;
                }
                Emit(prefix);
                Write(end);
                if (count > 0) output.Add((byte)buffer);
                return output;
            }
        }
    }
}
