// Menu "Dynamic Shadows > Setup Scene".
//
// Prepares the open scene for the shadow pass: creates the materials if they do not exist, puts the
// shadow catcher material on all the geometry, checks the lighting and leaves the carrier for the
// character material. It is everything that has to be done apart from putting your FBX in the
// scene.
//
// It can be run as many times as needed: it duplicates nothing.

using UnityEditor;
using UnityEngine;
using System.IO;

public static class SetupDynamicShadowsScene
{
    private const string CatcherShader = "Memoria/ShadowCatcher";
    private const string ActorShader = "Memoria/FieldActorLit";
    private const string CarrierName = "MemoriaCharacterMaterial";
    private const string MaterialFolder = "Assets/Materials";

    [MenuItem("Dynamic Shadows/Setup Scene")]
    public static void Setup()
    {
        Material catcher = GetOrCreateMaterial(CatcherShader, "ShadowCatcher");
        Material actor = GetOrCreateMaterial(ActorShader, "FieldActorLit");
        if (catcher == null || actor == null)
            return;

        GameObject carrier = SetupCarrier(actor);

        // All the scene geometry becomes catcher, except the carrier.
        int painted = 0;
        foreach (MeshRenderer renderer in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (carrier != null && renderer.transform.IsChildOf(carrier.transform))
                continue;

            Material[] materials = new Material[Mathf.Max(1, renderer.sharedMaterials.Length)];
            for (int i = 0; i < materials.Length; i++)
                materials[i] = catcher;
            renderer.sharedMaterials = materials;

            // Static batching bakes the transform into the vertices when the bundle is built, and
            // that cancels the container's scale (SCENESCALE) at runtime: the geometry would keep
            // drawing at its authoring size while the transform says otherwise.
            renderer.gameObject.isStatic = false;
            painted++;
        }

        SetupLight();

        Debug.Log(string.Format(
            "[DynamicShadows] Done: {0} object(s) now use the shadow catcher. Save the scene named " +
            "after the map number (150.unity for Cast. Alex./Guard) and use " +
            "Dynamic Shadows > Build Bundle.", painted));
    }

    private static Material GetOrCreateMaterial(string shaderName, string assetName)
    {
        Shader shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogError(string.Format(
                "[DynamicShadows] Cannot find shader '{0}'. Check that " +
                "Assets/Shaders/*.shader is in the project and compiles without errors.", shaderName));
            return null;
        }

        string path = MaterialFolder + "/" + assetName + ".mat";
        Material existing = AssetDatabase.LoadAssetAtPath(path, typeof(Material)) as Material;
        if (existing != null)
        {
            existing.shader = shader;
            return existing;
        }

        if (!Directory.Exists(MaterialFolder))
        {
            Directory.CreateDirectory(MaterialFolder);
            AssetDatabase.Refresh();
        }
        Material material = new Material(shader);
        AssetDatabase.CreateAsset(material, path);
        Debug.Log("[DynamicShadows] Material created: " + path);
        return material;
    }

    /// <summary>
    /// An object carrying the character material so that it travels inside the bundle: a shader
    /// cannot be compiled at runtime, so this is the only way to get it into the game.
    ///
    /// It is left ACTIVE with the MeshRenderer unchecked, and not the other way round. The mod
    /// finds the scene content by walking the root objects with FindObjectsOfType, which does not
    /// return disabled ones: a disabled carrier at the root would never be found.
    /// </summary>
    private static GameObject SetupCarrier(Material actor)
    {
        GameObject carrier = GameObject.Find(CarrierName);
        if (carrier == null)
        {
            carrier = GameObject.CreatePrimitive(PrimitiveType.Quad);
            carrier.name = CarrierName;
            Object.DestroyImmediate(carrier.GetComponent<Collider>());
            Debug.Log("[DynamicShadows] Character material carrier created.");
        }

        carrier.SetActive(true);
        carrier.isStatic = false;
        MeshRenderer renderer = carrier.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = actor;
        renderer.enabled = false;
        return carrier;
    }

    private static void SetupLight()
    {
        Light directional = null;
        foreach (Light light in Object.FindObjectsOfType<Light>())
        {
            if (light.type != LightType.Directional)
                continue;
            directional = light;
            break;
        }

        // No directional is created. The scene decides its own lighting, and a map lit only by
        // spotlights is a legitimate decision: forcing a directional would mean deleting it every
        // time this runs. It only warns when there is no light at all, which really is an oversight.
        if (directional == null && Object.FindObjectsOfType<Light>().Length == 0)
        {
            Debug.LogWarning("[DynamicShadows] The scene has no light at all. Without at least one " +
                             "with shadows enabled the catcher has nothing to catch and no shadow " +
                             "will be seen.");
        }

        // Without this there is nothing to catch: the catcher only paints shadow attenuation. And it
        // applies to every light, not just the directional: a spotlight with no Shadow Type casts
        // nothing, and it is easy to place one and never work out why its shadow is missing.
        foreach (Light light in Object.FindObjectsOfType<Light>())
        {
            if (light == null || light.shadows != LightShadows.None)
                continue;
            light.shadows = LightShadows.Soft;
            Debug.Log("[DynamicShadows] '" + light.name + "' had no shadows enabled; set to Soft.");
        }
    }
}
