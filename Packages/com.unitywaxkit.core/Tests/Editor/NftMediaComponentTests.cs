using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityWaxKit.Platform;

namespace UnityWaxKit.Tests
{
    /// <summary>WaxNftMedia starts one load: Show / ShowTemplate right after AddComponent, or Start alone (no network).</summary>
    public class NftMediaComponentTests
    {
        static readonly PropertyInfo NetworkProperty = typeof(WaxKit).GetProperty(nameof(WaxKit.Network));
        static readonly MethodInfo StartMethod = typeof(WaxNftMedia).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic);

        CountingPlatform platform;
        IWaxPlatform previousPlatform;
        WaxNetwork previousNetwork;
        SynchronizationContext previousContext;
        GameObject go;

        [SetUp]
        public void SetUp()
        {
            previousPlatform = WaxPlatform.IsSet ? WaxPlatform.Current : null;
            previousNetwork = WaxKit.Network;
            previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            platform = new CountingPlatform();
            WaxPlatform.Current = platform;
            NetworkProperty.SetValue(null, WaxNetwork.Testnet);
            go = new GameObject("nft media test");
        }

        [TearDown]
        public void TearDown()
        {
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
            NetworkProperty.SetValue(null, previousNetwork);
            WaxPlatform.Current = previousPlatform;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        // Unity's order: AddComponent (Awake), the caller's code in the same frame, then Start before the next Update.
        WaxNftMedia Add()
        {
            go.AddComponent<UnityEngine.UI.RawImage>();
            return go.AddComponent<WaxNftMedia>();
        }

        static void UnityStart(WaxNftMedia media) => StartMethod.Invoke(media, null);

        int Loads => platform.Requests.Count(u => u.Contains("/atomicassets/v1/"));

        [Test]
        public void ShowInSameFrameAsAddComponent_StartDoesNotLoadAgain()
        {
            var media = Add();
            _ = media.Show("1099512167123");
            UnityStart(media);
            Assert.AreEqual(1, Loads, string.Join("\n", platform.Requests));
            StringAssert.Contains("/atomicassets/v1/assets/1099512167123", platform.Requests[0]);
            Assert.IsTrue(media.IsLoading);
        }

        [Test]
        public void ShowTemplateInSameFrameAsAddComponent_StartDoesNotLoadAgain()
        {
            var media = Add();
            _ = media.ShowTemplate("123456", "mycollection1");
            UnityStart(media);
            Assert.AreEqual(1, Loads, string.Join("\n", platform.Requests));
            StringAssert.Contains("/atomicassets/v1/templates/mycollection1/123456", platform.Requests[0]);
        }

        [Test]
        public void SetUpInInspector_NoShow_StartLoadsOnce()
        {
            var media = Add();
            media.AssetId = "1099512167123"; // the serialized field, as the Inspector sets it
            Assert.AreEqual(0, Loads, "nothing loads before Start");
            UnityStart(media);
            Assert.AreEqual(1, Loads, string.Join("\n", platform.Requests));
            Assert.IsTrue(media.IsLoading);
        }

        /// <summary>Records every request and never answers, so each started load stays at its first read.</summary>
        sealed class CountingPlatform : IWaxPlatform
        {
            public readonly List<string> Requests = new List<string>();
            readonly TaskCompletionSource<WaxHttpResponse> never = new TaskCompletionSource<WaxHttpResponse>();

            public Task<WaxHttpResponse> Http(string method, string url, byte[] body, string contentType, IDictionary<string, string> headers,
                int timeoutSeconds, CancellationToken cancellation)
            {
                Requests.Add(url);
                return never.Task;
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
