using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Memoria.Field
{
    // Dumps everything needed to rebuild a field map as a modelling scene: the camera, a clean
    // background plate, and the walkmesh.
    //
    // Written from the running game rather than from the files on disc because the camera is only
    // fully determined at runtime: BGCAM_DEF supplies the rotation and projection distance, but the
    // screen mapping also depends on the current resolution and on per-map framing (narrow maps are
    // pillarboxed), which FieldPerspectiveCamera measures.
    //
    // Config line, read from MemoriaFieldObjects.txt:
    //     EXPORTSCENE
    // Exports the map being played into "MemoriaSceneExport/<fldMapNo>/".
    public static class FieldSceneExport
    {
        public const String OutputDirectory = "MemoriaSceneExport";

        private static Boolean _requested;

        /// <summary>
        /// Camera indices already written for the field being played.
        ///
        /// A field is not one view: BGSCENE holds a list of BGCAM_DEF and the game switches between
        /// them, so the same room can have several backgrounds and several projections. The 3D pass
        /// already follows that on its own, but the export used to write whichever one happened to
        /// be active when the map loaded, and the other views had no project to model against.
        ///
        /// Each one is written the first time the game actually switches to it, so every camera is
        /// captured while it is live and is therefore exactly as verified as the first.
        /// </summary>
        private static readonly HashSet<Int32> _exportedCameras = new HashSet<Int32>();

        public static void Request()
        {
            _requested = true;
        }

        public static void Reset()
        {
            _lastPixelRect = new Rect(-1f, -1f, -1f, -1f);
            _stableFrames = 0;
            _requested = false;
            _exportedCameras.Clear();
        }

        /// <summary>Frames the viewport must hold still before exporting. See Update.</summary>
        private const Int32 ViewportSettleFrames = 3;

        private static Rect _lastPixelRect;
        private static Int32 _stableFrames;

        public static void Update(FieldMap fieldMap)
        {
            if (!_requested || fieldMap == null || fieldMap.mainCamera == null)
                return;
            if (fieldMap.scene == null || _exportedCameras.Contains(fieldMap.camIdx))
                return;

            // Wait for the viewport to stop moving.
            //
            // The game narrows the field viewport a frame or two after entering, and the background
            // size and the field of view written to the JSON both come from it. Exporting on the
            // first frame captured full screen instead: on map 150, 1920x1080 and fovX 47.83
            // instead of 1765x1080 and 44.36. The Blender project then came out with a camera that
            // is not the game's, and nothing in the log said the background was wrong.
            Rect pixelRect = fieldMap.mainCamera.pixelRect;
            if (pixelRect.width != _lastPixelRect.width || pixelRect.height != _lastPixelRect.height
                || pixelRect.x != _lastPixelRect.x || pixelRect.y != _lastPixelRect.y)
            {
                _lastPixelRect = pixelRect;
                _stableFrames = 0;
                return;
            }
            if (_stableFrames < ViewportSettleFrames)
            {
                _stableFrames++;
                return;
            }

            if (!FieldPerspectiveCamera.TryBuildMatrices(fieldMap, out Matrix4x4 worldToCamera, out Matrix4x4 projection))
                return;
            _exportedCameras.Add(fieldMap.camIdx);

            try
            {
                Int32 mapNo = FF9StateSystem.Common.FF9.fldMapNo;
                String directory = Path.Combine(OutputDirectory, mapNo.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);

                // Camera 0 keeps the unsuffixed names: it is the one the existing projects and
                // tools already use, and there is no reason to break them.
                String suffix = fieldMap.camIdx == 0 ? String.Empty : $"_cam{fieldMap.camIdx}";
                Single backgroundScale = ExportBackground(fieldMap, Path.Combine(directory, $"background{suffix}.png"));
                // The walkmesh belongs to the field, not to the camera, so it is written once.
                if (fieldMap.camIdx == 0 || !File.Exists(Path.Combine(directory, "walkmesh.obj")))
                    ExportWalkmesh(fieldMap, Path.Combine(directory, "walkmesh.obj"));
                ExportCamera(fieldMap, worldToCamera, projection, Path.Combine(directory, $"field{suffix}.json"), mapNo, backgroundScale);

                Int32 cameraCount = fieldMap.scene.cameraList?.Count ?? 1;
                Log.Message($"[FieldSceneExport] Exported map {mapNo} camera {fieldMap.camIdx} of {cameraCount} to '{Path.GetFullPath(directory)}'.");
                if (cameraCount > 1 && _exportedCameras.Count < cameraCount)
                    Log.Message($"[FieldSceneExport] This field has {cameraCount} cameras and {_exportedCameras.Count} are exported so far. Walk through the map until the view changes to get the rest; each one is written the first time the game switches to it.");
            }
            catch (Exception err)
            {
                Log.Error(err, "[FieldSceneExport] Failed to export the field.");
            }
        }

        /// <summary>
        /// Renders the whole reachable background, not just the part that fits on screen.
        ///
        /// A field background is bigger than the viewport: the game scrolls by moving the
        /// orthographic camera while the background quads sit still in world space (their vertex
        /// program is a plain glstate_matrix_mvp transform, so nothing about them is screen-space).
        /// Widening the camera therefore reveals the rest, in a single shot.
        ///
        /// The captured region is the visible frame grown by the same factor on both axes, kept
        /// centred on it. Growing both axes equally is what lets a consumer place the image with
        /// one uniform scale and no offset — Blender's camera background images, for one, only
        /// have a uniform scale. Centring it sidesteps having to work out which way curVRP runs
        /// against the camera's transform, at the cost of a margin of empty pixels.
        ///
        /// Returns that growth factor, which is what ties the image back to the camera frame.
        /// </summary>
        private static Single ExportBackground(FieldMap fieldMap, String path)
        {
            Camera camera = fieldMap.mainCamera;
            Rect rect = camera.pixelRect;
            Int32 frameWidth = Mathf.RoundToInt(rect.width);
            Int32 frameHeight = Mathf.RoundToInt(rect.height);
            if (frameWidth <= 0 || frameHeight <= 0)
                return 1f;

            // The camera's orthographic units are PSX pixels, and so are the scroll limits, so the
            // two can be compared directly.
            Single pixelsPerUnit = frameHeight / (2f * camera.orthographicSize);
            Single halfHeight = camera.orthographicSize;
            Single halfWidth = frameWidth / (2f * pixelsPerUnit);

            BGCAM_DEF bgCamera = fieldMap.scene.cameraList[fieldMap.camIdx];
            Single scrollX = Mathf.Max(0f, bgCamera.vrpMaxX - bgCamera.vrpMinX);
            Single scrollY = Mathf.Max(0f, bgCamera.vrpMaxY - bgCamera.vrpMinY);
            Single scale = Mathf.Max(1f, Mathf.Max(1f + scrollX / halfWidth, 1f + scrollY / halfHeight));

            Int32 fullWidth = Mathf.RoundToInt(frameWidth * scale);
            Int32 fullHeight = Mathf.RoundToInt(frameHeight * scale);
            // A render texture has a hard size limit, and a plate this big is already a lot of
            // memory. Past the cap the plate loses resolution rather than the export failing.
            const Int32 MaxSide = 8192;
            Single downscale = Mathf.Min(1f, Mathf.Min(MaxSide / (Single)fullWidth, MaxSide / (Single)fullHeight));
            if (downscale < 1f)
            {
                fullWidth = Mathf.RoundToInt(fullWidth * downscale);
                fullHeight = Mathf.RoundToInt(fullHeight * downscale);
                Log.Warning($"[FieldSceneExport] The full background would be larger than {MaxSide}px, so it is exported at {Mathf.RoundToInt(downscale * 100f)}% resolution.");
            }

            List<Renderer> hidden = new List<Renderer>();
            foreach (FF9Char character in FF9StateSystem.Common.FF9.charArray.Values)
            {
                if (character?.geo == null)
                    continue;
                foreach (Renderer renderer in character.geo.GetComponentsInChildren<Renderer>())
                {
                    if (!renderer.enabled)
                        continue;
                    renderer.enabled = false;
                    hidden.Add(renderer);
                }
            }

            RenderTexture target = new RenderTexture(fullWidth, fullHeight, 24);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Single previousSize = camera.orthographicSize;
            Single previousAspect = camera.aspect;
            Rect previousRect = camera.rect;

            // Camera.aspect derives itself from the viewport UNTIL it is assigned; from then on it
            // is pinned. So handing it back with "camera.aspect = previousAspect" restores nothing:
            // it turns something automatic into something manual, holding whatever value it had at
            // that instant. And the instant matters, because the game narrows the field viewport a
            // frame after entering: the first visit to a map exports with the viewport already
            // narrow and pins the right value by luck; coming back, it exports one frame earlier,
            // full screen, and pins 16:9 forever. The field is then drawn with a horizontal scale
            // that is not its viewport's, and the character proxy stops lining up with what the
            // game paints. It only ever failed the second time.
            Rect previousPixel = camera.pixelRect;
            Single derivedAspect = previousPixel.height > 0f ? previousPixel.width / previousPixel.height : 0f;
            Boolean aspectWasAutomatic = derivedAspect > 0f
                && Mathf.Abs(previousAspect - derivedAspect) < derivedAspect * 0.001f;
            Texture2D shot = new Texture2D(fullWidth, fullHeight, TextureFormat.RGB24, false);
            try
            {
                // The field camera is pillarboxed on narrow maps, so its rect has to be opened up
                // for the shot or the capture would inherit the letterbox.
                camera.rect = new Rect(0f, 0f, 1f, 1f);
                camera.orthographicSize = halfHeight * scale;
                camera.aspect = (halfWidth * scale) / (halfHeight * scale);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                shot.ReadPixels(new Rect(0f, 0f, fullWidth, fullHeight), 0, 0);
                shot.Apply();
                File.WriteAllBytes(path, shot.EncodeToPNG());
                Log.Message($"[FieldSceneExport] Background plate {fullWidth}x{fullHeight}, which is the {frameWidth}x{frameHeight} view grown x{N(scale, "F3")} to cover a scroll range of {N(scrollX, "F0")}x{N(scrollY, "F0")} PSX units.");
            }
            finally
            {
                camera.rect = previousRect;
                camera.orthographicSize = previousSize;
                // See the note above: if it was deriving itself, it has to go back to that and not
                // to a number, or it stays pinned to whatever aspect was current at export time.
                if (aspectWasAutomatic)
                    camera.ResetAspect();
                else
                    camera.aspect = previousAspect;
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(shot);
                UnityEngine.Object.DestroyImmediate(target);
                foreach (Renderer renderer in hidden)
                    if (renderer != null)
                        renderer.enabled = true;
            }
            return scale;
        }

        private static void ExportWalkmesh(FieldMap fieldMap, String path)
        {
            if (fieldMap.walkMesh?.tris == null)
            {
                Log.Warning("[FieldSceneExport] No walkmesh on this map.");
                return;
            }

            StringBuilder obj = new StringBuilder();
            obj.AppendLine("# FF9 walkmesh, in field units. Triangles keep the game's floor/triangle ids.");
            Int32 vertexIndex = 1;
            foreach (WalkMeshTriangle triangle in fieldMap.walkMesh.tris)
            {
                if (triangle?.originalVertices == null || triangle.originalVertices.Length < 3)
                    continue;
                foreach (Vector3 vertex in triangle.originalVertices)
                    obj.AppendLine($"v {N(vertex.x)} {N(vertex.y)} {N(vertex.z)}");
                obj.AppendLine($"# floor {triangle.floorIdx} tri {triangle.triIdx}");
                obj.AppendLine($"f {vertexIndex} {vertexIndex + 1} {vertexIndex + 2}");
                vertexIndex += 3;
            }
            File.WriteAllText(path, obj.ToString());
            Log.Message($"[FieldSceneExport] Walkmesh: {fieldMap.walkMesh.tris.Count} triangle(s).");
        }

        private static void ExportCamera(FieldMap fieldMap, Matrix4x4 worldToCamera, Matrix4x4 projection, String path, Int32 mapNo, Single backgroundScale)
        {
            // The camera transform is recovered the same way the runtime camera does it, so what the
            // exported project shows is what the game draws.
            Matrix4x4 cameraToWorld = (Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * worldToCamera).inverse;
            Vector3 position = cameraToWorld.GetColumn(3);
            Vector3 right = cameraToWorld.GetColumn(0);
            Vector3 up = cameraToWorld.GetColumn(1);
            Vector3 forward = cameraToWorld.GetColumn(2);

            Rect rect = fieldMap.mainCamera.pixelRect;
            BGCAM_DEF bgCamera = fieldMap.scene.cameraList[fieldMap.camIdx];

            // tan(halfFov) is the reciprocal of the projection scale; the P02/P12 terms are the
            // screen pan, which a renderer expresses as a lens shift rather than a camera move.
            Single fovX = 2f * Mathf.Atan(1f / projection[0, 0]);
            Single fovY = 2f * Mathf.Atan(1f / projection[1, 1]);

            StringBuilder json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine($"  \"map\": {mapNo},");
            json.AppendLine($"  \"mapName\": \"{FF9StateSystem.Common.FF9.mapNameStr}\",");
            json.AppendLine($"  \"cameraIndex\": {fieldMap.camIdx},");
            json.AppendLine($"  \"cameraCount\": {fieldMap.scene.cameraList?.Count ?? 1},");
            json.AppendLine($"  \"sceneScale\": {N(FieldPerspectiveCamera.SceneScale)},");
            json.AppendLine($"  \"renderWidth\": {Mathf.RoundToInt(rect.width)},");
            json.AppendLine($"  \"renderHeight\": {Mathf.RoundToInt(rect.height)},");
            json.AppendLine($"  \"fovXRadians\": {N(fovX, "F6")},");
            json.AppendLine($"  \"fovYRadians\": {N(fovY, "F6")},");
            // background.png covers this multiple of the frame, centred on it. A consumer places
            // it by scaling with this factor, with no offset.
            json.AppendLine($"  \"backgroundScale\": {N(backgroundScale, "F6")},");
            json.AppendLine($"  \"ndcOffsetX\": {N(-projection[0, 2], "F6")},");
            json.AppendLine($"  \"ndcOffsetY\": {N(-projection[1, 2], "F6")},");
            json.AppendLine($"  \"position\": [{N(position.x)}, {N(position.y)}, {N(position.z)}],");
            json.AppendLine($"  \"right\": [{N(right.x, "F6")}, {N(right.y, "F6")}, {N(right.z, "F6")}],");
            json.AppendLine($"  \"up\": [{N(up.x, "F6")}, {N(up.y, "F6")}, {N(up.z, "F6")}],");
            json.AppendLine($"  \"forward\": [{N(forward.x, "F6")}, {N(forward.y, "F6")}, {N(forward.z, "F6")}],");
            json.AppendLine($"  \"psxViewDistance\": {N(bgCamera.GetViewDistance())},");
            json.AppendLine($"  \"psxProjectionOffset\": [{N(fieldMap.GetProjectionOffset().x)}, {N(fieldMap.GetProjectionOffset().y)}]");
            json.AppendLine("}");
            File.WriteAllText(path, json.ToString());
            Log.Message($"[FieldSceneExport] Camera: fovX {N(fovX * Mathf.Rad2Deg, "F2")} deg, shift ndc ({N(-projection[0, 2], "F3")}, {N(-projection[1, 2], "F3")}).");
        }

        /// <summary>
        /// Numbers are formatted explicitly with the invariant culture: string interpolation uses
        /// the current one, and the game running in a locale with comma decimals would emit JSON
        /// and OBJ files that no parser accepts.
        /// </summary>
        private static String N(Single value, String format = "F4")
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
