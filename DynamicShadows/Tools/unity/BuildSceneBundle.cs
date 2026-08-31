// Cópialo en tu proyecto de Unity 5.2.3 dentro de "Assets/Editor/BuildSceneBundle.cs".
// Aparece el menú "Memoria > Build Scene Bundle".
//
// Empaqueta LA ESCENA QUE TENGAS ABIERTA con BuildStreamedSceneAssetBundle. Tiene que ser un
// bundle de escena y no de assets: los lightmaps y los light probes son datos de escena y solo
// viajan de esta forma.
//
// El bundle sale como "<NombreDeEscena>.unity3d" en la carpeta BuiltBundles, junto a Assets.
// Ese es el nombre que hay que poner en la linea SCENEBUNDLE de MemoriaFieldObjects.txt.

using UnityEditor;
using UnityEngine;
using System.IO;

public static class BuildSceneBundle
{
    [MenuItem("Memoria/Build Scene Bundle")]
    public static void Build()
    {
        string scenePath = EditorApplication.currentScene;
        if (string.IsNullOrEmpty(scenePath))
        {
            Debug.LogError("Guarda la escena antes de construir el bundle.");
            return;
        }

        // Los cambios sin guardar no entran en el bundle: se empaqueta lo que hay en disco.
        if (!EditorApplication.SaveCurrentSceneIfUserWantsTo())
            return;

        string sceneName = Path.GetFileNameWithoutExtension(scenePath);
        string outputDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "BuiltBundles");
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        string outputPath = Path.Combine(outputDir, sceneName + ".unity3d");

        // Dos cosas importantes:
        //  - El juego es x64: un bundle de StandaloneWindows (32 bits) no carga.
        //  - UncompressedAssetBundle produce el formato "UnityRaw". Sin esa opcion sale "UnityWeb"
        //    (comprimido con LZMA), que AssetBundle.CreateFromFile no puede abrir. El cargador tiene
        //    un plan B que lo descomprime en memoria, pero sin comprimir carga mas rapido.
        string error = BuildPipeline.BuildStreamedSceneAssetBundle(
            new[] { scenePath },
            outputPath,
            BuildTarget.StandaloneWindows64,
            BuildOptions.UncompressedAssetBundle);

        if (string.IsNullOrEmpty(error))
        {
            Debug.Log("Bundle creado: " + outputPath);
            EditorUtility.RevealInFinder(outputPath);
        }
        else
        {
            Debug.LogError("Fallo al crear el bundle: " + error);
        }
    }
}
