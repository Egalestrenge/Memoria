// Menú "Memoria > Setup Shadow Test Scene".
//
// Prepara la escena abierta para la prueba de sombras: crea los materiales si no existen, pone el
// material de shadow catcher en toda la geometría, añade la direccional y deja el portador del
// material del personaje. Es todo lo que hay que hacer aparte de meter tus FBX en la escena.
//
// Se puede ejecutar las veces que haga falta: no duplica nada.

using UnityEditor;
using UnityEngine;
using System.IO;

public static class SetupShadowTest
{
    private const string CatcherShader = "Memoria/ShadowCatcher";
    private const string ActorShader = "Memoria/FieldActorLit";
    private const string CarrierName = "MemoriaCharacterMaterial";
    private const string MaterialFolder = "Assets/Materials";

    [MenuItem("Memoria/Setup Shadow Test Scene")]
    public static void Setup()
    {
        Material catcher = GetOrCreateMaterial(CatcherShader, "ShadowCatcher");
        Material actor = GetOrCreateMaterial(ActorShader, "FieldActorLit");
        if (catcher == null || actor == null)
            return;

        GameObject carrier = SetupCarrier(actor);

        // Toda la geometría de la escena pasa a ser catcher, menos el portador.
        int painted = 0;
        foreach (MeshRenderer renderer in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (carrier != null && renderer.transform.IsChildOf(carrier.transform))
                continue;

            Material[] materials = new Material[Mathf.Max(1, renderer.sharedMaterials.Length)];
            for (int i = 0; i < materials.Length; i++)
                materials[i] = catcher;
            renderer.sharedMaterials = materials;

            // El batching estático hornea la transformada en los vértices al construir el bundle,
            // y eso anula la escala del contenedor (SCENESCALE) en runtime: la geometría seguiría
            // dibujándose a su tamaño de autoría mientras el transform dice otra cosa.
            renderer.gameObject.isStatic = false;
            painted++;
        }

        SetupLight();

        Debug.Log(string.Format(
            "[SetupShadowTest] Listo: {0} objeto(s) con el shadow catcher. Guarda la escena como " +
            "ShadowTest y usa Memoria > Build Scene Bundle.", painted));
    }

    private static Material GetOrCreateMaterial(string shaderName, string assetName)
    {
        Shader shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogError(string.Format(
                "[SetupShadowTest] No encuentro el shader '{0}'. Comprueba que " +
                "Assets/Shaders/*.shader está en el proyecto y que compila sin errores.", shaderName));
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
        Debug.Log("[SetupShadowTest] Material creado: " + path);
        return material;
    }

    /// <summary>
    /// Objeto que lleva el material del personaje para que viaje dentro del bundle: un shader no se
    /// puede compilar en runtime, así que esta es la única vía de meterlo en el juego.
    ///
    /// Queda ACTIVO con el MeshRenderer desmarcado, y no al revés. El mod localiza el contenido de
    /// la escena recorriendo los objetos raíz con FindObjectsOfType, que no devuelve los que están
    /// desactivados: un portador desactivado en la raíz no se encontraría nunca.
    /// </summary>
    private static GameObject SetupCarrier(Material actor)
    {
        GameObject carrier = GameObject.Find(CarrierName);
        if (carrier == null)
        {
            carrier = GameObject.CreatePrimitive(PrimitiveType.Quad);
            carrier.name = CarrierName;
            Object.DestroyImmediate(carrier.GetComponent<Collider>());
            Debug.Log("[SetupShadowTest] Portador del material del personaje creado.");
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

        // No se crea ninguna direccional. La escena decide su iluminación, y un mapa alumbrado
        // solo con focos es una decisión legítima: imponer una direccional obliga a borrarla cada
        // vez que se pasa por aquí. Solo se avisa si no hay ninguna luz, que sí es un olvido.
        if (directional == null && Object.FindObjectsOfType<Light>().Length == 0)
        {
            Debug.LogWarning("[SetupShadowTest] La escena no tiene ninguna luz. Sin al menos una con " +
                             "sombras activadas, el catcher no tiene nada que recoger y no se verá " +
                             "ninguna sombra.");
        }

        // Sin esto no hay nada que recoger: el catcher solo pinta la atenuación de sombra. Y vale
        // para toda luz, no solo la direccional: un foco sin Shadow Type no proyecta nada, y es
        // fácil ponerlo y quedarse sin entender por qué no se ve su sombra.
        foreach (Light light in Object.FindObjectsOfType<Light>())
        {
            if (light == null || light.shadows != LightShadows.None)
                continue;
            light.shadows = LightShadows.Soft;
            Debug.Log("[SetupShadowTest] '" + light.name + "' no tenía sombras activadas; puesto en Soft.");
        }
    }
}
