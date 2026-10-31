using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gulkonster.WaxKit.Editor
{
    internal static class CreateDemoScene
    {
        const string ScenePath = "Assets/WaxKitDemo/WaxKitDemo.unity";
        const string DemoTypeName = "WaxKitDemo";

        [MenuItem("Tools/Unity WAX Kit/Create Demo Scene")]
        static void Create()
        {
            // The demo script lives in the repo's Assets folder, so it is looked up by name.
            var demoType = TypeCache.GetTypesDerivedFrom<MonoBehaviour>().FirstOrDefault(t => t.Name == DemoTypeName);
            if (demoType == null)
            {
                EditorUtility.DisplayDialog("Unity WAX Kit",
                    $"{DemoTypeName}.cs was not found. It ships in Assets/WaxKitDemo of the unity-wax-kit repository.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            new GameObject(DemoTypeName, demoType);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"Unity WAX Kit: demo scene saved to {ScenePath}");
        }
    }
}
