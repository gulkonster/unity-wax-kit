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

            serializedObject.ApplyModifiedProperties();
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
                        EditorGUILayout.HelpBox($"{label}: \"{url}\" does not start with https://. WebGL builds and Android block plain http, " +
                                                "so use https:// endpoints.", MessageType.Warning);
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
