using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityWaxKit.Editor
{
    /// <summary>Inspector of <see cref="WaxKitManager"/>: fields that apply only, and help for common setup mistakes.</summary>
    [CustomEditor(typeof(WaxKitManager))]
    internal sealed class WaxKitManagerEditor : UnityEditor.Editor
    {
        [MenuItem("GameObject/Unity WAX Kit/WAX Kit Manager", false, 10)]
        static void Create(MenuCommand command)
        {
            var go = new GameObject("WAX Kit Manager", typeof(WaxKitManager));
            GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create WAX Kit Manager");
            Selection.activeGameObject = go;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            NetworksGUI();

            EditorGUILayout.Space();
            var appName = Field("appName");
            if (string.IsNullOrWhiteSpace(appName.stringValue))
                EditorGUILayout.HelpBox("The app name is empty. Wallets show it to the player, and WaxKit.Initialize needs one.", MessageType.Error);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Wallets", EditorStyles.boldLabel);
            Field("anchor");
            Field("myCloudWallet");
            var webAuthApp = Field("webAuthApp");
            Field("webAuthWeb");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sessions", EditorStyles.boldLabel);
            Field("multipleSessions");
            Field("autoRestore");

            EditorGUILayout.Space();
            var theme = Field("theme");
            if (theme.enumValueIndex == (int)WaxKitManager.ThemeChoice.Custom)
                using (new EditorGUI.IndentLevelScope())
                    Field("customTheme", true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Wait times", EditorStyles.boldLabel);
            Field("anchorLoginTimeoutSeconds");
            Field("myCloudWalletLoginTimeoutSeconds");
            Field("webAuthWebTimeoutSeconds");
            Field("transactionExpirySeconds");

            if (webAuthApp.boolValue)
            {
                EditorGUILayout.Space();
                Field("webAuthRequestAccount");
            }

            EditorGUILayout.Space();
            MarketGUI();

            EditorGUILayout.Space();
            MediaGUI();

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>NFT media: field names, gateways, mode, limits and caches, with checks for wrong input.</summary>
        void MediaGUI()
        {
            EditorGUILayout.LabelField("NFT media", EditorStyles.boldLabel);
            foreach (var name in new[] { "imageField", "videoField", "coverField" })
                if (string.IsNullOrWhiteSpace(Field(name).stringValue))
                    EditorGUILayout.HelpBox($"The {ObjectNames.NicifyVariableName(name)} is empty.", MessageType.Error);

            var overrides = Field("mediaFieldOverrides", true);
            for (var i = 0; i < overrides.arraySize; i++)
            {
                var entry = overrides.GetArrayElementAtIndex(i);
                var collection = (entry.FindPropertyRelative("collection").stringValue ?? "").Trim();
                var schema = (entry.FindPropertyRelative("schema").stringValue ?? "").Trim();
                if (!WaxToken.IsAccountName(collection))
                    EditorGUILayout.HelpBox($"Media field override {i + 1}: \"{collection}\" is not a collection name.", MessageType.Error);
                if (schema.Length > 0 && !WaxToken.IsAccountName(schema))
                    EditorGUILayout.HelpBox($"Media field override {i + 1}: \"{schema}\" is not a schema name.", MessageType.Error);
            }

            var gateways = Field("ipfsGateways", true);
            for (var i = 0; i < gateways.arraySize; i++)
            {
                var url = (gateways.GetArrayElementAtIndex(i).stringValue ?? "").Trim();
                if (url.Length > 0 && WaxMediaSettings.CheckGateway(url) == null)
                    EditorGUILayout.HelpBox($"IPFS gateway \"{url}\" must be an https URL without a query.", MessageType.Error);
                else if (url.Contains("token=") || url.Contains("key="))
                    EditorGUILayout.HelpBox($"IPFS gateway \"{url}\" looks like it holds a key. Everything in a game is public: never ship a secret key.", MessageType.Warning);
            }
            var useDefaults = Field("useDefaultGateways");
            if (gateways.arraySize == 0 && !useDefaults.boolValue)
                EditorGUILayout.HelpBox("No IPFS gateway: add one or turn on the default gateways.", MessageType.Error);
            else if (gateways.arraySize == 0)
                EditorGUILayout.HelpBox("Only the public default gateways are set. You can start with them; they are rate-limited and change, so choose your own gateway when your traffic or uptime needs it.", MessageType.Info);
            Field("gatewayTimeoutSeconds");
            Field("videoStartTimeoutSeconds");
            Field("mediaMode");
            Field("videoAudio");
            Field("firstFrameForVideo");
            Field("maxDownloadMegabytes");
            Field("maxImageWidth");
            Field("maxImageHeight");
            Field("maxFrames");
            Field("maxDecodedMegabytes");
            Field("memoryCacheMegabytes");
            Field("diskCacheMegabytes");
        }

        /// <summary>Prices and balances: Alcor, sources, cache, tracked tokens, with checks for wrong input.</summary>
        void MarketGUI()
        {
            EditorGUILayout.LabelField("Prices and balances", EditorStyles.boldLabel);
            var alcor = Field("alcorEnabled");
            if (alcor.boolValue)
            {
                var hosts = Field("alcorHosts", true);
                for (var i = 0; i < hosts.arraySize; i++)
                {
                    var entry = hosts.GetArrayElementAtIndex(i);
                    var chainId = (entry.FindPropertyRelative("chainId").stringValue ?? "").Trim();
                    var url = (entry.FindPropertyRelative("apiUrl").stringValue ?? "").Trim();
                    if (chainId.Length != 64 || !chainId.All(Uri.IsHexDigit))
                        EditorGUILayout.HelpBox($"Alcor host {i + 1}: the chain id must be 64 hex characters.", MessageType.Error);
                    if (url.Length > 0 && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        EditorGUILayout.HelpBox($"Alcor host {i + 1}: \"{url}\" must start with https://.", MessageType.Error);
                }
                Field("priceSource");
            }
            Field("coreUsdSource");
            Field("oracleMaxAgeMinutes");
            Field("marketCacheSeconds");
            Field("trackNetworkCoin");
            var tokens = Field("trackedTokens", true);
            for (var i = 0; i < tokens.arraySize; i++)
            {
                var entry = tokens.GetArrayElementAtIndex(i);
                var contract = (entry.FindPropertyRelative("contract").stringValue ?? "").Trim();
                var symbol = (entry.FindPropertyRelative("symbol").stringValue ?? "").Trim();
                var poolId = (entry.FindPropertyRelative("poolId").stringValue ?? "").Trim();
                var label = $"Tracked token {i + 1}";
                if (contract.Length == 0)
                    EditorGUILayout.HelpBox($"{label}: the contract is empty.", MessageType.Error);
                else if (!WaxToken.IsAccountName(contract))
                    EditorGUILayout.HelpBox($"{label}: \"{contract}\" is not an account name (1-12 characters a-z, 1-5 and dots).", MessageType.Error);
                if (!WaxToken.IsSymbolCode(symbol))
                    EditorGUILayout.HelpBox($"{label}: \"{symbol}\" is not a symbol (1-7 capital letters, for example TLM).", MessageType.Error);
                if (poolId.Length > 0 && (!poolId.All(char.IsDigit) || !long.TryParse(poolId, out _)))
                    EditorGUILayout.HelpBox($"{label}: the pool id \"{poolId}\" must be a whole number (or empty).", MessageType.Error);
            }
        }

        SerializedProperty Field(string name, bool children = false)
        {
            var property = serializedObject.FindProperty(name);
            EditorGUILayout.PropertyField(property, children);
            return property;
        }

        /// <summary>The Networks list, its Add buttons, the Active network dropdown and the network checks.</summary>
        void NetworksGUI()
        {
            var networks = Field("networks", true);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add WAX Mainnet")) AddNetwork(WaxKitManager.NetworkEntry.From(WaxNetwork.Mainnet));
                if (GUILayout.Button("Add WAX Testnet")) AddNetwork(WaxKitManager.NetworkEntry.From(WaxNetwork.Testnet));
                if (GUILayout.Button("Add custom network")) AddNetwork(new WaxKitManager.NetworkEntry { name = "Custom" });
            }

            var names = new List<string>();
            for (var i = 0; i < networks.arraySize; i++) names.Add(networks.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue);

            var active = serializedObject.FindProperty("activeNetwork");
            var index = names.IndexOf(active.stringValue);
            var options = names.Select((n, i) => string.IsNullOrEmpty(n) ? $"(entry {i + 1} has no name)" : n).ToList();
            if (index < 0) options.Add($"{active.stringValue} (not in the list)");
            var picked = EditorGUILayout.Popup(new GUIContent("Active Network", active.tooltip), index < 0 ? options.Count - 1 : index, options.ToArray());
            if (picked < names.Count) active.stringValue = names[picked];

            if (names.IndexOf(active.stringValue) < 0)
                EditorGUILayout.HelpBox($"The active network \"{active.stringValue}\" is not in the Networks list. Pick one above, " +
                                        "or Awake throws when the game starts.", MessageType.Error);

            var duplicates = names.Where(n => !string.IsNullOrEmpty(n)).GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
                EditorGUILayout.HelpBox($"Two or more networks are named {string.Join(", ", duplicates.Select(d => $"\"{d}\""))}. " +
                                        "Give each a different name: the Active network is picked by name (the first one wins).", MessageType.Error);

            for (var i = 0; i < networks.arraySize; i++)
            {
                var entry = networks.GetArrayElementAtIndex(i);
                var label = string.IsNullOrEmpty(names[i]) ? $"Network {i + 1}" : names[i];
                var chainId = (entry.FindPropertyRelative("chainId").stringValue ?? "").Trim();
                if (chainId.Length != 64 || !chainId.All(Uri.IsHexDigit))
                    EditorGUILayout.HelpBox($"{label}: the chain id must be 64 hex characters (get_info's chain_id).", MessageType.Error);

                var rpc = Endpoints(entry.FindPropertyRelative("rpcEndpoints"));
                if (rpc.Count == 0)
                    EditorGUILayout.HelpBox($"{label}: add at least one RPC endpoint.", MessageType.Error);
                foreach (var url in rpc.Concat(Endpoints(entry.FindPropertyRelative("atomicEndpoints"))))
                    if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        EditorGUILayout.HelpBox($"{label}: \"{url}\" does not start with https://. Anyone on the network path can read and " +
                                                "change plain http answers (balances, table rows, NFTs), and WebGL builds and Android block it. " +
                                                "Use https:// endpoints.", MessageType.Warning);
            }
        }

        static List<string> Endpoints(SerializedProperty list)
        {
            var urls = new List<string>();
            for (var i = 0; i < list.arraySize; i++)
            {
                var url = (list.GetArrayElementAtIndex(i).stringValue ?? "").Trim();
                if (url.Length > 0) urls.Add(url);
            }
            return urls;
        }

        void AddNetwork(WaxKitManager.NetworkEntry entry)
        {
            serializedObject.ApplyModifiedProperties();
            var manager = (WaxKitManager)target;
            Undo.RecordObject(manager, "Add Network");
            manager.Networks.Add(entry);
            EditorUtility.SetDirty(manager);
            serializedObject.Update();
        }
    }
}
