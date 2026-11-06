using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityWaxKit.Platform;

namespace UnityWaxKit.Tests
{
    /// <summary>
    /// WaxNftMedia video: start = prepared and first frame, the start time limit, player errors before and after the start,
    /// and stale events of a replaced / cleared / disabled video. No real video: the asset read and the video probe are faked,
    /// the player is never prepared, and the tests raise the player's events themselves.
    /// </summary>
    public class NftMediaVideoTests
    {
        const string AssetId = "1099500000001";
        static readonly FieldInfo NftMediaField = typeof(WaxKit).GetField("nftMedia", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly PropertyInfo NetworkProperty = typeof(WaxKit).GetProperty(nameof(WaxKit.Network));
        static readonly byte[] Mp4Head = { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0 };

        IWaxPlatform previousPlatform;
        WaxNetwork previousNetwork;
        object previousClient;
        Action<VideoPlayer> previousPrepare, previousPlay;
        int previousTimeout;
        GameObject go;
        WaxNftMedia media;
        int loaded, failed;
        readonly List<string> events = new List<string>();

        [SetUp]
        public void SetUp()
        {
            previousPlatform = WaxPlatform.IsSet ? WaxPlatform.Current : null;
            previousNetwork = WaxKit.Network;
            previousClient = NftMediaField.GetValue(null);
            previousPrepare = WaxNftMedia.PrepareVideo;
            previousPlay = WaxNftMedia.PlayPrepared;
            previousTimeout = WaxKit.MediaSettings.VideoStartTimeoutSeconds;

            WaxPlatform.Current = new AssetPlatform();
            NetworkProperty.SetValue(null, WaxNetwork.Testnet);
            var loader = new WaxMediaLoader(WaxKit.MediaSettings, (request, cancellation) =>
                Task.FromResult(new WaxMediaResponse { Status = 200, Data = Mp4Head }), null, "Windows", null);
            NftMediaField.SetValue(null, new WaxNftMediaClient(WaxKit.MediaSettings, loader));
            WaxNftMedia.PrepareVideo = p => { };
            WaxNftMedia.PlayPrepared = p => { };

            go = new GameObject("nft video test");
            go.AddComponent<RawImage>();
            media = go.AddComponent<WaxNftMedia>();
            media.Mode = WaxNftMedia.ModeChoice.Animated;
            loaded = failed = 0;
            events.Clear();
            media.OnLoaded.AddListener(() => { loaded++; events.Add("loaded"); });
            media.OnFailed.AddListener(_ => { failed++; events.Add("failed"); });
        }

        [TearDown]
        public void TearDown()
        {
            // Edit mode calls no OnDestroy: run it, so a pending load is cancelled before the object goes.
            if (media != null) Invoke(media, "OnDestroy");
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
            WaxNftMedia.PrepareVideo = previousPrepare;
            WaxNftMedia.PlayPrepared = previousPlay;
            WaxKit.MediaSettings.VideoStartTimeoutSeconds = previousTimeout;
            NftMediaField.SetValue(null, previousClient);
            NetworkProperty.SetValue(null, previousNetwork);
            WaxPlatform.Current = previousPlatform;
        }

        static void Invoke(WaxNftMedia target, string method) =>
            typeof(WaxNftMedia).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);

