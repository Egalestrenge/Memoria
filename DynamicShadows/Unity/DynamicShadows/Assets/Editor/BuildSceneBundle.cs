// Menu "Dynamic Shadows > Build Bundle".
//
// Packs THE SCENE YOU CURRENTLY HAVE OPEN with BuildStreamedSceneAssetBundle. It has to be a scene
// bundle and not an asset bundle: lightmaps and light probes are scene data and only travel this
// way.
//
// The bundle comes out as "<SceneName>.unity3d" INSIDE the mod folder, in
// DynamicShadows/Mod/DynamicShadows/. Writing there rather than into a separate artifacts folder is
// deliberate: with "SCENEBUNDLE auto" the file name IS the configuration, so a scene called
// 150.unity produces 150.unity3d and map 150 already loads it. There is no intermediate copy step
// that can be forgotten.

using UnityEditor;
using UnityEngine;
using System.IO;

public static class BuildSceneBundle
{
    [MenuItem("Dynamic Shadows/Build Bundle")]
    public static void Build()
    {
        string scenePath = EditorApplication.currentScene;
        if (string.IsNullOrEmpty(scenePath))
        {
            Debug.LogError("Save the scene before building the bundle.");
            return;
        }

        // Unsaved changes do not make it into the bundle: what is on disk is what gets packed.
        if (!EditorApplication.SaveCurrentSceneIfUserWantsTo())
            return;

        string sceneName = Path.GetFileNameWithoutExtension(scenePath);

        // Application.dataPath is <repo>/DynamicShadows/Unity/DynamicShadows/Assets; going up three
        // levels reaches DynamicShadows/, which holds both Unity/ and Mod/.
        string unityProject = Path.GetDirectoryName(Application.dataPath);
        string dynamicShadows = Path.GetDirectoryName(Path.GetDirectoryName(unityProject));
        string outputDir = Path.Combine(dynamicShadows, "Mod/DynamicShadows");
        if (!Directory.Exists(outputDir))
        {
            Debug.LogError(
                "Cannot find the mod folder: " + outputDir + ". The Unity project has to live in " +
                "DynamicShadows/Unity/<project>/ inside the repo.");
            return;
        }

        string outputPath = Path.Combine(outputDir, sceneName + ".unity3d");

        // Two things that matter:
        //  - The game is x64: a StandaloneWindows (32-bit) bundle will not load.
        //  - UncompressedAssetBundle produces the "UnityRaw" format. Without that option you get
        //    "UnityWeb" (LZMA compressed), which AssetBundle.CreateFromFile cannot open. The loader
        //    has a fallback that decompresses it in memory, but uncompressed loads faster.
        string error = BuildPipeline.BuildStreamedSceneAssetBundle(
            new[] { scenePath },
            outputPath,
            BuildTarget.StandaloneWindows64,
            BuildOptions.UncompressedAssetBundle);

        if (string.IsNullOrEmpty(error))
        {
            Debug.Log("Bundle created: " + outputPath);
            EditorUtility.RevealInFinder(outputPath);
        }
        else
        {
            Debug.LogError("Failed to build the bundle: " + error);
        }
    }
}
