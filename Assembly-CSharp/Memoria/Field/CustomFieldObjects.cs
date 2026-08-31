using Memoria.Prime;
using Memoria.Scripts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Memoria.Field
{
    // Spawns custom models into existing field maps.
    //
    // Field objects are normally created by the map's event script (see EventEngine.updateModelsToBeAdded),
    // which cannot be extended without rewriting the ".eb" binary. This reads a plain text file instead,
    // so positions and models can be tweaked without rebuilding the assembly.
    //
    // File "MemoriaFieldObjects.txt", in the game root folder (next to Memoria.ini):
    //   <fldMapNo> <modelName> <x> <y> <z> [scale]
    //   1213 GEO_NPC_F0_CUB 0 0 0 1.0
    // Prefix the coordinates with '@' to make them relative to the player's starting position:
    //   1213 GEO_NPC_F0_CUB @0 0 200
    // Use the model name PRIMITIVE_CUBE to spawn an Unity primitive instead of a model, which tells
    // apart a broken FBX from a broken field integration.
    //
    // A line containing only "TRACE" logs the player position while walking, which is how usable
    // coordinates are found: walk to the spot, read the log.
    public static class CustomFieldObjects
    {
        public const String ConfigFileName = "MemoriaFieldObjects.txt";
        public const String PrimitiveCubeName = "PRIMITIVE_CUBE";
        public const String TraceKeyword = "TRACE";
        public const String DumpKeyword = "DUMP";
        public const String ProbeKeyword = "PROBE";
        public const String CameraKeyword = "CAMERA";
        public const String ExportSceneKeyword = "EXPORTSCENE";
        public const String LightKeyword = "LIGHT";
        public const String ShadowDistanceKeyword = "SHADOWDISTANCE";
        public const String LitFlag = "LIT";
        public const String PrimitivePlaneName = "PRIMITIVE_PLANE";
        public const String PlayerKeyword = "PLAYER3D";
        public const String AmbientKeyword = "AMBIENT";
        public const String SceneBundleKeyword = "SCENEBUNDLE";
        public const String SceneScaleKeyword = "SCENESCALE";
        public const String CharacterLightKeyword = "CHARLIGHT";
        public const String MaskDebugKeyword = "MASKDEBUG";
        public const String CatcherDebugKeyword = "CATCHERDEBUG";
        public const String ShadowBiasKeyword = "SHADOWBIAS";

        private const Single TraceMinInterval = 0.5f;
        private const Single TraceMinDistance = 30f;

        private static Boolean _traceEnabled;
        private static Single _traceLastTime;
        private static Vector3 _traceLastPos;

        private static readonly List<GameObject> _spawned = new List<GameObject>();
        private static Boolean _dumpEnabled;
        private static Boolean _dumpPending;
        private static Boolean _autoBundle;
        private static Boolean _autoBundleTried;
        private static String _pendingBundleFile;
        private static String _pendingBundleScene;
        private static FieldPerspectiveCamera.PlayerProxyMode _playerProxyMode;
        /// <summary>
        /// True entre el cierre de un campo y la entrada al siguiente.
        ///
        /// El campo no muere en cuanto se llama a su cierre: sigue actualizandose unos frames
        /// mientras la escena se funde y carga, asi que LateUpdate volvia a pedir el horneado del
        /// proxy justo despues de haberlo limpiado y lo reconstruia entero sobre un mapa que ya
        /// no existe. Se veia como proxies "supervivientes" al entrar en el siguiente, que es una
        /// pista falsa: el cierre si se habia ejecutado.
        /// </summary>
        private static Boolean _fieldClosed;

        private static Boolean _cameraCheckEnabled;
        private static Single _cameraCheckLastTime;

        public static void SpawnIntoField(FieldMap fieldMap)
        {
            try
            {
                if (fieldMap == null)
                    return;

                Int32 mapNo = FF9StateSystem.Common.FF9.fldMapNo;
                Vector3 reference = GetReferencePosition(fieldMap, out String referenceSource);
                Log.Message($"[CustomFieldObjects] Field {mapNo} ({FF9StateSystem.Common.FF9.mapNameStr}), {referenceSource} at {reference.x:F0} {reference.y:F0} {reference.z:F0}");

                _traceEnabled = false;
                _traceLastTime = 0f;
                _traceLastPos = new Vector3(Single.MaxValue, Single.MaxValue, Single.MaxValue);
                _spawned.Clear();
                _dumpEnabled = false;
                _dumpPending = false;
                _cameraCheckEnabled = false;
                _cameraCheckLastTime = 0f;
                _playerProxyMode = FieldPerspectiveCamera.PlayerProxyMode.Off;

                // Entrar en un mapa tiene que dejar el pase 3D vacio, se haya llegado por donde se
                // haya llegado: combate, menu, video o volver al mismo sitio. Un proxy que
                // sobrevive a su mapa es una silueta de mas en la mascara de profundidad.
                if (FieldPerspectiveCamera.ProxyCount > 0)
                    Log.Warning($"[CustomFieldObjects] {FieldPerspectiveCamera.ProxyCount} proxy(ies) came into this field from the previous one. Dropping them. This does not mean the previous shutdown was skipped: the field keeps updating for a few frames while the scene fades, and anything baked in that window arrives here.");
                FieldPerspectiveCamera.Cleanup();
                _fieldClosed = false;
                FieldSceneBundle.Reset();
                FieldSceneExport.Reset();
                _autoBundle = false;
                _autoBundleTried = false;
                _pendingBundleFile = null;
                _pendingBundleScene = null;

                if (!File.Exists(ConfigFileName))
                    return;

                Int32 lineNo = 0;
                // El fichero se relee en cada mapa, asi que el registro de ajustes ya aplicados
                // se vacia aqui o el aviso de duplicados saltaria a partir del segundo mapa.
                _appliedSettings.Clear();
                FieldPerspectiveCamera.DebugMask = false;
                FieldPerspectiveCamera.CatcherDebugMode = 0;
                FieldPerspectiveCamera.ShadowBias = -1f;
                FieldPerspectiveCamera.ShadowNormalBias = -1f;
                FieldPerspectiveCamera.AutoShadowDistance = true;
                foreach (String line in File.ReadAllLines(ConfigFileName))
                {
                    lineNo++;
                    if (IsBlankOrComment(line))
                        continue;
                    if (String.Equals(line.Trim(), TraceKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        _traceEnabled = true;
                        continue;
                    }
                    if (String.Equals(line.Trim(), DumpKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        _dumpEnabled = true;
                        continue;
                    }
                    if (String.Equals(line.Trim(), ProbeKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        ProbeRenderingCapabilities();
                        continue;
                    }
                    if (String.Equals(line.Trim(), CameraKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        _cameraCheckEnabled = true;
                        continue;
                    }
                    if (String.Equals(line.Trim(), ExportSceneKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        FieldSceneExport.Request();
                        continue;
                    }
                    if (TryApplySetting(line))
                        continue;
                    Entry entry;
                    if (!TryParseEntry(line, out entry))
                    {
                        Log.Warning($"[CustomFieldObjects] Invalid entry at line {lineNo}: {line}");
                        continue;
                    }
                    if (entry.MapNo != mapNo)
                        continue;
                    Spawn(fieldMap, entry, reference);
                }
            }
            catch (Exception err)
            {
                Log.Error(err, "[CustomFieldObjects] Failed to spawn custom field objects.");
            }
        }

        // The player controller does not exist yet when a field is initialized, so relative
        // coordinates fall back to the default character position stored in the walkmesh info.
        private static Vector3 GetReferencePosition(FieldMap fieldMap, out String source)
        {
            if (fieldMap.playerController != null)
            {
                source = "player";
                return fieldMap.playerController.transform.localPosition;
            }
            if (fieldMap.bgi != null && fieldMap.bgi.charPos != null)
            {
                source = "bgi.charPos (no player yet)";
                return fieldMap.bgi.charPos.ToVector3();
            }
            source = "origin (no player, no bgi)";
            return Vector3.zero;
        }

        /// <summary>
        /// Snapshots the player into the 3D pass, from LateUpdate rather than Update.
        ///
        /// The pose is only final after the animation stage, which runs between the two. Baking in
        /// Update copies the previous frame's pose, and while walking that reads as the proxy
        /// trailing the character by a few pixels — enough to see, since the proxy is what decides
        /// where the shadow falls and which pixels the catcher must leave alone.
        /// </summary>
        public static void LateUpdate(FieldMap fieldMap)
        {
            if (fieldMap == null || _fieldClosed)
                return;
            FieldPerspectiveCamera.RequestPlayerProxy(fieldMap, _playerProxyMode);
        }

        /// <summary>
        /// Tears down everything the mod put in the field. The game keeps its Unity scene across
        /// field transitions, so nothing here is destroyed on its own: without this the player
        /// proxy stays behind, and once the scene bundle is unloaded its shader goes with it and
        /// the leftover renderer draws in Unity's magenta error material.
        /// </summary>
        public static void ShutdownField()
        {
            _fieldClosed = true;
            FieldPerspectiveCamera.Cleanup();
            FieldSceneBundle.Reset();
            FieldSceneExport.Reset();
        }

        public static void UpdateTrace(FieldMap fieldMap)
        {
            if (fieldMap == null || fieldMap.playerController == null)
                return;

            // The player only exists a few frames after the field is initialized, so the
            // comparison against a real field character has to wait until here.
            if (_dumpPending)
            {
                _dumpPending = false;
                foreach (GameObject go in _spawned)
                    DumpObject("spawned", go);
                if (fieldMap.player != null)
                    DumpObject("player", fieldMap.player.gameObject);
                Transform mapTr = fieldMap.transform;
                Log.Message($"[CustomFieldObjects] DUMP fieldMap pos={mapTr.position} scale={mapTr.lossyScale} layer={mapTr.gameObject.layer} active={mapTr.gameObject.activeInHierarchy}");
                Camera cam = fieldMap.GetMainCamera();
                if (cam != null)
                    Log.Message($"[CustomFieldObjects] DUMP camera '{cam.name}' cullingMask=0x{cam.cullingMask:X} near={cam.nearClipPlane} far={cam.farClipPlane} depth={cam.depth}");
            }

            FieldPerspectiveCamera.Sync(fieldMap);
            if (_pendingBundleFile == null && _autoBundle && !_autoBundleTried)
            {
                _autoBundleTried = true;
                String byConvention = FF9StateSystem.Common.FF9.fldMapNo.ToString(CultureInfo.InvariantCulture) + ".unity3d";
                if (FieldSceneBundle.Exists(byConvention))
                    _pendingBundleFile = byConvention;
                else
                    Log.Message($"[CustomFieldObjects] No '{byConvention}' for this map, so nothing is added to the 3D pass.");
            }
            if (_pendingBundleFile != null)
            {
                String file = _pendingBundleFile;
                String scene = _pendingBundleScene;
                _pendingBundleFile = null;
                _pendingBundleScene = null;
                FieldSceneBundle.Request(file, scene);
            }
            FieldSceneBundle.Update();
            FieldPerspectiveCamera.ApplyShadowBias();
            FieldPerspectiveCamera.ApplyShadowDistance(fieldMap);
            FieldSceneExport.Update(fieldMap);

            if (_cameraCheckEnabled && Time.realtimeSinceStartup - _cameraCheckLastTime > 2f)
            {
                _cameraCheckLastTime = Time.realtimeSinceStartup;
                FieldPerspectiveCamera.LogSetup(fieldMap);
                FieldPerspectiveCamera.LogPlayerProxy(fieldMap);
                FieldPerspectiveCamera.LogCharacterTint();
                FieldPerspectiveCamera.LogRowScale();
                FieldPerspectiveCamera.LogProxyScales();
                FieldPerspectiveCamera.LogCharacterProjection(fieldMap);
                FieldPerspectiveCamera.LogProjectionError(fieldMap, "player", fieldMap.playerController.transform.localPosition);
                foreach (GameObject go in _spawned)
                {
                    if (go == null)
                        continue;
                    if (go.transform.parent == null)
                    {
                        foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>())
                            Log.Message($"[CustomFieldObjects] CAMERA lit '{go.name}' layer={go.layer} world={go.transform.position} visible={renderer.isVisible} bounds={renderer.bounds} shader='{renderer.sharedMaterial?.shader?.name}'");
                        continue;
                    }
                    FieldPerspectiveCamera.LogProjectionError(fieldMap, go.name, go.transform.localPosition);
                }
            }

            if (!_traceEnabled)
                return;
            if (Time.realtimeSinceStartup - _traceLastTime < TraceMinInterval)
                return;
            Vector3 pos = fieldMap.playerController.transform.localPosition;
            if ((pos - _traceLastPos).sqrMagnitude < TraceMinDistance * TraceMinDistance)
                return;
            _traceLastTime = Time.realtimeSinceStartup;
            _traceLastPos = pos;
            Single sceneScale = FieldPerspectiveCamera.SceneScale;
            String authored = sceneScale > 1f
                ? $"  (scene units: {pos.x / sceneScale:F2} {pos.y / sceneScale:F2} {pos.z / sceneScale:F2})"
                : String.Empty;
            Log.Message($"[CustomFieldObjects] TRACE map {FF9StateSystem.Common.FF9.fldMapNo} player at {pos.x:F0} {pos.y:F0} {pos.z:F0}{authored}");
        }

        private static void Spawn(FieldMap fieldMap, Entry entry, Vector3 reference)
        {
            GameObject go;
            if (String.Equals(entry.ModelName, PrimitiveCubeName, StringComparison.OrdinalIgnoreCase))
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            }
            else if (String.Equals(entry.ModelName, PrimitivePlaneName, StringComparison.OrdinalIgnoreCase))
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Plane);
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            }
            else
            {
                go = ModelFactory.CreateModel(entry.ModelName, false, true, Configuration.Graphics.ElementsSmoothTexture);
            }
            if (go == null)
            {
                Log.Warning($"[CustomFieldObjects] Model '{entry.ModelName}' could not be created.");
                return;
            }

            go.name = "CustomFieldObject_" + entry.ModelName;

            // Same setup as FieldMap.AddFieldChar and FieldCreatorScene.SetupDummy:
            // the object lives in the FieldMap's local space and uses the mirrored field scale.
            Vector3 fieldPosition = entry.IsRelative ? reference + entry.Position : entry.Position;
            if (entry.Lit)
            {
                // The 3D root carries the handedness mirror, so children use field coordinates.
                go.transform.parent = FieldPerspectiveCamera.GetOrCreateRoot();
                go.transform.localPosition = fieldPosition;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = new Vector3(entry.Scale, entry.Scale, entry.Scale);
            }
            else
            {
                go.transform.parent = fieldMap.transform;
                go.transform.localPosition = fieldPosition;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = new Vector3(-entry.Scale, -entry.Scale, entry.Scale);
            }

            if (entry.Lit)
                SetupLitObject(go);
            else
                SetupPsxObject(go);

            _spawned.Add(go);
            _dumpPending = _dumpEnabled;

            Vector3 spawnPos = fieldPosition;
            Log.Message($"[CustomFieldObjects] Spawned '{entry.ModelName}' at {spawnPos.x:F0} {spawnPos.y:F0} {spawnPos.z:F0} (scale {entry.Scale}{(entry.Lit ? ", lit" : "")}) world={go.transform.position}");
        }

        // Real-time shadows need built-in Unity shaders with a ShadowCaster pass: none of the
        // shaders shipped in StreamingAssets/Shaders has one, and Unity cannot compile Cg/HLSL at
        // runtime, so the only candidates are shaders that survived the build's shader stripping.
        private static readonly String[] ProbedShaders =
        {
            "Standard",
            "Diffuse",
            "Legacy Shaders/Diffuse",
            "Bumped Diffuse",
            "Legacy Shaders/Bumped Diffuse",
            "VertexLit",
            "Mobile/Diffuse",
            "Mobile/VertexLit",
            "Transparent/Cutout/Diffuse",
            "Legacy Shaders/Transparent/Cutout/Diffuse",
            "Unlit/Texture",
            "Unlit/Transparent Cutout",
        };

        private static void ProbeRenderingCapabilities()
        {
            // Unity 5.2 has no QualitySettings.shadows; shadowDistance == 0 is what actually disables them.
            Log.Message($"[CustomFieldObjects] PROBE shadowDistance={QualitySettings.shadowDistance} cascades={QualitySettings.shadowCascades} projection={QualitySettings.shadowProjection} pixelLights={QualitySettings.pixelLightCount}");
            Log.Message($"[CustomFieldObjects] PROBE shadowsSupported={SystemInfo.supportsShadows} renderTextures={SystemInfo.supportsRenderTextures} depthTex={SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Depth)} shaderLevel={SystemInfo.graphicsShaderLevel}");
            foreach (String name in ProbedShaders)
            {
                Shader shader = Shader.Find(name);
                if (shader == null)
                    Log.Message($"[CustomFieldObjects] PROBE shader '{name}': NOT FOUND (stripped from the build)");
                else
                    Log.Message($"[CustomFieldObjects] PROBE shader '{name}': found, supported={shader.isSupported}, queue={shader.renderQueue}");
            }
        }

        private static Texture2D _whiteTexture;

        private static Texture2D GetWhiteTexture()
        {
            if (_whiteTexture == null)
            {
                _whiteTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                Color[] pixels = new Color[4];
                for (Int32 i = 0; i < pixels.Length; i++)
                    pixels[i] = Color.white;
                _whiteTexture.SetPixels(pixels);
                _whiteTexture.Apply();
                _whiteTexture.name = "CustomFieldObjectsWhite";
            }
            return _whiteTexture;
        }

        private static void DumpObject(String tag, GameObject go)
        {
            if (go == null)
            {
                Log.Message($"[CustomFieldObjects] DUMP {tag}: <null>");
                return;
            }
            Log.Message($"[CustomFieldObjects] DUMP {tag} '{go.name}' active={go.activeInHierarchy} layer={go.layer} world={go.transform.position} local={go.transform.localPosition} scale={go.transform.lossyScale}");
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            Log.Message($"[CustomFieldObjects] DUMP {tag}: {renderers.Length} renderer(s)");
            foreach (Renderer renderer in renderers)
            {
                Mesh mesh = null;
                if (renderer is SkinnedMeshRenderer skinned)
                    mesh = skinned.sharedMesh;
                else
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null)
                        mesh = filter.sharedMesh;
                }
                Material mat = renderer.sharedMaterial;
                String meshInfo = mesh == null
                    ? "mesh=<null>"
                    : $"mesh='{mesh.name}' verts={mesh.vertexCount} tris={mesh.triangles.Length / 3} colors={mesh.colors?.Length ?? 0} uv={mesh.uv?.Length ?? 0}";
                String matInfo = mat == null
                    ? "mat=<null>"
                    : $"shader='{mat.shader?.name}' queue={mat.renderQueue} tex={(mat.mainTexture == null ? "<null>" : mat.mainTexture.name)} color={mat.GetColor("_Color")}";
                Log.Message($"[CustomFieldObjects] DUMP {tag}:   {renderer.GetType().Name} enabled={renderer.enabled} visible={renderer.isVisible} bounds={renderer.bounds.size} {meshInfo} {matInfo}");
            }
        }

        // "PSX/FieldMapActor" discards every pixel where (texture alpha * vertex color alpha)
        // is at or below 0.5. Unity feeds white when a mesh has no COLOR channel at all (FF9's own
        // field models have none and render fine), so the dangerous case is the opposite one: an
        // exporter that writes vertex colors with a zeroed alpha, which makes the model vanish.
        private static void EnsureVertexColors(Mesh mesh)
        {
            if (mesh == null || mesh.vertexCount == 0)
                return;
            Color[] colors = mesh.colors;
            if (colors == null || colors.Length != mesh.vertexCount)
            {
                colors = new Color[mesh.vertexCount];
                for (Int32 i = 0; i < colors.Length; i++)
                    colors[i] = Color.white;
                mesh.colors = colors;
                return;
            }
            Single maxAlpha = 0f;
            for (Int32 i = 0; i < colors.Length; i++)
                maxAlpha = Mathf.Max(maxAlpha, colors[i].a);
            if (maxAlpha > 0.5f)
                return; // Real vertex colors: leave the artist's data alone.
            for (Int32 i = 0; i < colors.Length; i++)
                colors[i].a = 1f;
            mesh.colors = colors;
        }

        // Drawn by the field's own orthographic camera through the PSX projection.
        private static void SetupPsxObject(GameObject go)
        {
            Shader shader = ShadersLoader.Find(Configuration.Shaders.FieldCharacterShader);
            foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>())
            {
                foreach (Material material in renderer.materials)
                {
                    if (shader != null)
                        material.shader = shader;
                    material.SetColor("_Color", Color.white);
                    // "PSX/FieldMapActor" kills every pixel where (texture alpha * vertex color
                    // alpha) <= 0.5. With no texture assigned the shader falls back to its declared
                    // "grey" default, whose alpha is not 1, and the whole model disappears.
                    if (material.mainTexture == null)
                        material.mainTexture = GetWhiteTexture();
                    // Depth clip plane; without it the property may default to 0 and clip the model.
                    if (material.HasProperty("_Slice"))
                        material.SetFloat("_Slice", 40960f);
                    // -1 = follow the shader's own queue, same as FieldMapActor.SetRenderQueue(-1)
                    material.renderQueue = -1;
                    ModelFactory.SetMatFilter(material, Configuration.Graphics.ElementsSmoothTexture);
                }
            }

            // Prevent mesh culling, since in PSX render mode the position depends not only on
            // UnityEngine.Camera but also on BGCAM_DEF.GetMatrixRT
            Bounds hugeBounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            foreach (MeshFilter meshFilter in go.GetComponentsInChildren<MeshFilter>())
            {
                // ".mesh" instantiates a copy: primitives share Unity's built-in meshes and
                // editing those would corrupt every other primitive in the game.
                Mesh mesh = meshFilter.mesh;
                EnsureVertexColors(mesh);
                mesh.bounds = hugeBounds;
            }
            foreach (SkinnedMeshRenderer skinnedRenderer in go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                // Meshes built by ModelImporter are created per instance, so they are safe to edit.
                if (skinnedRenderer.sharedMesh != null)
                    EnsureVertexColors(skinnedRenderer.sharedMesh);
                skinnedRenderer.localBounds = hugeBounds;
            }
        }

        // Drawn by the derived perspective camera with a standard Unity shader, so it takes part in
        // real lighting and shadow mapping. Bounds are deliberately left untouched here: frustum and
        // shadow culling need them to be correct.
        private static void SetupLitObject(GameObject go)
        {
            FieldPerspectiveCamera.SetLayerRecursively(go, FieldPerspectiveCamera.Layer3D);
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse") ?? Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null)
            {
                Log.Warning("[CustomFieldObjects] No standard lit shader survived the build; falling back to the PSX setup.");
                SetupPsxObject(go);
                return;
            }
            foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>())
            {
                renderer.castShadows = true;
                renderer.receiveShadows = true;
                foreach (Material material in renderer.materials)
                {
                    material.shader = shader;
                    material.renderQueue = -1;
                    if (material.HasProperty("_Color"))
                        material.SetColor("_Color", Color.white);
                    if (material.mainTexture == null)
                        material.mainTexture = GetWhiteTexture();
                }
            }
        }

        /// <summary>
        /// Global settings appearing twice are applied in order, so the last one silently wins.
        /// That is very hard to spot in a file with a long comment header, and it has already cost
        /// an afternoon: two PLAYER3D lines meant the game ran in a mode nobody had chosen.
        /// </summary>
        private static readonly HashSet<String> _appliedSettings = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

        // "LIGHT <eulerX> <eulerY> <eulerZ> [intensity]" and "SHADOWDISTANCE <units>"
        private static Boolean TryApplySetting(String line)
        {
            String[] token = line.Trim().Split(DataPatchers.SpaceSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (token.Length == 0)
                return false;
            if (!_appliedSettings.Add(token[0]))
                Log.Warning($"[CustomFieldObjects] '{token[0]}' is set more than once in MemoriaFieldObjects.txt. The last line wins: '{line.Trim()}'.");
            if (String.Equals(token[0], ShadowDistanceKeyword, StringComparison.OrdinalIgnoreCase) && token.Length >= 2)
            {
                if (String.Equals(token[1], "auto", StringComparison.OrdinalIgnoreCase))
                {
                    FieldPerspectiveCamera.AutoShadowDistance = true;
                    Log.Message("[CustomFieldObjects] Shadow distance: auto, measured from the camera on every map.");
                    return true;
                }
                // Un numero fijo apaga el automatico: manda lo que se escriba aqui.
                FieldPerspectiveCamera.AutoShadowDistance = false;
                if (TryParseSingle(token[1], out Single distance))
                    FieldPerspectiveCamera.SetShadowDistance(distance);
                return true;
            }
            if (String.Equals(token[0], SceneScaleKeyword, StringComparison.OrdinalIgnoreCase) && token.Length >= 2)
            {
                if (TryParseSingle(token[1], out Single sceneScale))
                    FieldPerspectiveCamera.SetSceneScale(sceneScale);
                return true;
            }
            if (String.Equals(token[0], SceneBundleKeyword, StringComparison.OrdinalIgnoreCase) && token.Length >= 2)
            {
                // "SCENEBUNDLE auto" carga <fldMapNo>.unity3d en cada mapa donde exista uno. Es lo
                // que evita tener que anadir una linea por mapa a un fichero que crece con el
                // juego entero: el nombre del archivo es la configuracion.
                if (String.Equals(token[1], "auto", StringComparison.OrdinalIgnoreCase))
                {
                    _autoBundle = true;
                    Log.Message("[CustomFieldObjects] Scene bundles: auto, looking for <fldMapNo>.unity3d on every map.");
                    return true;
                }
                // Deferred on purpose: the config is parsed while the field is still being built,
                // and the request has to happen once the 3D pass exists.
                if (token.Length >= 3 && Int32.TryParse(token[1], out Int32 bundleMapNo) && bundleMapNo == FF9StateSystem.Common.FF9.fldMapNo)
                {
                    _pendingBundleFile = token[2];
                    _pendingBundleScene = token.Length >= 4 ? token[3] : null;
                }
                return true;
            }
            if (String.Equals(token[0], AmbientKeyword, StringComparison.OrdinalIgnoreCase) && token.Length >= 4)
            {
                Color color = Color.black;
                if (!TryParseSingle(token[1], out color.r) || !TryParseSingle(token[2], out color.g) || !TryParseSingle(token[3], out color.b))
                    return true;
                color.a = 1f;
                Single intensity = 1f;
                if (token.Length >= 5)
                    TryParseSingle(token[4], out intensity);
                FieldPerspectiveCamera.SetAmbient(color, intensity);
                return true;
            }
            if (String.Equals(token[0], PlayerKeyword, StringComparison.OrdinalIgnoreCase) && token.Length >= 2)
            {
                if (String.Equals(token[1], "shadow", StringComparison.OrdinalIgnoreCase))
                    _playerProxyMode = FieldPerspectiveCamera.PlayerProxyMode.ShadowsOnly;
                else if (String.Equals(token[1], "full", StringComparison.OrdinalIgnoreCase))
                    _playerProxyMode = FieldPerspectiveCamera.PlayerProxyMode.Full;
                else if (String.Equals(token[1], "only", StringComparison.OrdinalIgnoreCase))
                    _playerProxyMode = FieldPerspectiveCamera.PlayerProxyMode.Only;
                else
                    _playerProxyMode = FieldPerspectiveCamera.PlayerProxyMode.Off;
                Log.Message($"[CustomFieldObjects] Player 3D proxy: {_playerProxyMode}");
                return true;
            }
            if (String.Equals(token[0], CatcherDebugKeyword, StringComparison.OrdinalIgnoreCase))
            {
                Int32 mode = 1;
                if (token.Length >= 2)
                {
                    if (String.Equals(token[1], "off", StringComparison.OrdinalIgnoreCase))
                        mode = 0;
                    else if (!Int32.TryParse(token[1], out mode))
                        mode = 1;
                }
                FieldPerspectiveCamera.CatcherDebugMode = Mathf.Clamp(mode, 0, 4);
                Log.Message($"[CustomFieldObjects] Catcher additive pass debug mode: {FieldPerspectiveCamera.CatcherDebugMode}");
                return true;
            }
            if (String.Equals(token[0], MaskDebugKeyword, StringComparison.OrdinalIgnoreCase))
            {
                FieldPerspectiveCamera.DebugMask = token.Length < 2
                    || !String.Equals(token[1], "off", StringComparison.OrdinalIgnoreCase);
                Log.Message($"[CustomFieldObjects] Mask debug: {(FieldPerspectiveCamera.DebugMask ? "ON, the proxy is drawn solid green over the game's own render" : "off")}");
                return true;
            }
            if (String.Equals(token[0], ShadowBiasKeyword, StringComparison.OrdinalIgnoreCase) && token.Length >= 2)
            {
                if (TryParseSingle(token[1], out Single bias))
                    FieldPerspectiveCamera.ShadowBias = bias;
                if (token.Length >= 3 && TryParseSingle(token[2], out Single normalBias))
                    FieldPerspectiveCamera.ShadowNormalBias = normalBias;
                Log.Message($"[CustomFieldObjects] Shadow bias {FieldPerspectiveCamera.ShadowBias}, normal bias {FieldPerspectiveCamera.ShadowNormalBias}");
                return true;
            }
            if (String.Equals(token[0], CharacterLightKeyword, StringComparison.OrdinalIgnoreCase) && token.Length >= 2)
            {
                Single influence;
                if (!TryParseSingle(token[1], out influence))
                    return true;
                // Sin Clamp01: por encima de 1 exagera el efecto, que es justo para lo que esta.
                FieldPerspectiveCamera.CharacterLightInfluence = Mathf.Max(0f, influence);
                Log.Message($"[CustomFieldObjects] Character light influence: {FieldPerspectiveCamera.CharacterLightInfluence}");
                return true;
            }
            if (String.Equals(token[0], LightKeyword, StringComparison.OrdinalIgnoreCase) && token.Length >= 4)
            {
                Vector3 euler = Vector3.zero;
                if (!TryParseSingle(token[1], out euler.x) || !TryParseSingle(token[2], out euler.y) || !TryParseSingle(token[3], out euler.z))
                    return true;
                Single intensity = 1f;
                if (token.Length >= 5)
                    TryParseSingle(token[4], out intensity);
                FieldPerspectiveCamera.GetOrCreateLight(euler, intensity);
                Log.Message($"[CustomFieldObjects] Directional light euler={euler} intensity={intensity}");
                return true;
            }
            return false;
        }

        private static Boolean IsBlankOrComment(String line)
        {
            String trimmed = line.Trim();
            return trimmed.Length == 0 || trimmed.StartsWith("#") || trimmed.StartsWith(";");
        }

        private static Boolean TryParseEntry(String line, out Entry entry)
        {
            entry = null;
            if (IsBlankOrComment(line))
                return false;
            String[] token = line.Trim().Split(DataPatchers.SpaceSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (token.Length < 5)
                return false;

            Entry result = new Entry();
            if (!Int32.TryParse(token[0], out result.MapNo))
                return false;
            result.ModelName = token[1];

            String xToken = token[2];
            if (xToken.StartsWith("@"))
            {
                result.IsRelative = true;
                xToken = xToken.Substring(1);
            }
            if (!TryParseSingle(xToken, out result.Position.x))
                return false;
            if (!TryParseSingle(token[3], out result.Position.y))
                return false;
            if (!TryParseSingle(token[4], out result.Position.z))
                return false;

            result.Scale = 1f;
            if (token.Length >= 6 && !TryParseSingle(token[5], out result.Scale))
                return false;
            for (Int32 i = 5; i < token.Length; i++)
                if (String.Equals(token[i], LitFlag, StringComparison.OrdinalIgnoreCase))
                    result.Lit = true;

            entry = result;
            return true;
        }

        private static Boolean TryParseSingle(String text, out Single value)
        {
            return Single.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private sealed class Entry
        {
            public Int32 MapNo;
            public String ModelName;
            public Vector3 Position;
            public Boolean IsRelative;
            public Single Scale;
            public Boolean Lit;
        }
    }
}
