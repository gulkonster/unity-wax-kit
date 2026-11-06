#if UNITY_EDITOR_WIN
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UnityWaxKit.Tests
{
    /// <summary>
    /// WebP on Windows through the shipped libwebp 1.6.0. Samples made with libwebp's cwebp / img2webp (license-clean, ours);
    /// expected pixels are hashes of libwebp's own dwebp output (stills) and of the dwebp-decoded frames composed by hand,
    /// which Chrome's ImageDecoder gives too (animation).
    /// </summary>
    public class WebPWindowsTests
    {
        // 24 x 16, VP8 (lossy, opaque).
        static readonly byte[] Lossy = Convert.FromBase64String("UklGRnAAAABXRUJQVlA4IGQAAABwBACdASoYABAAPpE+mkelo6KhMAgAsBIJbACdMoRwN6SAZ35yo1qJRQre8AD++G6psjY+pqNbrGjA/fOTN/7GJH6KFZdYCYR99f7Xj+Nc5kOkb9+LAoY3LMQDkPncqdQnigAA");
        // 24 x 16, VP8X + ALPH + VP8 (lossy with alpha gradient).
        static readonly byte[] Alpha = Convert.FromBase64String("UklGRpAAAABXRUJQVlA4WAoAAAAQAAAAFwAADwAAQUxQSBIAAAAFDzALERFCLJj4S7c/goj+R8xWUDggWAAAAJADAJ0BKhgAEAA+bSySRaQioZgEAEAGxLQATpdAJNmAsznePewA/u8scxzGIKkluWy1YJMBKuKT/iX7y//5mHH3Lc//Vter/nE5ryLZLQv3urMeSY4AAAA=");
        // 24 x 16, VP8L (lossless with alpha).
        static readonly byte[] Lossless = Convert.FromBase64String("UklGRjwAAABXRUJQVlA4TDAAAAAvF8ADEM2VIaL/sYgY8P5HAiJtmzq4f8eDh4MsEEjC2d9sAyFBCjE7AcjGMcygvhM=");
        // 20 x 12, 3 frames (100 / 250 / 100 ms, loop count 2): full frame disposed to background, a 20 x 6 strip blended, a full frame.
        static readonly byte[] Animated = Convert.FromBase64String("UklGRnABAABXRUJQVlA4WAoAAAACAAAAEwAACwAAQU5JTQYAAAD/////AgBBTk1GbgAAAAAAAAAAABMAAAsAAGQAAANWUDggVgAAAJAEAJ0BKhQADAA+bSqRRaQioZgEAEAGxKAJ0cgD7AK4A/gGUAfoABatZeloAAD+4vbUX4orP+A/+/yg4v/nr3iHCQj/9tDdlCjX//7Z9djlIQtPAAAAQU5NRlAAAAAAAAAAAAATAAAFAAD6AAAAVlA4IDgAAAD0AgCdASoUAAYAPm0skkWDAIAAANiUAXZdAPwAAZPkAAD7jv/zYFc1z7/f/00jxpHjSPlP2sAAAEFOTUZ2AAAAAAAAAAAAEwAACwAAZAAAAFZQOCBeAAAAlAMAnQEqFAAMAD5tHJlJgYAAANiWwAnTKEcDekgGEF+CVWTNgAD+/Guv3RhUFgHxgBbOLI5hzKbsVJydsQsp6+Sgdj/afVMjXbVqN12NxFVhNhjJ+Vkp8DgYyQHEAA==");

        SynchronizationContext previousContext;

        [SetUp]
        public void SetUp()
        {
            previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown() => SynchronizationContext.SetSynchronizationContext(previousContext);

        static WaxMediaLimits Limits(int maxSide = 4096, int maxFrames = 500, long maxBytes = 128L << 20) =>
            new WaxMediaLimits { MaxWidth = maxSide, MaxHeight = maxSide, MaxFrames = maxFrames, MaxDecodedBytes = maxBytes };

        static WaxDecodedImage Decode(byte[] data, WaxMediaLimits limits, bool firstFrameOnly = false) =>
            WaxWebPWindows.Decode(data, WaxMediaType.WebP, limits, firstFrameOnly, default).GetAwaiter().GetResult();

        /// <summary>FNV-1a over RGBA with fully transparent pixels as 0,0,0,0 (same as the reference tool).</summary>
        static uint Hash(byte[] rgba)
        {
            var h = 0x811c9dc5u;
            for (var k = 0; k < rgba.Length; k += 4)
            {
                var transparent = rgba[k + 3] == 0;
                for (var j = 0; j < 4; j++)
                {
                    h ^= transparent ? 0u : rgba[k + j];
                    h *= 16777619u;
                }
            }
            return h;
        }

        [Test]
        public void Version_Is160() => Assert.AreEqual("1.6.0", WaxWebPWindows.Version);

        [TestCase("lossy", 0xF4B458D9u)]
        [TestCase("alpha", 0xA6A60264u)]
        [TestCase("lossless", 0x3A605A35u)]
        public void Still_MatchesDwebp(string name, uint expected)
        {
            var data = name == "lossy" ? Lossy : name == "alpha" ? Alpha : Lossless;
            var image = Decode(data, Limits());
            Assert.AreEqual(24, image.Width);
            Assert.AreEqual(16, image.Height);
            Assert.AreEqual(1, image.Frames.Count);
            Assert.AreEqual(expected, Hash(image.Frames[0]), $"0x{Hash(image.Frames[0]):X8}");
        }

        [Test]
        public void Animated_FramesDelaysLoop_MatchReference()
        {
            var image = Decode(Animated, Limits());
            Assert.AreEqual(20, image.Width);
            Assert.AreEqual(12, image.Height);
            CollectionAssert.AreEqual(new[] { 0xB810F18Du, 0x7494AC75u, 0x54C0E1CFu }, image.Frames.Select(Hash).ToArray());
            CollectionAssert.AreEqual(new[] { 100, 250, 100 }, image.DelaysMs);
            Assert.AreEqual(1, image.LoopCount, "WebP loop count 2 (played twice) = repeated once");
            Assert.AreEqual(1, Decode(Animated, Limits(), firstFrameOnly: true).Frames.Count, "static mode: first frame only");
        }

        [Test]
        public void Limits_BeforeDecoding()
        {
            StringAssert.Contains("larger than the limit", Assert.Throws<WaxKitException>(() => Decode(Lossy, Limits(maxSide: 10))).Message);
            StringAssert.Contains("frames", Assert.Throws<WaxKitException>(() => Decode(Animated, Limits(maxFrames: 2))).Message);
            StringAssert.Contains("MaxDecodedBytes", Assert.Throws<WaxKitException>(() => Decode(Animated, Limits(maxBytes: 2000))).Message);

            // A VP8X header claiming a 30000 x 30000 canvas: refused from the header, libwebp never decodes it.
            var huge = (byte[])Animated.Clone();
            huge[24] = 0x2F; huge[25] = 0x75; huge[26] = 0; huge[27] = 0x2F; huge[28] = 0x75; huge[29] = 0;
            Assert.AreEqual((30000, 30000), WaxMediaFormat.ReadSize(huge, WaxMediaType.WebP));
            Assert.Throws<WaxKitException>(() => Decode(huge, Limits()));
        }

        [Test]
        public void Broken_IsAClearError()
        {
            foreach (var cut in new[] { 20, 40, Lossy.Length - 10 })
            {
                var e = Assert.Throws<WaxKitException>(() => Decode(Lossy.Take(cut).ToArray(), Limits()), $"lossy cut at {cut}");
                Assert.AreEqual(WaxErrorKind.NotSupported, e.Kind);
            }
            Assert.Throws<WaxKitException>(() => Decode(Animated.Take(60).ToArray(), Limits()), "animation cut in its first frame");
            var garbage = (byte[])Lossless.Clone();
            for (var i = 21; i < garbage.Length; i++) garbage[i] = 0xFF;
            Assert.Throws<WaxKitException>(() => Decode(garbage, Limits()));
        }

        [Test]
        public void Loader_UsesLibwebpOnWindows()
        {
            var settings = new WaxMediaSettings();
            Task<WaxMediaResponse> Fetch(WaxMediaRequest r, CancellationToken c) => Task.FromResult(new WaxMediaResponse { Status = 200, Data = Animated });
            var loader = new WaxMediaLoader(settings, Fetch, WaxMediaPlatformDecoder.Decoder, "Windows", null);
            var loaded = loader.LoadImage("QmYm1FG7LxhF3mFUaVmVEVqRztEmByVbHwL6ZWXwVY2dvb", WaxMediaMode.Animated, default).GetAwaiter().GetResult();
            Assert.AreEqual(3, loaded.Decoded.Frames.Count);
            var textures = WaxNftMediaClient.ToTextures(loaded);
            Assert.IsTrue(textures.IsAnimated);
            textures.Dispose();
        }
    }
}
#endif
