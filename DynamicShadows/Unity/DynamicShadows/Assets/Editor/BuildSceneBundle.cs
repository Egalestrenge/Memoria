// Menú "Dynamic Shadows > Construir bundle".
//
// Empaqueta LA ESCENA QUE TENGAS ABIERTA con BuildStreamedSceneAssetBundle. Tiene que ser un
// bundle de escena y no de assets: los lightmaps y los light probes son datos de escena y solo
// viajan de esta forma.
//
// El bundle sale como "<NombreDeEscena>.unity3d" DENTRO de la carpeta del mod, en
// DynamicShadows/Mod/DynamicShadows/. Escribir ahi y no en una carpeta de artefactos aparte es
// deliberado: con "SCENEBUNDLE auto" el nombre del fichero ES la configuracion, asi que una
// escena llamada 150.unity produce 150.unity3d y el mapa 150 ya la carga. No hay ningun paso de
// copia intermedio que se pueda olvidar.

using UnityEditor;
using UnityEngine;
using System.IO;

public static class BuildSceneBundle
{
    [MenuItem("Dynamic Shadows/Construir bundle")]
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

        // Application.dataPath es <repo>/DynamicShadows/Unity/DynamicShadows/Assets; subiendo tres
        // niveles se llega a DynamicShadows/, donde estan tanto Unity/ como Mod/.
        string unityProject = Path.GetDirectoryName(Application.dataPath);
        string dynamicShadows = Path.GetDirectoryName(Path.GetDirectoryName(unityProject));
        string outputDir = Path.Combine(dynamicShadows, "Mod/DynamicShadows");
        if (!Directory.Exists(outputDir))
        {
            Debug.LogError(
                "No encuentro la carpeta del mod: " + outputDir + ". El proyecto de Unity tiene que " +
                "estar en DynamicShadows/Unity/<proyecto>/ dentro del repo.");
            return;
        }

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
