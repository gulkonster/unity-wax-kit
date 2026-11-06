using System.Linq;
using UnityEditor;

namespace UnityWaxKit.Editor
{
    /// <summary>Inspector of <see cref="WaxNftMedia"/>: the fields, with checks for wrong ids.</summary>
    [CustomEditor(typeof(WaxNftMedia))]
    internal sealed class WaxNftMediaEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var asset = (serializedObject.FindProperty("assetId").stringValue ?? "").Trim();
            var template = (serializedObject.FindProperty("templateId").stringValue ?? "").Trim();
            var collection = (serializedObject.FindProperty("collectionName").stringValue ?? "").Trim();

            DrawDefaultInspector();

            if (asset.Length > 0 && !asset.All(char.IsDigit))
                EditorGUILayout.HelpBox($"The asset id \"{asset}\" must be digits.", MessageType.Error);
            if (template.Length > 0 && !template.All(char.IsDigit))
                EditorGUILayout.HelpBox($"The template id \"{template}\" must be digits.", MessageType.Error);
            if (collection.Length > 0 && !WaxToken.IsAccountName(collection))
                EditorGUILayout.HelpBox($"\"{collection}\" is not a collection name.", MessageType.Error);
            if (asset.Length > 0 && template.Length > 0)
                EditorGUILayout.HelpBox("Both ids are set: the asset id is used.", MessageType.Info);
            if (asset.Length == 0 && template.Length == 0)
                EditorGUILayout.HelpBox("Set an asset id or a template id (or call Show / ShowTemplate from code).", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
