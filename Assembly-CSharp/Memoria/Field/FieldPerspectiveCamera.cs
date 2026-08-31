using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Memoria.Field
{
    // Derives a real perspective camera from the field's own camera data, so custom 3D geometry can
    // be rendered with standard Unity shaders (lighting, shadow maps, lightmaps) and still line up
    // with the field.
    //
    // The field's Unity camera is orthographic and effectively 2D: FieldMap moves it only in X/Y
    // (see CenterCameraOnPlayer) and every field shader fakes the perspective in its vertex program
    // through _MatrixRT / _ViewDistance, emulating the PSX GTE. BGCAM_DEF however stores a genuine
    // 3D camera: a 3x3 rotation, a translation, and "proj", the GTE projection distance.
    //
    // Ground truth for the projection is PSX.CalculateGTE_RTPT:
    //     v = p; v.y = -v.y
    //     c = RT * v; c.y = -c.y
    //     screen.xy = c.xy * viewDist / |c.z| + projectionOffset
    // which FieldMap's orthographic camera then turns into pixels.
    //
    // Matching that with a perspective camera means:
    //     worldToCamera = Diag(1,-1,-1) * RT * Diag(1,-1,1)
    //         the first flip is PSX's Y-down convention, the second is Unity looking down -Z
    //     P00/P11 carry the projection scale, P02/P12 the screen pan as a lens shift. The pan
    //         belongs in the projection and not in the camera transform: moving the camera would
    //         change the perspective, while the game only shifts the framing.
    //
    // The pixel scale is measured from the orthographic camera rather than computed from
    // FieldMap.HalfFieldWidth: on narrow maps the field width is clamped (183 instead of 199 at
    // 1920x1080) and the camera's aspect is retuned per map, so assuming the mapping produced a
    // horizontal error that grew linearly with the distance to the screen centre.
    public static class FieldPerspectiveCamera
    {
        public const Single NearClip = 1f;
        public const Single FarClip = 40960f;

        /// <summary>
        /// How the orthographic field camera turns a PSX screen-space point into pixels.
        /// Measured instead of assumed: its "aspect" changes per map (narrow maps clamp the field
        /// width), so deriving the mapping from orthographicSize/aspect/Screen is not reliable.
        /// Sampling three points is exact, because an orthographic projection is affine.
        /// </summary>
        private struct OrthoMapping
        {
            public Single OriginX, OriginY;   // pixels for the PSX point (0,0)
            public Single ScaleX, ScaleY;     // pixels per PSX unit
            public Rect PixelRect;

            public static OrthoMapping Measure(Camera camera)
            {
                Vector3 atOrigin = camera.WorldToScreenPoint(Vector3.zero);
                Vector3 atUnitX = camera.WorldToScreenPoint(new Vector3(1f, 0f, 0f));
                Vector3 atUnitY = camera.WorldToScreenPoint(new Vector3(0f, 1f, 0f));
                return new OrthoMapping
                {
                    OriginX = atOrigin.x,
                    OriginY = atOrigin.y,
                    ScaleX = atUnitX.x - atOrigin.x,
                    ScaleY = atUnitY.y - atOrigin.y,
                    PixelRect = camera.pixelRect,
                };
            }
        }

        public static Boolean TryBuildMatrices(FieldMap fieldMap, out Matrix4x4 worldToCamera, out Matrix4x4 projection)
        {
            worldToCamera = Matrix4x4.identity;
            projection = Matrix4x4.identity;

            if (fieldMap == null || fieldMap.scene == null || fieldMap.mainCamera == null)
                return false;
            if (fieldMap.camIdx < 0 || fieldMap.camIdx >= fieldMap.scene.cameraList.Count)
                return false;

            BGCAM_DEF bgCamera = fieldMap.scene.cameraList[fieldMap.camIdx];
            Single viewDist = bgCamera.GetViewDistance();
            if (viewDist <= 0f)
                return false;

            OrthoMapping mapping = OrthoMapping.Measure(fieldMap.mainCamera);
            if (Mathf.Approximately(mapping.ScaleX, 0f) || Mathf.Approximately(mapping.ScaleY, 0f))
                return false;
            if (mapping.PixelRect.width <= 0f || mapping.PixelRect.height <= 0f)
                return false;

            Vector2 projectionOffset = fieldMap.GetProjectionOffset();

            Matrix4x4 flipY = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));
            Matrix4x4 toUnityView = Matrix4x4.Scale(new Vector3(1f, -1f, -1f));
            worldToCamera = toUnityView * bgCamera.GetMatrixRT() * flipY;

            // BGCAM's matrix is not a rotation: its rows carry a scale, and the Y one is 14/15,
            // the PSX 320x224 framebuffer shown at 4:3. FFIX keeps that inside the camera matrix so
            // the models line up with the pre-rendered art, which was painted for that proportion.
            //
            // A Unity camera cannot hold it. Sync() drives this camera through its transform, and
            // Quaternion.LookRotation orthonormalises whatever it is handed, so the scale is
            // silently dropped while the projection built below still assumes it. The result is a
            // character drawn about 7% off vertically from where the game's own shader puts him:
            // small enough to look like a mask that "does not quite fit", and impossible to see in
            // a diagnostic that compares the matrices to each other instead of to the camera.
            //
            // So the scale comes out of the view and goes into the projection, where it belongs.
            // Substituting x' = x/kx and z' = z/kz into ndc = (P00*x + P02*z) / -z gives
            // P00' = P00 * kx/kz, P02 unchanged, and P23' = P23 / kz for the depth row.
            Vector3 rowScale = new Vector3(
                new Vector3(worldToCamera[0, 0], worldToCamera[0, 1], worldToCamera[0, 2]).magnitude,
                new Vector3(worldToCamera[1, 0], worldToCamera[1, 1], worldToCamera[1, 2]).magnitude,
                new Vector3(worldToCamera[2, 0], worldToCamera[2, 1], worldToCamera[2, 2]).magnitude);
            if (rowScale.x <= 0f || rowScale.y <= 0f || rowScale.z <= 0f)
                return false;
            for (Int32 row = 0; row < 3; row++)
                for (Int32 column = 0; column < 4; column++)
                    worldToCamera[row, column] /= rowScale[row];
            _lastRowScale = rowScale;

            // PSX:    psx.x = viewDist * (vx / -vz) + offset.x        (and likewise for y)
            // pixels: screen.x = OriginX + psx.x * ScaleX
            // NDC:    ndc.x = 2 * (screen.x - rect.x) / rect.width - 1
            // Unity:  ndc.x = P00 * (vx / -vz) - P02
            Single pixelsToNdcX = 2f / mapping.PixelRect.width;
            Single pixelsToNdcY = 2f / mapping.PixelRect.height;

            projection = new Matrix4x4();
            projection[0, 0] = viewDist * mapping.ScaleX * pixelsToNdcX;
            projection[1, 1] = viewDist * mapping.ScaleY * pixelsToNdcY;
            projection[0, 2] = 1f - (mapping.OriginX + projectionOffset.x * mapping.ScaleX - mapping.PixelRect.x) * pixelsToNdcX;
            projection[1, 2] = 1f - (mapping.OriginY + projectionOffset.y * mapping.ScaleY - mapping.PixelRect.y) * pixelsToNdcY;
            projection[2, 2] = -(FarClip + NearClip) / (FarClip - NearClip);
            projection[2, 3] = -2f * FarClip * NearClip / (FarClip - NearClip);
            projection[3, 2] = -1f;

            // The scale that was taken out of the view, handed back here.
            projection[0, 0] *= rowScale.x / rowScale.z;
            projection[1, 1] *= rowScale.y / rowScale.z;
            projection[2, 3] /= rowScale.z;
            return true;
        }


        // ---- Runtime camera -------------------------------------------------------------------
        //
        // Custom 3D geometry lives on its own layer, drawn by a second camera that uses the matrices
        // derived above. It clears only the depth buffer and renders after the field camera, so the
        // existing field image stays underneath while the 3D pass composites on top.

        public const Int32 Layer3D = 30;

        /// <summary>
        /// The 3D pass uses field coordinates unchanged.
        ///
        /// The view matrix has determinant -1, which looks wrong at first sight but is exactly what
        /// a Unity camera needs: worldToCameraMatrix == Scale(1,1,-1) * transform.worldToLocalMatrix,
        /// so it is always negative. Mirroring the world to force +1 makes the matrix unrepresentable
        /// as a transform, and Quaternion.LookRotation then silently rebuilds the right axis
        /// backwards, which inverts left/right motion while leaving static objects looking correct.
        /// </summary>
        public static Vector3 FieldToWorld3D(Vector3 fieldPoint)
        {
            return fieldPoint;
        }

        public static Boolean TryBuild3D(FieldMap fieldMap, out Matrix4x4 worldToCamera, out Matrix4x4 projection)
        {
            return TryBuildMatrices(fieldMap, out worldToCamera, out projection);
        }

        /// <summary>Row scales taken out of BGCAM's matrix, kept for the diagnostics.</summary>
        private static Vector3 _lastRowScale = Vector3.one;

        private static Camera _camera3D;
        private static Light _light;
        private static Transform _root3D;
        private static Transform _sceneRoot3D;

        /// <summary>
        /// Field units are about 340 per metre (the player model is ~593 units tall), which breaks
        /// every Unity setting expressed "per unit": lightmap resolution, shadow distance, particle
        /// sizes, LOD bias. Authoring a scene in metres and scaling it here keeps those defaults
        /// meaningful, at the cost of dividing field coordinates by the factor when placing things.
        /// </summary>
        public static Single SceneScale { get; private set; } = 1f;
        private static readonly List<MeshRenderer> _playerProxies = new List<MeshRenderer>();
        private static readonly List<Material[]> _playerProxyMaterials = new List<Material[]>();
        private static readonly Int32 ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly Int32 LightInfluencePropertyId = Shader.PropertyToID("_LightInfluence");
        private static readonly Int32 ColorMaskPropertyId = Shader.PropertyToID("_ColorMask");
        private static readonly Int32 StencilRefPropertyId = Shader.PropertyToID("_StencilRef");
        private static readonly Int32 StencilCompPropertyId = Shader.PropertyToID("_StencilComp");
        private static readonly Int32 StencilOpPropertyId = Shader.PropertyToID("_StencilOp");
        // ShaderLab values: Comp Always = 8, Op Replace = 2, Op Keep = 0.
        private const Single StencilAlways = 8f;
        private const Single StencilReplace = 2f;
        private const Single StencilKeep = 0f;
        private static readonly Int32 ModulatePropertyId = Shader.PropertyToID("_Modulate");
        private static readonly Int32 SrcBlendPropertyId = Shader.PropertyToID("_SrcBlend");
        private static readonly Int32 DstBlendPropertyId = Shader.PropertyToID("_DstBlend");

        /// <summary>
        /// Queue for the character's depth mask. It has to land before the shadow catcher, which
        /// sits at Geometry-1, or the catcher would already have multiplied those pixels.
        /// </summary>
        private const Int32 DepthMaskQueue = 1998;

        /// <summary>How far to look for something blocking a directional light, in field units.</summary>
        private const Single SunProbeDistance = 20000f;

        private static readonly Dictionary<Renderer, Transform> _characterOwner = new Dictionary<Renderer, Transform>();
        private static readonly Dictionary<Transform, Color> _tintByOwner = new Dictionary<Transform, Color>();
        private static readonly MaterialPropertyBlock _tintBlock = new MaterialPropertyBlock();
        private static Color _lastTint = Color.white;
        private static Boolean _tintApplied;
        private static Boolean _tintHadBlocker;
        private static Color _probeTotal = Color.white;
        private static Color _probeReference = Color.white;
        private static Color _lastTotal = Color.white;
        private static Color _lastReference = Color.white;

        private static Boolean _lastReportedMask;
        private static PlayerProxyMode _lastReportedMode = (PlayerProxyMode)(-1);

        /// <summary>
        /// Name of the shader that reproduces the game's own colour arithmetic for a field actor.
        /// It cannot be compiled at runtime, so it has to travel inside the scene bundle, already
        /// built by the 5.2.3 editor.
        /// </summary>
        public const String CharacterShaderName = "Memoria/FieldActorLit";

        /// <summary>Material harvested from the scene bundle, used as the template for the proxy.</summary>
        public static Material CharacterMaterial { get; set; }

        /// <summary>
        /// The single dial for how much the scene's lighting shows on the characters.
        ///
        /// It is a gain on the DEPARTURE from normal, not a blend: 0 leaves the game's own render
        /// untouched, 1 applies the lighting exactly as the scene computes it, and above 1
        /// exaggerates it. Deliberately unbounded — the physical amount of light a torch throws is
        /// rarely the amount that reads well on a character, and the alternative was multiplying
        /// the lights themselves, which means a tuning value per map instead of one for the game.
        /// The scene keeps its authored values; this decides how much of them reach the character.
        /// </summary>
        public static Single CharacterLightInfluence = 1f;

        /// <summary>
        /// Draws the character proxy in solid green instead of writing depth only.
        ///
        /// It is the one way to tell apart the two things that look identical on screen: a mask
        /// whose silhouette does not match what the game draws, and a shadow map too coarse or too
        /// biased for the character. The proxy is drawn over the game's own render, so any green
        /// spilling past his outline is the mask being wrong, and an outline that fits exactly
        /// clears the mask and points at the shadow instead.
        /// </summary>
        public static Boolean DebugMask;

        /// <summary>
        /// Whether the shadow distance is worked out per map instead of being a fixed number.
        ///
        /// A fixed one cannot be right for every field: the camera sits 3000 field units from the
        /// player in one map and 6400 in another, and anything past the limit simply has no shadow.
        /// Worse, it fails quietly — the geometry, the lights and the shader are all fine and
        /// nothing casts. Measuring it from the camera removes the guess.
        /// </summary>
        public static Boolean AutoShadowDistance = true;

        /// <summary>
        /// Diagnostic mode for the catcher's additive pass: 0 off, 1 flat red, 2 the shadow term
        /// alone, 3 the reach term alone, 4 the final factor. The pass darkens by
        /// reach * (1 - shadow), and when nothing darkens those terms fail in different ways and
        /// need different fixes.
        /// </summary>
        public static Int32 CatcherDebugMode;

        /// <summary>Shadow bias for the 3D lights, or negative to leave whatever they came with.</summary>
        public static Single ShadowBias = -1f;
        public static Single ShadowNormalBias = -1f;
        private static readonly List<Mesh> _playerProxyMeshes = new List<Mesh>();

        public enum PlayerProxyMode
        {
            Off,
            /// <summary>Invisible in the 3D pass, but present in the shadow map.</summary>
            ShadowsOnly,
            /// <summary>Drawn in the 3D pass on top of the PSX original (useful to compare both).</summary>
            Full,
            /// <summary>Drawn only in the 3D pass: the PSX renderers are switched off, so the
            /// character shares one real depth buffer with the 3D geometry.</summary>
            Only,
        }

        /// <summary>
        /// Container for scene-bundle content, scaled from authoring units to field units. It is a
        /// child of the 3D root so it inherits nothing else; objects placed directly on the root
        /// (lit objects, the player proxy) stay in field coordinates.
        /// </summary>
        public static Transform GetOrCreateSceneRoot()
        {
            // Resolved first and on its own line: creating the root clears _sceneRoot3D, so calling
            // this inside SetParent would null the field right after it was assigned.
            Transform root = GetOrCreateRoot();
            if (_sceneRoot3D == null)
            {
                GameObject go = new GameObject("Field3D Scene");
                _sceneRoot3D = go.transform;
                _sceneRoot3D.SetParent(root, false);
                _sceneRoot3D.localPosition = Vector3.zero;
                _sceneRoot3D.localRotation = Quaternion.identity;
            }
            _sceneRoot3D.localScale = Vector3.one * SceneScale;
            return _sceneRoot3D;
        }

        public static void SetSceneScale(Single scale)
        {
            SceneScale = scale <= 0f ? 1f : scale;
            if (_sceneRoot3D != null)
                _sceneRoot3D.localScale = Vector3.one * SceneScale;
            Log.Message($"[FieldPerspectiveCamera] Scene scale = {SceneScale} field units per authoring unit. A field position P is authored at P/{SceneScale}.");
        }

        /// <summary>Parent for everything the 3D pass draws, in plain field coordinates.</summary>
        public static Transform GetOrCreateRoot()
        {
            if (_root3D == null)
            {
                GameObject go = new GameObject("Field3D Root");
                _root3D = go.transform;
                _root3D.position = Vector3.zero;
                _root3D.rotation = Quaternion.identity;
                _root3D.localScale = Vector3.one;
                _sceneRoot3D = null;
                ForgetProxies();
                Log.Message("[FieldPerspectiveCamera] Created the 3D root (field coordinates).");
            }
            return _root3D;
        }

        /// <summary>
        /// Drops every piece of proxy state at once.
        ///
        /// The four collections are indexed in lockstep, so they have to be cleared together or
        /// not at all. Clearing only the renderers and the meshes -as this used to- leaves the
        /// materials from the previous map lined up against a different cast of characters: proxy 0
        /// keeps the moogle's texture while it is now Steiner, and since the mask cuts by that
        /// texture's alpha, the silhouette comes out of the wrong character. It shows as the mask
        /// breaking after leaving a map and coming back, and only then, which is a long way to walk
        /// from the cause.
        ///
        /// The root is destroyed by the game on a field transition, so this is the one place that
        /// catches every path in and out, whether or not the shutdown hook ran.
        /// </summary>
        private static void ForgetProxies()
        {
            _playerProxies.Clear();
            _playerProxyMeshes.Clear();
            _playerProxyMaterials.Clear();
            _characterOwner.Clear();
            _tintByOwner.Clear();
        }

        /// <summary>How many proxies are still alive. Entering a field it must be zero.</summary>
        public static Int32 ProxyCount => _playerProxies.Count;

        public static Camera Camera3D => _camera3D;

        /// <summary>True for the objects this class creates, so they are never mistaken for
        /// content coming from a loaded scene.</summary>
        public static Boolean IsOwnObject(Transform transform)
        {
            if (transform == null)
                return false;
            if (_root3D != null && transform == _root3D)
                return true;
            if (_sceneRoot3D != null && transform == _sceneRoot3D)
                return true;
            if (_camera3D != null && transform == _camera3D.transform)
                return true;
            if (_light != null && transform == _light.transform)
                return true;
            return false;
        }

        /// <summary>
        /// Rebuilds a static copy of the player's skinned meshes on the 3D layer, so the character
        /// takes part in shadow mapping. SkinnedMeshRenderer.BakeMesh snapshots the posed mesh each
        /// frame; ShadowsOnly keeps the PSX-rendered original on screen while its silhouette lands
        /// on the 3D geometry.
        /// </summary>
        private static FieldMap _pendingFieldMap;
        private static PlayerProxyMode _pendingMode;

        /// <summary>
        /// Records what to snapshot; the snapshot itself happens in OnPreCull.
        ///
        /// LateUpdate is not late enough. Script execution order between MonoBehaviours is
        /// undefined, so an actor whose animation the game advances in its own LateUpdate can be
        /// posed after ours. The player never showed it because he stands still; a moogle's pompom
        /// swings every frame, and there the lag reads as the proxy sitting beside the character
        /// rather than on him.
        ///
        /// OnPreCull on the 3D camera is the last moment before anything is drawn, so whatever pose
        /// the game is about to render is the pose that gets baked.
        /// </summary>
        public static void RequestPlayerProxy(FieldMap fieldMap, PlayerProxyMode mode)
        {
            _pendingFieldMap = fieldMap;
            _pendingMode = mode;
            if (_camera3D != null && _camera3D.GetComponent<ProxySyncOnPreCull>() == null)
                _camera3D.gameObject.AddComponent<ProxySyncOnPreCull>();
        }

        /// <summary>Drives the snapshot from the 3D camera, immediately before it culls and draws.</summary>
        private sealed class ProxySyncOnPreCull : MonoBehaviour
        {
            private void OnPreCull()
            {
                if (_pendingFieldMap != null)
                    SyncPlayerProxy(_pendingFieldMap, _pendingMode);
            }
        }

        public static void SyncPlayerProxy(FieldMap fieldMap, PlayerProxyMode mode)
        {
            if (mode == PlayerProxyMode.Off || fieldMap == null || fieldMap.player == null)
            {
                foreach (MeshRenderer proxy in _playerProxies)
                    if (proxy != null)
                        proxy.gameObject.SetActive(false);
                if (fieldMap?.player != null)
                    foreach (SkinnedMeshRenderer restored in fieldMap.player.GetComponentsInChildren<SkinnedMeshRenderer>())
                        restored.enabled = true;
                return;
            }

            // The root first. Creating it clears the proxy state -the five collections run in
            // parallel and are only useful together- so asking for it AFTER collecting the
            // characters wiped the owner map that had just been built, on the first frame of every
            // map: the tint was not applied that time and the diagnostics listed nobody.
            Transform root = GetOrCreateRoot();
            SkinnedMeshRenderer[] sources = CollectFieldCharacters(fieldMap);
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");

            for (Int32 i = 0; i < sources.Length; i++)
            {
                while (_playerProxies.Count <= i)
                {
                    GameObject go = new GameObject($"Field3D PlayerProxy{_playerProxies.Count}");
                    go.transform.parent = root;
                    go.layer = Layer3D;
                    go.AddComponent<MeshFilter>();
                    _playerProxies.Add(go.AddComponent<MeshRenderer>());
                    _playerProxyMeshes.Add(new Mesh());
                }

                SkinnedMeshRenderer source = sources[i];
                MeshRenderer proxy = _playerProxies[i];
                if (proxy == null)
                    continue;
                Mesh baked = _playerProxyMeshes[i];
                if (baked == null)
                    _playerProxyMeshes[i] = baked = new Mesh();

                // In "Only" mode this code is what switches the source renderer off, so its
                // "enabled" flag can no longer be read as the game's intent: doing so would make the
                // proxy deactivate itself on the very next frame.
                Boolean sourceIsShown = mode == PlayerProxyMode.Only || source.enabled;
                proxy.gameObject.SetActive(sourceIsShown && source.gameObject.activeInHierarchy);
                if (!proxy.gameObject.activeSelf)
                    continue;

                source.BakeMesh(baked);
                proxy.GetComponent<MeshFilter>().sharedMesh = baked;

                // FieldMap's transform is the identity (position 0, no rotation, unit scale), so the
                // renderer's world transform can be reused verbatim as a local transform under the
                // mirrored root. The scale is deliberately left at one: BakeMesh already applies the
                // renderer's own scale to the snapshot, and the player's is (-1,-1,1), so copying it
                // would mirror the body a second time and sink it under the floor.
                proxy.transform.localPosition = source.transform.position;
                proxy.transform.localRotation = source.transform.rotation;
                proxy.transform.localScale = Vector3.one;

                // In "shadow" the character is drawn by the game in the PSX pass, before any of
                // this. The catcher, which runs afterwards over a freshly cleared z-buffer, does not
                // know somebody is in front and multiplies the shadows on top of them. The fix is to
                // draw the proxy as a depth mask: same silhouette, same alpha cutout, without
                // writing a single pixel of colour. The catcher finds it in the z-buffer and does
                // not paint there. It needs a material that can do this; without one it falls back
                // to the older mode. With the character drawn by the game, the proxy has two roles:
                //   CHARLIGHT 0  -> pure mask, writes no colour and the pixel is left untouched
                //   CHARLIGHT >0 -> it also modulates: multiplies what the game painted by the
                //                   lighting factor, so the character darkens in shadow and takes
                //                   the tint of nearby lights while still being the game's own
                //                   drawing.
                Boolean depthMask = mode == PlayerProxyMode.ShadowsOnly && CanDepthMask();
                // There used to be a mode here that modulated the character pixels by blending
                // over what the game had painted. It was removed: the proxy multiplies whatever it
                // FINDS in those pixels, and cannot know whether the game drew the character there
                // or an NPC walking in front. With a moogle in the way, a dark ghost of Steiner
                // showed on top of it. Lighting now goes through ApplyCharacterTint, on the game's
                // own material.
                Boolean modulate = false;
                if (_lastReportedMask != depthMask || _lastReportedMode != mode)
                {
                    _lastReportedMask = depthMask;
                    _lastReportedMode = mode;
                    Log.Message($"[FieldPerspectiveCamera] Player proxy: mode {mode}, depth mask {(depthMask ? "ON" : "off")}, modulation {(modulate ? $"ON (CHARLIGHT {CharacterLightInfluence})" : "off")}, material '{(CharacterMaterial != null ? CharacterMaterial.shader.name : "Standard fallback")}'.");
                }
                SyncProxyMaterials(source, proxy, i, shader, depthMask, modulate);
                proxy.shadowCastingMode = mode == PlayerProxyMode.ShadowsOnly && !depthMask
                    ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly
                    : UnityEngine.Rendering.ShadowCastingMode.On;
                // When modulating, the shadow it receives IS the effect: without this Unity
                // compiles the no-shadow variant and the character never darkens.
                proxy.receiveShadows = mode != PlayerProxyMode.ShadowsOnly || modulate;
                // In "Only" the characters leave the PSX pass entirely, which is what makes their
                // depth comparable with the 3D geometry instead of always losing to it.
                source.enabled = mode != PlayerProxyMode.Only;
            }

            for (Int32 i = sources.Length; i < _playerProxies.Count; i++)
                if (_playerProxies[i] != null)
                    _playerProxies[i].gameObject.SetActive(false);

            ApplyCharacterTint(sources);
        }

        // There was an attempt here to correct the proxy scale with lossy/local. It was wrong:
        // BakeMesh applies the renderer's WORLD scale, not its local one, so Vector3.one is always
        // already correct and that code applied the factor twice. It flattened both characters.

        /// <summary>
        /// Every character on the field, not just the player.
        ///
        /// They all need a proxy for the same reason: the shadow catcher multiplies whatever is
        /// already in the framebuffer, and the game draws its characters standing on the floor. A
        /// moogle without a proxy has no depth in the 3D pass, so the shadow cast across the floor
        /// gets painted over him like a decal. With a proxy he is in front of the floor and the
        /// catcher leaves him alone — and he casts a shadow of his own into the bargain.
        /// </summary>
        private static SkinnedMeshRenderer[] CollectFieldCharacters(FieldMap fieldMap)
        {
            List<SkinnedMeshRenderer> collected = new List<SkinnedMeshRenderer>();
            _characterOwner.Clear();

            foreach (FF9Char character in FF9StateSystem.Common.FF9.charArray.Values)
            {
                if (character?.geo == null || !character.geo.activeInHierarchy)
                    continue;
                AddCharacter(character.geo.transform, collected);
            }

            // charArray is the party and the field's actors, but the player is worth making sure
            // of: on some maps he is driven separately.
            if (fieldMap.player != null)
                AddCharacter(fieldMap.player.transform, collected);
            return collected.ToArray();
        }

        private static void AddCharacter(Transform owner, List<SkinnedMeshRenderer> collected)
        {
            foreach (SkinnedMeshRenderer renderer in owner.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (renderer == null || _characterOwner.ContainsKey(renderer))
                    continue;
                _characterOwner[renderer] = owner;
                collected.Add(renderer);
            }
        }

        /// <summary>
        /// Lights the character by tinting the material the game already draws him with, instead of
        /// drawing anything of our own.
        ///
        /// The game's actor shader computes tex * (vertexColour * _Color), so multiplying _Color is
        /// exactly the knob it already understands: the character keeps his own colours, his own
        /// alpha cut and his own ordering against everything else on screen, and simply gets darker
        /// or picks up a nearby light's hue.
        ///
        /// It is one colour for the whole character rather than per pixel. For FFIX that is the
        /// right resolution anyway — the field models are flat-lit — and it is the only approach
        /// that cannot paint over whatever else the game happens to be drawing at those pixels.
        ///
        /// The value is written through a MaterialPropertyBlock and read back from the material
        /// every frame. That way the game stays the owner of _Color: if it fades the character out,
        /// the fade is what we read and multiply, and nothing accumulates.
        /// </summary>
        private static void ApplyCharacterTint(Renderer[] sources)
        {
            if (sources == null || sources.Length == 0)
                return;

            if (CharacterLightInfluence <= 0f)
            {
                if (!_tintApplied)
                    return;
                _tintApplied = false;
                foreach (Renderer source in sources)
                    if (source != null)
                        source.SetPropertyBlock(null);
                return;
            }

            _tintApplied = true;
            _tintByOwner.Clear();

            foreach (Renderer source in sources)
            {
                if (source == null)
                    continue;

                // One colour per CHARACTER, not per renderer. A character is made of several
                // (body, arms, cape) and each has its own transform: computing the light at each
                // one's position means the ray is blocked for one and not for the next, and then
                // half the figure darkens with a hard edge along the seam.
                Transform owner = _characterOwner.TryGetValue(source, out Transform found) ? found : source.transform;
                if (!_tintByOwner.TryGetValue(owner, out Color tint))
                {
                    tint = ComputeCharacterTint(owner.position);
                    _tintByOwner[owner] = tint;
                }
                if (source == sources[0])
                {
                    _lastTint = tint;
                    _lastTotal = _probeTotal;
                    _lastReference = _probeReference;
                }
                Material material = source.sharedMaterial;
                Color baseColor = material != null && material.HasProperty(ColorPropertyId)
                    ? material.GetColor(ColorPropertyId)
                    : Color.white;
                _tintBlock.Clear();
                _tintBlock.SetColor(ColorPropertyId, new Color(
                    baseColor.r * tint.r, baseColor.g * tint.g, baseColor.b * tint.b, baseColor.a));
                source.SetPropertyBlock(_tintBlock);
            }
        }

        /// <summary>
        /// The lighting reaching a point, as a multiplier around white.
        ///
        /// Shadowing is a raycast against the 3D layer rather than a shadow-map lookup, which is not
        /// reachable from script. It therefore needs colliders on the scenario geometry: tick
        /// "Generate Colliders" when importing the FBX in Unity. Without them nothing ever blocks
        /// the light and only the point lights have any effect, which is said in the log.
        /// </summary>
        private static Color ComputeCharacterTint(Vector3 worldPosition)
        {
            // Roughly chest height, so the character is not shadowed by the floor he stands on.
            Vector3 probe = worldPosition + Vector3.up * 300f;

            // Two sums. "total" is the light actually arriving here; "reference" is what would
            // arrive standing in the open with no nearby lamp: ambient plus every directional at
            // full strength.
            //
            // Dividing one by the other is what makes CHARLIGHT mean something. Scaling the raw
            // total instead scales the whole base along with it, so raising the dial to notice a
            // torch also brightens the character everywhere. Against the reference, an unshadowed
            // character with no lamp near comes out at exactly 1 whatever the ambient and the
            // directional are worth, and the dial then amplifies only the departures from that:
            // going into shadow, or stepping up to a flame.
            Color total = RenderSettings.ambientLight;
            Color reference = RenderSettings.ambientLight;

            Transform root = _root3D;
            if (root == null)
                return Color.Lerp(Color.white, total, CharacterLightInfluence);

            Int32 mask = 1 << Layer3D;
            Boolean anyBlocker = false;
            foreach (Light light in root.GetComponentsInChildren<Light>())
            {
                if (light == null || !light.enabled || light.intensity <= 0f)
                    continue;

                if (light.type == LightType.Directional)
                {
                    Vector3 toLight = -light.transform.forward;
                    Color contribution = light.color * light.intensity;
                    reference += contribution;
                    Boolean blocked = Physics.Raycast(probe, toLight, SunProbeDistance, mask);
                    anyBlocker |= blocked;
                    if (blocked)
                        continue;
                    total += contribution;
                    continue;
                }

                Vector3 delta = light.transform.position - probe;
                Single distance = delta.magnitude;
                if (light.range <= 0f || distance >= light.range)
                    continue;
                // Same falloff shape Unity uses for its vertex lights: smooth, and zero at range.
                Single attenuation = 1f - distance / light.range;
                attenuation *= attenuation;
                if (light.type == LightType.Spot)
                {
                    Single angle = Vector3.Angle(light.transform.forward, -delta.normalized);
                    if (angle > light.spotAngle * 0.5f)
                        continue;
                }
                if (Physics.Raycast(probe, delta.normalized, distance, mask))
                {
                    anyBlocker = true;
                    continue;
                }
                total += light.color * light.intensity * attenuation;
            }

            _tintHadBlocker = anyBlocker;
            // Stored separately, and the caller decides whose they are: this runs once per
            // character, so assigning them here leaves the last one processed in the log next to the
            // player's tint, two figures that do not belong together and do not add up.
            _probeTotal = total;
            _probeReference = reference;

            // Two different effects, with different references.
            //
            // Darkening in shadow is measured against the light that would arrive with nothing in
            // the way. It is a fraction, never above 1, and dividing is the right thing.
            //
            // Taking on the tint of a nearby lamp cannot be measured that way. Dividing gives a
            // number with no ceiling, and on a map lit only by spotlights -no directional- the
            // reference collapses to pure ambient: with AMBIENT 0.15 the tint came out at 4.4, the
            // character four times brighter than the background they are standing on. Ambient does
            // not represent what is normal there; the background already shows a lit room.
            //
            // So the lamps' contribution goes through a saturating function: x/(x+ref) runs from 0
            // to 1 however far x climbs, and it stops mattering that the reference is small. A
            // brutal lamp brightens by at most a factor of two, not four.
            Single gain = CharacterLightInfluence;
            return new Color(
                Modulate(total.r, reference.r, gain),
                Modulate(total.g, reference.g, gain),
                Modulate(total.b, reference.b, gain),
                1f);
        }

        private static Single Modulate(Single arriving, Single reference, Single gain)
        {
            Single lamp = Mathf.Max(0f, arriving - reference);
            Single reaching = Mathf.Min(arriving, reference);
            Single shadowRatio = reference > 0.0001f ? reaching / reference : 1f;
            Single lampGain = lamp > 0f ? lamp / (lamp + Mathf.Max(reference, 0.0001f)) : 0f;
            return (1f + (shadowRatio - 1f) * gain) * (1f + lampGain * gain);
        }

        /// <summary>
        /// Sets the shadow distance to cover the camera, the player and every light in the scene.
        ///
        /// Twice the distance to the farthest of them: enough margin for the player to walk to the
        /// other end of the map without leaving the range, and for Unity's fade near the limit not
        /// to eat the shadow. Costs resolution in a huge map, which is the honest trade for shadows
        /// that exist at all.
        /// </summary>
        public static void ApplyShadowDistance(FieldMap fieldMap)
        {
            if (!AutoShadowDistance || _camera3D == null)
                return;

            Vector3 eye = _camera3D.transform.position;
            Single farthest = 0f;
            if (fieldMap?.player != null)
                farthest = Vector3.Distance(eye, fieldMap.player.transform.position);
            if (_root3D != null)
            {
                foreach (Light light in _root3D.GetComponentsInChildren<Light>())
                {
                    if (light == null || light.type == LightType.Directional)
                        continue;
                    farthest = Mathf.Max(farthest, Vector3.Distance(eye, light.transform.position) + light.range);
                }
            }
            if (farthest <= 0f)
                return;

            Single wanted = farthest * 2f;
            if (Mathf.Abs(QualitySettings.shadowDistance - wanted) < farthest * 0.1f)
                return;
            Single before = QualitySettings.shadowDistance;
            QualitySettings.shadowDistance = wanted;
            Log.Message($"[FieldPerspectiveCamera] Shadow distance {before:F0} -> {wanted:F0}: the farthest thing that has to cast is {farthest:F0} field units from the camera. Past the limit nothing casts, and it fails without saying anything.");
        }

        public static void ApplyShadowBias()
        {
            if (_root3D == null || (ShadowBias < 0f && ShadowNormalBias < 0f))
                return;
            foreach (Light light in _root3D.GetComponentsInChildren<Light>())
            {
                if (light == null)
                    continue;
                if (ShadowBias >= 0f)
                    light.shadowBias = ShadowBias;
                if (ShadowNormalBias >= 0f)
                    light.shadowNormalBias = ShadowNormalBias;
            }
        }

        /// <summary>
        /// One line per character with the scales involved, so a proxy that does not line up can be
        /// told apart from a camera that does not: if lossy and local differ, the proxy needs the
        /// correction and it is visible right here.
        /// </summary>
        /// <summary>
        /// Projection error for every character, not just the player.
        ///
        /// The player matching while another character does not is the one thing that separates a
        /// camera problem from a per-character one, and the eye cannot tell how much. This gives
        /// the number for each of them.
        /// </summary>
        public static void LogCharacterProjection(FieldMap fieldMap)
        {
            HashSet<Transform> owners = new HashSet<Transform>();
            foreach (KeyValuePair<Renderer, Transform> entry in _characterOwner)
                if (entry.Value != null)
                    owners.Add(entry.Value);
            foreach (Transform owner in owners)
                LogProjectionError(fieldMap, $"actor '{owner.name}'", owner.position);

            // And now what is actually missing: a point on the MESH, away from the origin.
            //
            // Each actor's origin projecting exactly says nothing about the rest of their body: a
            // scale error in the projection leaves the centre pinned and displaces everything else,
            // the more so the further out. The centre of the proxy bounds is a real point on the
            // mesh, and the game draws that same mesh, so comparing there separates "the projection
            // fails away from the centre" from "the mesh is not where the game puts it".
            for (Int32 i = 0; i < _playerProxies.Count; i++)
            {
                MeshRenderer proxy = _playerProxies[i];
                if (proxy == null || !proxy.gameObject.activeInHierarchy)
                    continue;
                LogProjectionError(fieldMap, $"mesh centre of proxy{i}", proxy.bounds.center);
            }
        }

        public static void LogProxyScales()
        {
            foreach (KeyValuePair<Renderer, Transform> entry in _characterOwner)
            {
                Renderer source = entry.Key;
                if (source == null)
                    continue;
                // What the game applies versus what we apply. The proxy is placed with position +
                // rotation and unit scale, on the assumption that BakeMesh already folded in the
                // world scale. With negative scales, Unity's decomposition into rotation and
                // lossyScale is not guaranteed to rebuild the matrix, and then the proxy comes out
                // rotated or mirrored with respect to what the game draws. This reports it as a
                // number instead of leaving it to the eye.
                Matrix4x4 gameMatrix = source.transform.localToWorldMatrix;
                Matrix4x4 proxyMatrix = Matrix4x4.TRS(source.transform.position, source.transform.rotation, Vector3.one)
                    * Matrix4x4.Scale(source.transform.lossyScale);
                Single worst = 0f;
                for (Int32 row = 0; row < 4; row++)
                    for (Int32 column = 0; column < 4; column++)
                        worst = Mathf.Max(worst, Mathf.Abs(gameMatrix[row, column] - proxyMatrix[row, column]));
                // 0.02 and not 0.001: a rotation of 0.001 is 0.06 degrees, floating-point noise
                // that moves no pixel. Flagging that as a problem only misleads.
                Log.Message($"[FieldPerspectiveCamera] Proxy '{source.name}' of '{entry.Value?.name}': lossy {source.transform.lossyScale}, matrix mismatch {worst:F4}{(worst > 0.02f ? "  <-- THE PROXY CANNOT REPRODUCE THIS TRANSFORM" : "")}");
            }
        }

        public static void LogRowScale()
        {
            Log.Message($"[FieldPerspectiveCamera] BGCAM row scale {_lastRowScale} (Y should be about 14/15 = 0.9333, the PSX 320x224 aspect). It is folded into the projection because the camera transform cannot carry it.");
        }

        public static void LogCharacterTint()
        {
            // "hit nothing" is not a problem: it is the normal case when the character is in plain
            // sight of the lights. Saying "colliders missing" there is a constant false positive,
            // and the one that really knows whether there are colliders is the scene loader, which
            // already says so once.
            Log.Message($"[FieldPerspectiveCamera] Character light: arriving {_lastTotal}, reference {_lastReference}, tint {_lastTint} (CHARLIGHT {CharacterLightInfluence}), shadow probe {(_tintHadBlocker ? "blocked" : "clear")}.");
        }

        /// <summary>
        /// Mirrors the source renderer's materials onto the proxy, one per submesh.
        ///
        /// BakeMesh keeps the submeshes, so copying only sharedMaterial draws every submesh with
        /// the first one's texture: a character whose face, body and weapon come from different
        /// atlases comes out visibly wrong.
        ///
        /// _Color matters as much as the texture. The game's actor shader computes
        /// tex * (vertexColour * _Color), and _Color is where the field's tint lives, so dropping
        /// it makes the character ignore the lighting of the room it stands in.
        /// </summary>
        /// <summary>
        /// Destroys the player proxies and drops every cached material.
        ///
        /// Called when the field shuts down. The materials are copies of one that came out of the
        /// scene bundle, so they point at a shader that lives in that bundle; once the additive
        /// scene goes away the shader can go with it, and a renderer left holding it draws
        /// magenta. Nothing here survives a field transition on purpose.
        /// </summary>
        public static void Cleanup()
        {
            foreach (MeshRenderer proxy in _playerProxies)
                if (proxy != null)
                    UnityEngine.Object.Destroy(proxy.gameObject);

            foreach (Mesh mesh in _playerProxyMeshes)
                if (mesh != null)
                    UnityEngine.Object.Destroy(mesh);

            foreach (Material[] materials in _playerProxyMaterials)
            {
                if (materials == null)
                    continue;
                foreach (Material material in materials)
                    if (material != null)
                        UnityEngine.Object.Destroy(material);
            }

            ForgetProxies();
            CharacterMaterial = null;
            _pendingFieldMap = null;
            _tintApplied = false;
            _lastTint = Color.white;
            _lastReportedMode = (PlayerProxyMode)(-1);
        }

        /// <summary>
        /// Whether the bundle's character material can act as a depth mask, which needs the
        /// _ColorMask property. Standard has no such thing, so without a working bundle material
        /// the proxy goes back to being shadow-map only.
        /// </summary>
        private static Boolean CanDepthMask()
        {
            return CharacterMaterial != null
                && CharacterMaterial.shader != null
                && CharacterMaterial.shader.isSupported
                && CharacterMaterial.HasProperty(ColorMaskPropertyId);
        }

        private static void SyncProxyMaterials(Renderer source, MeshRenderer proxy, Int32 index, Shader fallback, Boolean depthMask, Boolean modulate)
        {
            Material[] originals = source.sharedMaterials;
            while (_playerProxyMaterials.Count <= index)
                _playerProxyMaterials.Add(null);

            // Checked again here and not only on collection: the material may have arrived by
            // another route, and an unsupported shader takes the character's shadow down with it.
            Material template = CharacterMaterial;
            if (template != null && (template.shader == null || !template.shader.isSupported))
                template = null;

            Material[] materials = _playerProxyMaterials[index];
            Shader wanted = template != null ? template.shader : fallback;
            Boolean rebuild = materials == null || materials.Length != originals.Length;
            if (!rebuild)
            {
                foreach (Material material in materials)
                {
                    // material.shader turns null when the bundle that owned it is gone. Unity then
                    // draws magenta instead of erroring, so it has to be caught here.
                    if (material != null && material.shader != null && material.shader == wanted)
                        continue;
                    rebuild = true;
                    break;
                }
            }

            if (rebuild)
            {
                materials = new Material[originals.Length];
                for (Int32 j = 0; j < materials.Length; j++)
                    materials[j] = template != null ? new Material(template) : new Material(fallback);
                _playerProxyMaterials[index] = materials;
                proxy.sharedMaterials = materials;
            }

            // Refreshed every frame rather than once: the game retints the actor through _Color as
            // it walks between differently lit parts of a map.
            for (Int32 j = 0; j < materials.Length; j++)
            {
                Material original = originals[j];
                if (original == null)
                    continue;
                materials[j].mainTexture = original.mainTexture;
                if (original.HasProperty(ColorPropertyId) && materials[j].HasProperty(ColorPropertyId))
                    materials[j].SetColor(ColorPropertyId, original.GetColor(ColorPropertyId));
                if (materials[j].HasProperty(LightInfluencePropertyId))
                    materials[j].SetFloat(LightInfluencePropertyId, CharacterLightInfluence);
                if (materials[j].HasProperty(ColorMaskPropertyId))
                {
                    // When modulating it does write colour: its colour IS the factor the game's
                    // painted pixels are multiplied by.
                    materials[j].SetFloat(ColorMaskPropertyId, depthMask && !modulate && !DebugMask ? 0f : 15f);
                    // -1 restores the queue the shader itself declares.
                    materials[j].renderQueue = depthMask ? DepthMaskQueue : -1;
                }
                if (DebugMask && depthMask && materials[j].HasProperty(ColorPropertyId))
                    materials[j].SetColor(ColorPropertyId, Color.green);
                if (materials[j].HasProperty(ModulatePropertyId))
                {
                    materials[j].SetFloat(ModulatePropertyId, modulate ? 1f : 0f);
                    // Blend DstColor Zero = multiply the destination. Without modulation, the
                    // usual premultiplied blend.
                    Boolean multiply = modulate && !DebugMask;
                    materials[j].SetFloat(SrcBlendPropertyId, multiply ? (Single)UnityEngine.Rendering.BlendMode.DstColor : (Single)UnityEngine.Rendering.BlendMode.One);
                    materials[j].SetFloat(DstBlendPropertyId, multiply ? (Single)UnityEngine.Rendering.BlendMode.Zero
                        : (DebugMask ? (Single)UnityEngine.Rendering.BlendMode.Zero : (Single)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
                }
                if (materials[j].HasProperty(StencilRefPropertyId))
                {
                    // Stencil discard was removed: it cut without looking at depth and bit into
                    // the character's own shadow. The properties are left inert.
                    materials[j].SetFloat(StencilRefPropertyId, 0f);
                    materials[j].SetFloat(StencilCompPropertyId, StencilAlways);
                    materials[j].SetFloat(StencilOpPropertyId, StencilKeep);
                }
            }
        }

        /// <summary>
        /// Compares transforms rather than bounds: FieldMapActor inflates the player's localBounds
        /// to Single.MaxValue * 0.01f to defeat culling (the PSX projection happens in the vertex
        /// shader, so Unity's frustum test means nothing), which makes its bounds centre useless.
        /// </summary>
        public static void LogPlayerProxy(FieldMap fieldMap)
        {
            if (fieldMap?.player == null)
                return;

            // Each proxy against ITS renderer, not against the player's.
            //
            // Proxies stopped being player-only and grew to cover every actor, but this kept
            // comparing proxy i with the player's renderer i. On a map with several characters that
            // pits unrelated objects against each other and reports errors of a thousand units on a
            // system that is fine: exactly the kind of false positive that costs an afternoon.
            Int32 index = 0;
            foreach (KeyValuePair<Renderer, Transform> entry in _characterOwner)
            {
                Renderer source = entry.Key;
                Int32 i = index++;
                if (source == null || i >= _playerProxies.Count)
                    continue;
                MeshRenderer proxy = _playerProxies[i];
                if (proxy == null || !proxy.gameObject.activeInHierarchy)
                    continue;
                Vector3 src = source.transform.position;
                Vector3 dst = proxy.transform.position;
                Log.Message($"[FieldPerspectiveCamera] CAMERA proxy{i} of '{entry.Value?.name}' src={src} proxy={dst} error={(dst - src).magnitude:F2} proxySize={proxy.bounds.size}");
            }
        }

        public static void Sync(FieldMap fieldMap)
        {
            if (fieldMap == null || fieldMap.mainCamera == null)
                return;
            if (!TryBuild3D(fieldMap, out Matrix4x4 worldToCamera, out Matrix4x4 projection))
                return;

            if (_camera3D == null)
            {
                GameObject go = new GameObject("Field3D Camera");
                _camera3D = go.AddComponent<Camera>();
                Log.Message($"[FieldPerspectiveCamera] Created the 3D camera on layer {Layer3D}.");
            }

            Camera fieldCamera = fieldMap.mainCamera;
            _camera3D.clearFlags = CameraClearFlags.Depth;
            _camera3D.cullingMask = 1 << Layer3D;
            _camera3D.depth = fieldCamera.depth + 1f;
            _camera3D.rect = fieldCamera.rect;
            _camera3D.nearClipPlane = NearClip;
            _camera3D.farClipPlane = FarClip;
            // Drive the transform, not worldToCameraMatrix: Unity culls objects and builds the
            // shadow frustum from the transform, so a camera left at the origin discards everything.
            Matrix4x4 cameraToWorld = (Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * worldToCamera).inverse;
            _camera3D.transform.position = cameraToWorld.GetColumn(3);
            _camera3D.transform.rotation = Quaternion.LookRotation(cameraToWorld.GetColumn(2), cameraToWorld.GetColumn(1));
            _camera3D.projectionMatrix = projection;

            // Nothing else may draw this layer, or the 3D objects would also appear flattened
            // through the field's orthographic camera.
            foreach (Camera other in Camera.allCameras)
                if (other != _camera3D)
                    other.cullingMask &= ~(1 << Layer3D);
        }

        /// <summary>Directional light for the 3D pass. Remember field space has Y pointing down.</summary>
        public static Light GetOrCreateLight(Vector3 eulerAngles, Single intensity)
        {
            if (_light == null)
            {
                GameObject go = new GameObject("Field3D Light");
                _light = go.AddComponent<Light>();
                _light.type = LightType.Directional;
                _light.shadows = LightShadows.Soft;
                _light.cullingMask = 1 << Layer3D;
                Log.Message("[FieldPerspectiveCamera] Created the 3D directional light.");
            }
            _light.transform.eulerAngles = eulerAngles;
            _light.intensity = intensity;
            return _light;
        }

        /// <summary>
        /// Ambient light for the 3D pass. Scenario geometry is meant to carry its lighting baked
        /// into the albedo, so the real-time directional light should stay dim (it is there for the
        /// character and its shadow); ambient is what keeps the baked art from being darkened by a
        /// light that is not supposed to relight it.
        /// </summary>
        public static void SetAmbient(Color color, Single intensity)
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = color;
            RenderSettings.ambientIntensity = intensity;
            Log.Message($"[FieldPerspectiveCamera] ambient = {color} x{intensity}");
        }

        /// <summary>
        /// Field units are large: a character is roughly 600 units tall, while the shipped quality
        /// setting is 40, which would put every shadow outside the shadow distance.
        /// </summary>
        public static void SetShadowDistance(Single distance)
        {
            QualitySettings.shadowDistance = distance;
            Log.Message($"[FieldPerspectiveCamera] shadowDistance = {QualitySettings.shadowDistance}");
        }

        public static void SetLayerRecursively(GameObject go, Int32 layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursively(child.gameObject, layer);
        }

        /// <summary>Screen position a field point gets through the game's own PSX projection.</summary>
        public static Vector3 ProjectWithPsx(FieldMap fieldMap, Vector3 fieldPoint)
        {
            BGCAM_DEF bgCamera = fieldMap.scene.cameraList[fieldMap.camIdx];
            Vector3 projected = PSX.CalculateGTE_RTPT(
                fieldPoint,
                Matrix4x4.identity,
                bgCamera.GetMatrixRT(),
                bgCamera.GetViewDistance(),
                fieldMap.GetProjectionOffset());
            return fieldMap.mainCamera.WorldToScreenPoint(projected);
        }

        /// <summary>Screen position the same field point gets through the derived perspective camera.</summary>
        public static Vector3 ProjectWithDerived(Camera fieldCamera, Matrix4x4 worldToCamera, Matrix4x4 projection, Vector3 fieldPoint)
        {
            Vector4 clip = projection * worldToCamera * new Vector4(fieldPoint.x, fieldPoint.y, fieldPoint.z, 1f);
            if (Mathf.Approximately(clip.w, 0f))
                return new Vector3(Single.NaN, Single.NaN, Single.NaN);
            Vector3 ndc = new Vector3(clip.x / clip.w, clip.y / clip.w, clip.z / clip.w);
            Rect rect = fieldCamera.pixelRect;
            return new Vector3(
                (ndc.x * 0.5f + 0.5f) * rect.width + rect.x,
                (ndc.y * 0.5f + 0.5f) * rect.height + rect.y,
                clip.w);
        }

        /// <summary>Logs, for one field point, how far the derived camera lands from the PSX projection.</summary>
        public static void LogProjectionError(FieldMap fieldMap, String label, Vector3 fieldPoint)
        {
            try
            {
                if (!TryBuildMatrices(fieldMap, out Matrix4x4 worldToCamera, out Matrix4x4 projection))
                {
                    Log.Message("[FieldPerspectiveCamera] CAMERA: field camera data not available yet.");
                    return;
                }
                Vector3 psx = ProjectWithPsx(fieldMap, fieldPoint);
                Vector3 derived = ProjectWithDerived(fieldMap.mainCamera, worldToCamera, projection, fieldPoint);
                Vector2 delta = new Vector2(derived.x - psx.x, derived.y - psx.y);
                Log.Message($"[FieldPerspectiveCamera] CAMERA {label} field=({fieldPoint.x:F0},{fieldPoint.y:F0},{fieldPoint.z:F0}) psx=({psx.x:F1},{psx.y:F1}) derived=({derived.x:F1},{derived.y:F1}) delta=({delta.x:F1},{delta.y:F1})");
            }
            catch (Exception err)
            {
                Log.Error(err, "[FieldPerspectiveCamera] Failed to compare projections.");
            }
        }

        public static void LogSetup(FieldMap fieldMap)
        {
            if (fieldMap?.scene == null || fieldMap.camIdx < 0 || fieldMap.camIdx >= fieldMap.scene.cameraList.Count)
                return;
            BGCAM_DEF bgCamera = fieldMap.scene.cameraList[fieldMap.camIdx];
            Single viewDist = bgCamera.GetViewDistance();
            Single fovY = 2f * Mathf.Atan(FieldMap.HalfFieldHeight / viewDist) * Mathf.Rad2Deg;
            Camera ortho = fieldMap.mainCamera;
            Log.Message($"[FieldPerspectiveCamera] CAMERA setup camIdx={fieldMap.camIdx} viewDist={viewDist:F1} fovY={fovY:F2} halfW={FieldMap.HalfFieldWidth} halfH={FieldMap.HalfFieldHeight} projOffset={fieldMap.GetProjectionOffset()}");
            if (_camera3D != null)
                Log.Message($"[FieldPerspectiveCamera] CAMERA 3D pos={_camera3D.transform.position} fwd={_camera3D.transform.forward} up={_camera3D.transform.up} rect={_camera3D.pixelRect} mask=0x{_camera3D.cullingMask:X}");
            if (ortho != null)
                Log.Message($"[FieldPerspectiveCamera] CAMERA ortho size={ortho.orthographicSize} aspect={ortho.aspect:F3} pixelRect={ortho.pixelRect} worldPos={ortho.transform.position} screen={Screen.width}x{Screen.height}");
        }
    }
}