        static IEnumerator Until(Func<bool> condition, double seconds = 5)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) yield return null;
        }

        static IEnumerator Frames(int count)
        {
            for (var i = 0; i < count; i++) yield return null;
        }

        VideoPlayer Player => go.GetComponent<VideoPlayer>();
        Texture Shown => go.GetComponent<RawImage>().texture;

        // Starts a load and waits until its video player exists (asset read and video probe done).
        IEnumerator ShowVideo()
        {
            _ = media.Show(AssetId);
            yield return Until(() => Player != null);
            Assert.IsNotNull(Player, "the video player was not created");
        }

        [UnityTest]
        public IEnumerator Loaded_OnlyAfterPreparedAndFirstFrame()
        {
            yield return ShowVideo();
            var player = Player;
            media.OnVideoPrepared(player);
            yield return Frames(5);
            Assert.AreEqual(0, loaded, "prepared alone is not the start");
            Assert.IsTrue(media.IsLoading);
            Assert.IsNull(Shown, "no video texture before the first frame");

            media.OnVideoFrame(player, 0);
            yield return Until(() => !media.IsLoading);
            Assert.AreEqual(1, loaded);
            Assert.AreEqual(0, failed);
            Assert.IsNull(media.Error);
            Assert.IsInstanceOf<RenderTexture>(Shown);
        }

        [UnityTest]
        public IEnumerator ErrorBeforeStart_FailsOnce_ReleasesPlayer()
        {
            yield return ShowVideo();
            var player = Player;
            media.OnVideoError(player, "boom");
            media.OnVideoError(player, "boom again");
            yield return Until(() => !media.IsLoading);
            Assert.AreEqual(1, failed);
            Assert.AreEqual(0, loaded);
            Assert.AreEqual("The video cannot be played: boom", media.Error);
            Assert.IsNull(Shown, "placeholder (none set)");
            Assert.IsTrue(Player == null, "video player released");
        }

        [UnityTest]
        public IEnumerator ErrorAfterStart_FailsOnce_ReleasesPlayer()
        {
            yield return ShowVideo();
            var player = Player;
            media.OnVideoPrepared(player);
            media.OnVideoFrame(player, 0);
            yield return Until(() => !media.IsLoading);
            Assert.AreEqual(1, loaded);

            media.OnVideoError(player, "decoder lost");
            media.OnVideoError(player, "decoder lost again");
            yield return Frames(3);
            Assert.AreEqual(1, failed);
            Assert.AreEqual(1, loaded);
            Assert.AreEqual("The video cannot be played: decoder lost", media.Error);
            Assert.IsNull(Shown);
            Assert.IsTrue(Player == null, "video player released");
        }

        [UnityTest]
        public IEnumerator ErrorInSameFrameAsFirstFrame_EndsFailed()
        {
            yield return ShowVideo();
            var player = Player;
            media.OnVideoPrepared(player);
            media.OnVideoFrame(player, 0);
            media.OnVideoError(player, "lost"); // same frame as the first frame
            yield return Frames(5);
            // The first frame may finish the load at once (On Loaded), then the error fails it; never On Loaded after On Failed.
            Assert.AreEqual(1, failed);
            Assert.AreEqual("failed", events[events.Count - 1], string.Join(", ", events));
            Assert.AreEqual("The video cannot be played: lost", media.Error);
            Assert.IsFalse(media.IsLoading);
        }

        [UnityTest]
        public IEnumerator NotStartedInTime_Fails()
        {
            WaxKit.MediaSettings.VideoStartTimeoutSeconds = 1;
            yield return ShowVideo();
            yield return Until(() => !media.IsLoading);
            Assert.AreEqual(1, failed);
            Assert.AreEqual(0, loaded);
            Assert.AreEqual("The video did not start in 1 s.", media.Error);
            Assert.IsTrue(Player == null, "video player released");
        }

        [UnityTest]
        public IEnumerator PreparedButNoFrame_Fails()
        {
            WaxKit.MediaSettings.VideoStartTimeoutSeconds = 1;
            yield return ShowVideo();
            media.OnVideoPrepared(Player);
            yield return Until(() => !media.IsLoading);
            Assert.AreEqual(1, failed);
            Assert.AreEqual("The video did not start in 1 s.", media.Error);
            Assert.IsTrue(Player == null, "video player and its texture released");
            Assert.IsNull(Shown);
        }

        [UnityTest]
        public IEnumerator Replaced_OldPlayerEventsChangeNothing()
        {
            yield return ShowVideo();
            var old = Player;
            _ = media.Show(AssetId);
            yield return Until(() => Player != null && !ReferenceEquals(Player, old));
            var current = Player;
            Assert.IsFalse(ReferenceEquals(old, current));

            media.OnVideoPrepared(old);
            media.OnVideoFrame(old, 0);
            media.OnVideoError(old, "late");
            yield return Frames(5);
            Assert.AreEqual(0, failed);
            Assert.AreEqual(0, loaded);
            Assert.IsNull(media.Error);
            Assert.IsNull(Shown, "no texture change from the old video");
            Assert.IsTrue(media.IsLoading, "the new load still waits for its own start");

            media.OnVideoPrepared(current);
            media.OnVideoFrame(current, 0);
            yield return Until(() => !media.IsLoading);
            Assert.AreEqual(1, loaded);
            Assert.AreEqual(0, failed);
        }

        [UnityTest]
        public IEnumerator Cleared_LateEventsAndTimeoutChangeNothing()
        {
            WaxKit.MediaSettings.VideoStartTimeoutSeconds = 1;
            yield return ShowVideo();
            var old = Player;
            media.Clear();
            Assert.IsTrue(Player == null, "video player released by Clear");
            media.OnVideoFrame(old, 0);
            media.OnVideoError(old, "late");
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 2) yield return null; // past the 1 s limit
            Assert.AreEqual(0, failed);
            Assert.AreEqual(0, loaded);
            Assert.IsNull(media.Error);
            Assert.IsNull(Shown);
        }

        [UnityTest]
        public IEnumerator Disabled_NoOnFailed()
        {
            WaxKit.MediaSettings.VideoStartTimeoutSeconds = 1;
            yield return ShowVideo();
            var player = Player;
            Invoke(media, "OnDisable"); // what Unity calls on disable (edit mode does not): the load is cancelled
            media.OnVideoError(player, "late");
            media.OnVideoFrame(player, 0);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 2) yield return null;
            Assert.AreEqual(0, failed);
            Assert.AreEqual(0, loaded);
            Assert.IsNull(media.Error);
            Assert.IsFalse(media.IsLoading);
        }

        static readonly FieldInfo PlaceholderField = typeof(WaxNftMedia).GetField("placeholder", BindingFlags.Instance | BindingFlags.NonPublic);

        int AssetReads => ((AssetPlatform)WaxPlatform.Current).AssetReads;

        // Shows a video and completes its start (prepared + first frame).
        IEnumerator StartedVideo()
        {
            yield return ShowVideo();
            var player = Player;
            media.OnVideoPrepared(player);
            media.OnVideoFrame(player, 0);
            yield return Until(() => !media.IsLoading);
            Assert.AreEqual(1, loaded);
        }

        Texture2D SetPlaceholder()
        {
            var texture = new Texture2D(2, 2) { name = "placeholder" };
            PlaceholderField.SetValue(media, texture);
            return texture;
        }

        [UnityTest]
        public IEnumerator StartedThenDisabled_LateErrorChangesNothing()
        {
            var placeholder = SetPlaceholder();
            yield return StartedVideo();
            var old = Player;
            Invoke(media, "OnDisable");
            Assert.IsTrue(Player == null, "video player released on disable");
            Assert.AreSame(placeholder, Shown, "placeholder shown on disable");

            media.OnVideoError(old, "late");
            media.OnVideoFrame(old, 1);
            yield return Frames(5);
            Assert.AreSame(placeholder, Shown);
            Assert.IsNull(media.Error);
            Assert.AreEqual(0, failed);
            Assert.AreEqual(1, loaded);
            UnityEngine.Object.DestroyImmediate(placeholder);
        }

        [UnityTest]
        public IEnumerator StartedDisabledEnabled_OldPlayerEventsDoNotTouchNewLoad()
        {
            var placeholder = SetPlaceholder();
            yield return StartedVideo();
            var old = Player;
            Assert.AreEqual(1, AssetReads);
            Invoke(media, "OnDisable");
            Invoke(media, "OnEnable");
            yield return Until(() => Player != null);
            var current = Player;
            Assert.IsNotNull(current, "enable started a new load with a new player");
            Assert.IsFalse(ReferenceEquals(old, current));
            Assert.AreEqual(2, AssetReads, "one full new load");

            // Late events of the old player while the new load waits for its start.
            media.OnVideoError(old, "late");
            media.OnVideoPrepared(old);
            media.OnVideoFrame(old, 1);
            yield return Frames(5);
            Assert.IsNull(media.Error);
            Assert.AreEqual(0, failed);
            Assert.AreSame(placeholder, Shown, "the old video does not replace the placeholder");
            Assert.IsTrue(media.IsLoading, "the new load still waits for its own start");

            media.OnVideoPrepared(current);
            media.OnVideoFrame(current, 0);
            yield return Until(() => !media.IsLoading);
            Assert.AreEqual(2, loaded);
            var shown = Shown;
            Assert.IsInstanceOf<RenderTexture>(shown);
            Assert.AreSame(current.targetTexture, shown);

            // And after the new start.
            media.OnVideoError(old, "later");
            media.OnVideoFrame(old, 2);
            yield return Frames(5);
            Assert.IsNull(media.Error);
            Assert.AreEqual(0, failed);
            Assert.AreSame(shown, Shown, "the new texture stays");
            Assert.IsTrue(ReferenceEquals(current, Player), "the new player stays");
            UnityEngine.Object.DestroyImmediate(placeholder);
        }

        [UnityTest]
        public IEnumerator EnableAndStartInSameFrame_OneLoad()
        {
            // Show right after AddComponent (before Unity's Start), disable, then enable and Start in the same frame.
            yield return ShowVideo();
            var old = Player;
            Assert.AreEqual(1, AssetReads);
            Invoke(media, "OnDisable");
            Invoke(media, "OnEnable");
            Invoke(media, "Start");
            yield return Until(() => Player != null);
            yield return Frames(5);
            Assert.AreEqual(2, AssetReads, "exactly one new load");
            Assert.IsFalse(ReferenceEquals(old, Player));
            Assert.AreEqual(1, go.GetComponents<VideoPlayer>().Length, "one player");
            Assert.IsTrue(media.IsLoading);

            media.OnVideoPrepared(Player);
            media.OnVideoFrame(Player, 0);
            yield return Until(() => !media.IsLoading);
            Assert.AreEqual(1, loaded);
            Assert.AreEqual(0, failed);
        }

        /// <summary>Answers the asset read with one NFT whose data has only a video (https URL).</summary>
        sealed class AssetPlatform : IWaxPlatform
        {
            const string Asset = "{\"success\":true,\"data\":{\"asset_id\":\"" + AssetId + "\",\"collection\":{\"collection_name\":\"mygame\"}," +
                                 "\"schema\":{\"schema_name\":\"heroes\"},\"data\":{\"video\":\"https://video.example/hero.mp4\"}}}";

            public int AssetReads;

            public Task<WaxHttpResponse> Http(string method, string url, byte[] body, string contentType, IDictionary<string, string> headers,
                int timeoutSeconds, CancellationToken cancellation)
            {
                if (!url.Contains("/atomicassets/v1/assets/" + AssetId))
                    return Task.FromResult(new WaxHttpResponse { Status = 0, Error = "no recorded answer for " + url });
                AssetReads++;
                return Task.FromResult(new WaxHttpResponse { Status = 200, Text = Asset });
            }

            public Task Delay(int milliseconds, CancellationToken cancellation) => Task.CompletedTask;
            public IWaxSocket CreateSocket() => throw new NotSupportedException();
            public string Load(string key) => null;
            public void Save(string key, string value) { }
            public void Delete(string key) { }
            public bool IsMobile => false;
            public bool PopupNeedsGesture => false;
            public string ReturnUrl => null;
            public string UserAgent => "test";
            public bool? CanOpenScheme(string scheme) => null;
            public void OpenLink(string url) { }
            public void ReturnToGame() { }
            public void BeginWalletRequest() { }
            public void EndWalletRequest() { }
            public Task WaitForPlayerBack(int stayMilliseconds, CancellationToken cancellation) => Task.CompletedTask;
            public void LetWalletAppComeToFront(string scheme, bool force) { }
            public bool BringWalletAppToFront(string scheme) => false;
            public IWaxPopupFlow OpenWalletPopup(string url, JObject post, WaxPopupProtocol protocol) => throw new NotSupportedException();
        }
    }
}
