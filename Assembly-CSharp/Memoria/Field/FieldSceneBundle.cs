using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Memoria.Field
{
    // Loads a scenario authored in the Unity editor as a streamed scene AssetBundle, and grafts its
    // contents onto the 3D pass.
    //
    // A scene bundle is used rather than a prefab because baked lighting is scene data: lightmap
    // textures live in LightmapSettings.lightmaps and light probes in LightmapSettings.lightProbes,
    // neither of which a prefab carries. Loading the scene additively makes Unity merge that data
    // and fix up every renderer's lightmapIndex, which is what makes a baked scenario work at all.
    //
    // The bundle must be built with the same Unity version as the game (5.2.3): the runtime here
    // only has the old API surface (Application.LoadLevelAdditive; SceneManager arrived in 5.3).
    //
    // Config line, read from MemoriaFieldObjects.txt:
    //     SCENEBUNDLE <fldMapNo> <bundleFile> [sceneName]
    // The file is looked up in the game folder and then inside each active mod folder. Without a
    // scene name the first scene in the bundle is used.
    public static class FieldSceneBundle
    {
        /// <summary>Name of the shadow-catcher shader, as written in the .shader file.</summary>
        private const String CatcherShaderName = "Memoria/ShadowCatcher";

        // Bundles stay loaded for the whole session: AssetBundle.CreateFromFile throws if the same
        // file is opened twice, and every field transition re-adds the scene.
        private static readonly Dictionary<String, AssetBundle> _bundles = new Dictionary<String, AssetBundle>();

        /// <summary>Frames que se sigue mirando despues de la primera adopcion. Ver Update.</summary>
        private const Int32 AdoptGraceFrames = 5;

        private static String _pendingScene;
        private static Int32 _pendingFrames;
        private static Int32 _firstAdoptedFrame;
        private static HashSet<Transform> _rootsBeforeLoad;
        private static readonly List<GameObject> _adopted = new List<GameObject>();

        public static void Reset()
        {
            _pendingScene = null;
            _rootsBeforeLoad = null;
            _firstAdoptedFrame = 0;
            _adopted.Clear();
        }

        public static void Request(String bundleFile, String sceneName)
        {
            try
            {
                String path = ResolvePath(bundleFile);
                if (path == null)
                {
                    Log.Warning($"[FieldSceneBundle] Bundle not found: {bundleFile}");
                    return;
                }

                if (!_bundles.TryGetValue(path, out AssetBundle bundle) || bundle == null)
                {
                    // CreateFromFile only opens uncompressed bundles ("UnityRaw"). The scene build
                    // pipeline produces LZMA-compressed ones ("UnityWeb") unless it is told
                    // otherwise, and those have to go through memory instead.
                    bundle = AssetBundle.CreateFromFile(path);
                    if (bundle == null)
                    {
                        Log.Message($"[FieldSceneBundle] '{path}' is not an uncompressed bundle; decompressing it in memory.");
                        bundle = AssetBundle.CreateFromMemoryImmediate(File.ReadAllBytes(path));
                    }
                    if (bundle == null)
                    {
                        Log.Warning($"[FieldSceneBundle] Could not open '{path}'. Check the header: it starts with the Unity version it was built with, which must be the game's (5.2.3), and the target must be 64-bit.");
                        return;
                    }
                    _bundles[path] = bundle;
                    Log.Message($"[FieldSceneBundle] Opened '{path}'.");
                }

                String[] scenePaths = bundle.GetAllScenePaths();
                if (scenePaths == null || scenePaths.Length == 0)
                {
                    Log.Warning($"[FieldSceneBundle] '{path}' has no scenes in it. Build it with BuildPipeline.BuildStreamedSceneAssetBundle, not as an asset bundle.");
                    return;
                }
                foreach (String scenePath in scenePaths)
                    Log.Message($"[FieldSceneBundle]   scene: {scenePath}");

                String scene = sceneName;
                if (String.IsNullOrEmpty(scene))
                    scene = Path.GetFileNameWithoutExtension(scenePaths[0]);

                // Make sure the 3D camera, root and light already exist before the snapshot, or
                // they would look like objects that came out of the scene.
                FieldPerspectiveCamera.GetOrCreateSceneRoot();
                _rootsBeforeLoad = CollectRoots(true);
                _pendingScene = scene;
                _pendingFrames = 0;
                _firstAdoptedFrame = 0;
                Application.LoadLevelAdditive(scene);
                Log.Message($"[FieldSceneBundle] Loading scene '{scene}' additively ({_rootsBeforeLoad.Count} roots before).");
            }
            catch (Exception err)
            {
                Log.Error(err, "[FieldSceneBundle] Failed to load the scene bundle.");
            }
        }

        /// <summary>
        /// Additive loading does not necessarily finish inside the call, so the new root objects are
        /// picked up by diffing the scene roots over the following frames.
        /// </summary>
        public static void Update()
        {
            if (_pendingScene == null)
                return;
            try
            {
                _pendingFrames++;
                HashSet<Transform> roots = CollectRoots(false);
                Transform root3D = FieldPerspectiveCamera.GetOrCreateSceneRoot();

                Int32 adopted = 0;
                foreach (Transform candidate in roots)
                {
                    if (candidate == null || _rootsBeforeLoad.Contains(candidate))
                        continue;
                    if (FieldPerspectiveCamera.IsOwnObject(candidate))
                        continue;
                    // The game keeps creating objects of its own while the additive load is in
                    // flight, so "new since the snapshot" is not enough on its own. Scene content
                    // worth adopting always draws or lights something.
                    GameObject go = candidate.gameObject;
                    if (go.GetComponentsInChildren<Renderer>(true).Length == 0 &&
                        go.GetComponentsInChildren<Light>(true).Length == 0)
                    {
                        _rootsBeforeLoad.Add(candidate);
                        Log.Message($"[FieldSceneBundle] Ignoring '{go.name}': nothing to draw or light, not scene content.");
                        continue;
                    }
                    Adopt(candidate, root3D);
                    adopted++;
                }

                if (adopted > 0)
                {
                    Log.Message($"[FieldSceneBundle] Adopted {adopted} root object(s) from '{_pendingScene}' into the 3D pass.");
                    if (_firstAdoptedFrame == 0)
                        _firstAdoptedFrame = _pendingFrames;
                    else
                        Log.Warning($"[FieldSceneBundle] {adopted} more root object(s) turned up {_pendingFrames - _firstAdoptedFrame} frame(s) after the first batch. Either the additive load split across frames -and until now the rest was being left behind, unscaled and on the wrong layer- or the game created these and they do not belong to the scene at all.");
                }

                // No se puede parar en el primer frame que de algo. La carga aditiva no garantiza
                // entregar todas sus raices a la vez, y parando ahi el resto se quedaba fuera: sin
                // la escala del contenedor y en la capa que no dibuja la camara 3D, o sea invisible
                // y sin avisar. Se sigue mirando unos frames mas y se dice si llega algo tarde.
                if (_firstAdoptedFrame > 0 && _pendingFrames - _firstAdoptedFrame >= AdoptGraceFrames)
                {
                    _pendingScene = null;
                    _rootsBeforeLoad = null;
                }
                else if (_firstAdoptedFrame == 0 && _pendingFrames > 300)
                {
                    Log.Warning($"[FieldSceneBundle] Scene '{_pendingScene}' produced no new objects after {_pendingFrames} frames. Check the scene name against the paths logged above.");
                    _pendingScene = null;
                    _rootsBeforeLoad = null;
                }
            }
            catch (Exception err)
            {
                Log.Error(err, "[FieldSceneBundle] Failed while adopting scene objects.");
                _pendingScene = null;
            }
        }

        private static void Adopt(Transform candidate, Transform root3D)
        {
            GameObject go = candidate.gameObject;
            // worldPositionStays: false on purpose. The scene is authored in its own units and the
            // container carries the conversion, so the local transform must be left untouched.
            candidate.SetParent(root3D, false);
            FieldPerspectiveCamera.SetLayerRecursively(go, FieldPerspectiveCamera.Layer3D);
            _adopted.Add(go);

            // Authored values against what they become once the container's scale is applied: this
            // is what tells apart "the scene is not in the expected units" from "the scale is not
            // being applied".
            Log.Message($"[FieldSceneBundle] '{go.name}' container scale {root3D.localScale.x} | authored pos {candidate.localPosition} scale {candidate.localScale} | world pos {candidate.position} scale {candidate.lossyScale}");
            Boolean anyStaticBatch = false;
            foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                anyStaticBatch |= renderer.isPartOfStaticBatch;
                // The centre is what validates the Blender -> FBX -> Unity axis conversion: a
                // marker authored at a known field position has to come back at that position.
                Log.Message($"[FieldSceneBundle]   '{renderer.name}' at field {renderer.bounds.center} size {renderer.bounds.size} staticBatch={renderer.isPartOfStaticBatch}");
            }
            if (anyStaticBatch && !Mathf.Approximately(root3D.localScale.x, 1f))
            {
                // Static batching combines meshes at build time with their world transform baked
                // into the vertices, so the renderer ignores the transform afterwards. That silently
                // defeats the scene scale: positions look right in the transform data while the
                // geometry keeps drawing at its authored size.
                Log.Warning($"[FieldSceneBundle] '{go.name}' is statically batched, so the scene scale of {root3D.localScale.x} has no effect on how it draws. In Unity, open the Static dropdown and uncheck 'Batching Static' while keeping 'Lightmap Static'.");
            }

            // A scene brings its own camera and audio listener, which would fight the game's.
            foreach (Camera camera in go.GetComponentsInChildren<Camera>(true))
            {
                Log.Message($"[FieldSceneBundle] Removing camera '{camera.name}' that came with the scene.");
                UnityEngine.Object.Destroy(camera);
            }
            foreach (AudioListener listener in go.GetComponentsInChildren<AudioListener>(true))
                UnityEngine.Object.Destroy(listener);

            // Scene lights are kept, but restricted to the 3D layer so they cannot touch anything
            // the game renders through the PSX pass.
            //
            // Their range needs converting by hand. A light's range is in world units and Unity
            // does NOT scale it with the transform, so a torch authored with a 3 m range keeps a
            // range of 3 once the container has scaled the scene into field units, where 3 units
            // is 9 millimetres. The light then reaches nothing at all, which looks exactly like a
            // light that is not working.
            Single containerScale = root3D.localScale.x;
            Int32 shadowCasters = 0;
            foreach (Light light in go.GetComponentsInChildren<Light>(true))
            {
                light.cullingMask = 1 << FieldPerspectiveCamera.Layer3D;

                // Forzar la luz a por-pixel, o no proyecta nada.
                //
                // En forward, Unity solo emite pases ForwardAdd para las luces que decide tratar
                // por pixel; el resto las degrada a luz por vertice, y una luz por vertice no tiene
                // pase ForwardAdd ni sombra, este como este configurada. El limite lo pone
                // QualitySettings.pixelLightCount, que en un juego de 2000 suele ser 1 o 2, y
                // reparte segun intensidad y distancia: en cuanto hay dos focos, uno se cae.
                //
                // ForcePixel lo saca de ese reparto. Solo se aplica a las luces con sombras
                // activadas, que son las unicas para las que importa.
                if (light.shadows != LightShadows.None)
                {
                    light.renderMode = LightRenderMode.ForcePixel;
                    shadowCasters++;
                }
                // Los ajustes de sombra de la propia luz, que mandan por debajo de cualquier shader:
                // con strength 0 la sombra se calcula y llega multiplicada por nada, y un bias
                // grande la desplaza hasta sacarla de la superficie. Ninguna de las dos se puede
                // deducir mirando la pantalla, y las dos se parecen a "el shader no funciona".
                String shadowInfo = light.shadows == LightShadows.None
                    ? "no shadows"
                    : $"shadows {light.shadows}, strength {light.shadowStrength:F2}, bias {light.shadowBias:F4}, normal bias {light.shadowNormalBias:F4}";
                if (light.type == LightType.Spot)
                    shadowInfo += $", cone {light.spotAngle:F0} deg";

                if (light.type != LightType.Directional && !Mathf.Approximately(containerScale, 1f))
                {
                    Single authored = light.range;
                    light.range = authored * containerScale;
                    Log.Message($"[FieldSceneBundle] Scene light '{light.name}': {light.type}, intensity {light.intensity}, {shadowInfo}, range {authored} authored -> {light.range} field units.");
                    continue;
                }
                Log.Message($"[FieldSceneBundle] Scene light '{light.name}': {light.type}, intensity {light.intensity}, {shadowInfo}");
            }

            // The character shader cannot be compiled at runtime, so it has to arrive inside the
            // bundle. Any material in the scene using it is taken as the template; the object
            // carrying it can be left disabled in the editor, since inactive children are scanned.
            if (FieldPerspectiveCamera.CharacterMaterial == null)
            {
                foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material?.shader == null || material.shader.name != FieldPerspectiveCamera.CharacterShaderName)
                            continue;
                        // Un shader que no compila para esta plataforma no tiene ni pase de sombra,
                        // asi que adoptarlo no solo pinta mal: deja al personaje sin proyectar nada.
                        // Standard es peor de aspecto pero funciona, y es mejor sitio donde caer.
                        if (!material.shader.isSupported)
                        {
                            Log.Warning($"[FieldSceneBundle] Character material on '{renderer.name}' uses '{material.shader.name}', which is not supported on this build. Falling back to Standard, so the character still casts a shadow (its silhouette will include the alpha-cut quads).");
                            continue;
                        }
                        FieldPerspectiveCamera.CharacterMaterial = material;
                        Log.Message($"[FieldSceneBundle] Character material from '{renderer.name}': shader '{material.shader.name}'.");
                        break;
                    }
                    if (FieldPerspectiveCamera.CharacterMaterial != null)
                        break;
                }
            }

            // El presupuesto de luces por pixel tiene que dar para todas las que proyectan. ForcePixel
            // saca a cada luz del reparto, pero el limite global sigue existiendo y Unity lo aplica
            // igual, asi que hay que subirlo si se queda corto.
            if (shadowCasters > 0 && QualitySettings.pixelLightCount < shadowCasters + 1)
            {
                Int32 before = QualitySettings.pixelLightCount;
                QualitySettings.pixelLightCount = shadowCasters + 1;
                Log.Message($"[FieldSceneBundle] Per-pixel light budget raised from {before} to {QualitySettings.pixelLightCount} for {shadowCasters} shadow-casting light(s). Below that Unity demotes the extra ones to vertex lights, which have no ForwardAdd pass and therefore cast nothing.");
            }
            else if (shadowCasters > 0)
            {
                Log.Message($"[FieldSceneBundle] {shadowCasters} shadow-casting light(s), per-pixel budget {QualitySettings.pixelLightCount}, all forced to per-pixel.");
            }

            // Diagnostico del pase aditivo del catcher, puesto desde la configuracion.
            if (FieldPerspectiveCamera.CatcherDebugMode > 0)
            {
                foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
                    foreach (Material material in renderer.sharedMaterials)
                        if (material?.shader != null && material.shader.name == CatcherShaderName
                            && material.HasProperty("_AddDebug"))
                            material.SetFloat("_AddDebug", FieldPerspectiveCamera.CatcherDebugMode);
                String what;
                switch (FieldPerspectiveCamera.CatcherDebugMode)
                {
                    case 1: what = "flat red wherever the pass runs at all"; break;
                    case 2: what = "the shadow term alone: black where the lamp's light is blocked. All white means the shader is not reading that lamp's shadow map"; break;
                    case 3: what = "the reach term alone: black where the lamp does not reach. All black means the attenuation is zero, so the product can never darken"; break;
                    case 4: what = "the final factor: black where the pass darkens fully, white where it does nothing, without the material's colour on top"; break;
                    default: what = "an unknown mode"; break;
                }
                Log.Message($"[FieldSceneBundle] Catcher additive pass in diagnostic mode {FieldPerspectiveCamera.CatcherDebugMode}: {what}.");
            }

            // Que SubShader esta activo, no cual esperabamos que lo estuviera.
            //
            // Los shaders del mod llevan un SubShader de respaldo por si el principal no compila en
            // Direct3D 9, y desde fuera los dos son "soportados": isSupported no los distingue. Lo
            // que si los distingue es el numero de pases, porque passCount devuelve los del
            // SubShader ACTIVO. Sin esto, "los focos no proyectan sombra" es indistinguible de
            // "el pase de sombra de los focos no compilo", y eso solo se puede resolver adivinando.
            //
            //   Memoria/ShadowCatcher  : 4 pases el completo, 3 el de respaldo (sin ForwardAdd,
            //                            o sea sin sombras de focos ni de luces puntuales)
            //   Memoria/FieldActorLit  : 3 pases el completo, 2 el de respaldo (sin iluminacion)
            HashSet<Shader> reported = new HashSet<Shader>();
            HashSet<Material> reportedMaterials = new HashSet<Material>();
            foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material?.shader == null || !material.shader.name.StartsWith("Memoria/"))
                        continue;

                    // Cuanto oscurece el catcher lo gradua el propio material, y cada mapa trae el
                    // suyo. Con _Strength a 0, o con un color de sombra blanco, el shader hace todo
                    // el trabajo y no se ve nada: indistinguible de un shader roto.
                    if (material.shader.name == CatcherShaderName && reportedMaterials.Add(material)
                        && material.HasProperty("_Strength") && material.HasProperty("_ShadowColor"))
                    {
                        Single strength = material.GetFloat("_Strength");
                        Color shadowColor = material.GetColor("_ShadowColor");
                        Log.Message($"[FieldSceneBundle] Catcher material on '{renderer.name}': strength {strength:F2}, shadow colour {shadowColor}.");
                    }

                    if (!reported.Add(material.shader))
                        continue;
                    Int32 passes = material.passCount;
                    Boolean full = material.shader.name == FieldPerspectiveCamera.CharacterShaderName
                        ? passes >= 3
                        : passes >= 4;
                    if (full)
                        Log.Message($"[FieldSceneBundle] '{material.shader.name}': {passes} passes, the full sub-shader compiled.");
                    else
                        Log.Warning($"[FieldSceneBundle] '{material.shader.name}': only {passes} passes, so it fell back to the reduced sub-shader. For the catcher that means no shadows from spot or point lights; for the character, no lighting at all. The reason is in Unity's Console when the bundle is built.");
                }
            }

            // A shader that did not survive the bundle draws pink and reports nothing, which is a
            // long way to walk before finding out. Say it here instead.
            foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
                foreach (Material material in renderer.sharedMaterials)
                    if (material?.shader != null && !material.shader.isSupported)
                        Log.Warning($"[FieldSceneBundle] Shader '{material.shader.name}' on '{renderer.name}' is not supported on this build: it will draw pink. Check the bundle was built with the 5.2.3 editor for StandaloneWindows64.");

            Int32 rendererCount = 0;
            Int32 lightmapped = 0;
            foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                rendererCount++;
                if (renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 65534)
                    lightmapped++;
            }
            Log.Message($"[FieldSceneBundle] '{go.name}': {rendererCount} renderer(s), {lightmapped} with a lightmap.");

            // Whether the character darkens in shadow is decided by a raycast, because a shader's
            // shadow map cannot be read from script. No colliders, no darkening — and that failure
            // is completely silent, so it gets said here, once, where it can be known for certain.
            Int32 colliders = go.GetComponentsInChildren<Collider>(true).Length;
            if (colliders > 0)
                Log.Message($"[FieldSceneBundle] '{go.name}': {colliders} collider(s), so characters can be shadowed by this geometry.");
            else if (rendererCount > 0)
                Log.Warning($"[FieldSceneBundle] '{go.name}' has no colliders, so nothing will ever block the light and the characters will not darken when they walk into shadow (a nearby light will still tint them). In Unity, select the FBX and tick 'Generate Colliders' in the Model tab, then rebuild the bundle.");
            // bakedColorSpace has to match the player's: baking in Linear and running in Gamma
            // (or the other way round) produces lightmaps that look washed out or crushed.
            Log.Message($"[FieldSceneBundle] Lightmaps: {LightmapSettings.lightmaps?.Length ?? 0}, mode {LightmapSettings.lightmapsMode}, baked in {LightmapSettings.bakedColorSpace}, player runs in {QualitySettings.activeColorSpace}, probes: {(LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : 0)}.");
        }

        /// <summary>
        /// Las raices de la escena. Lo que se adopta es la diferencia entre la foto de antes de la
        /// carga aditiva y la de despues, asi que las dos fotos no pueden sacarse igual.
        ///
        /// La de ANTES tiene que incluir lo inactivo. FindObjectsOfType no devuelve objetos
        /// desactivados, asi que un objeto del juego que estuviera apagado en ese instante no sale
        /// en la foto, y cuando el juego lo enciende unos frames despues aparece como "nuevo desde
        /// la carga": se lo lleva el pase 3D, reparentado y cambiado de capa. En la primera visita
        /// a un mapa apenas hay objetos apagados; al volver, el juego arrastra los de la visita
        /// anterior, y de ahi que falle solo la segunda vez.
        ///
        /// La de DESPUES se queda con los activos. Resources.FindObjectsOfTypeAll devuelve tambien
        /// assets cargados -los prefabs que trae el propio bundle, por ejemplo-, y adoptar un asset
        /// no tiene ningun sentido. Sobrecoger en la foto de antes es gratis: como mucho deja algo
        /// sin adoptar, y eso se ve. Sobrecoger en la de despues es lo que rompe.
        /// </summary>
        private static HashSet<Transform> CollectRoots(Boolean includeInactive)
        {
            HashSet<Transform> roots = new HashSet<Transform>();
            if (includeInactive)
            {
                foreach (Transform transform in Resources.FindObjectsOfTypeAll<Transform>())
                    if (transform != null && transform.parent == null)
                        roots.Add(transform);
                return roots;
            }
            foreach (Transform transform in UnityEngine.Object.FindObjectsOfType<Transform>())
                if (transform != null && transform.parent == null)
                    roots.Add(transform);
            return roots;
        }

        /// <summary>Whether a bundle by that name is anywhere the loader would look for it.</summary>
        public static Boolean Exists(String bundleFile)
        {
            return ResolvePath(bundleFile) != null;
        }

        private static String ResolvePath(String bundleFile)
        {
            if (File.Exists(bundleFile))
                return bundleFile;
            foreach (String modFolder in Configuration.Mod.FolderNames)
            {
                String candidate = modFolder + "/" + bundleFile;
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }
    }
}
