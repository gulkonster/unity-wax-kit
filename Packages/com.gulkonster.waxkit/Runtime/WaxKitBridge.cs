using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Scripting;

namespace Gulkonster.WaxKit
{
    /// <summary>
    /// Hidden GameObject that receives JS results and wallet UI events via SendMessage.
    /// JS sends: { "id": 1, "ok": true, "data": ... } or { "id": 1, "ok": false, "error": { "kind": "...", "message": "..." } }.
    /// </summary>
    internal sealed class WaxKitBridge : MonoBehaviour
    {
        public const string ObjectName = "WaxKitBridge";

        static WaxKitBridge instance;

        readonly Dictionary<int, TaskCompletionSource<JToken>> pending = new Dictionary<int, TaskCompletionSource<JToken>>();
        int nextId;

        /// <summary>Wallet UI events from JS: { "type": "show", ... } or { "type": "close" }.</summary>
        public Action<JObject> UiEvent;

        public static WaxKitBridge Instance
        {
            get
            {
                if (instance == null)
                {
                    var go = new GameObject(ObjectName) { hideFlags = HideFlags.HideInHierarchy };
                    DontDestroyOnLoad(go);
                    instance = go.AddComponent<WaxKitBridge>();
                }
                return instance;
            }
        }

        public int Begin(out Task<JToken> result)
        {
            var id = ++nextId;
            var source = new TaskCompletionSource<JToken>();
            pending[id] = source;
            result = source.Task;
            return id;
        }

        [Preserve]
        public void OnResult(string json)
        {
            var message = JObject.Parse(json);
            var id = (int)message["id"];
            if (!pending.Remove(id, out var source)) return;

            if ((bool)message["ok"])
            {
                source.SetResult(message["data"]);
                return;
            }

            var error = message["error"];
            if (!Enum.TryParse((string)error?["kind"], out WaxErrorKind kind)) kind = WaxErrorKind.Wallet;
            source.SetException(new WaxKitException(kind, (string)error?["message"] ?? "Unknown wallet error"));
        }

        [Preserve]
        public void OnUiEvent(string json) => UiEvent?.Invoke(JObject.Parse(json));
    }
}
