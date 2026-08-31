# Builds a Blender project from what EXPORTSCENE dumps: the camera placed like the game's, the map
# background framed in that camera, the walkmesh as a mesh, and everything at metric scale.
#
# Usage (from the repo root):
#   blender --background --factory-startup --python DynamicShadows/Tools/blender/build_field_project.py -- <export_folder> [output.blend]
#
# Example:
#   blender --background --factory-startup --python DynamicShadows/Tools/blender/build_field_project.py -- ^
#       "C:/Program Files (x86)/Steam/steamapps/common/FINAL FANTASY IX/MemoriaSceneExport/150"

import json
import math
import os
import re
import sys

import bpy
from bpy_extras.object_utils import world_to_camera_view
from mathutils import Matrix, Vector


def unity_to_blender(v):
    """Field (left-handed, Y up) -> Blender (right-handed, Z up).

    Swapping Y and Z is an odd permutation (determinant -1), and that change of
    handedness is exactly what is needed between systems of opposite chirality.

    The X and Z negations come from measurement: markers placed on known axes and
    carried through Blender -> FBX -> Unity -> game came back with X and Z sign
    flipped. That is a 180 degree rotation about the vertical axis (determinant
    +1), introduced by the FBX export chain. Compensating for it here makes the
    round trip exact.
    """
    return Vector((-v[0], -v[2], v[1]))


def read_export(folder, name="field.json"):
    with open(os.path.join(folder, name), "r", encoding="utf-8") as handle:
        data = json.load(handle)
    data["_folder"] = folder
    # Camera 0 was written without a suffix, so as not to break existing projects.
    suffix = "" if name == "field.json" else name[len("field"):-len(".json")]
    data["_suffix"] = suffix
    data["_background"] = "background%s.png" % suffix
    return data


def read_exports(folder):
    """Every camera dumped for a map, ordered by index.

    A field is not one view: BGSCENE holds a list of BGCAM_DEF and the game switches between them,
    so the same room can have several backgrounds and several projections. The geometry is the same;
    the only thing that changes is where it is looked at from.
    """
    names = ["field.json"] if os.path.exists(os.path.join(folder, "field.json")) else []
    names += sorted(n for n in os.listdir(folder)
                    if re.match(r"^field_cam\d+\.json$", n))
    return [read_export(folder, name) for name in names]


def camera_geometry(data):
    """What is needed to reproduce the game camera in Blender.

    The exported basis is orthogonal but NOT orthonormal: |up| is 1.0713. That
    number is 15/14, the stretch of the PSX 320x224 framebuffer shown at 4:3, and
    FFIX carries it inside its camera matrix so that the models line up with the
    backgrounds, which were painted for that ratio.

    A Blender camera is orthonormal by construction, so the scale has to be taken
    out of the basis and put where it belongs: in the field-of-view tangents. The
    game projects with the INVERSE of this basis, not with its transpose, and for
    a scaled basis those are not the same: the inverse divides by the square of
    the norm. Hence the factor is k/kz and not kz/k.

    That leaves an angular aspect (1.5257) that no longer matches the pixel one
    (1.6343). The difference is declared as pixel aspect, and comes out at 1.0711,
    the reciprocal of the PSX (4/3)/(320/224) = 0.93333, which confirms where it
    comes from.

    Everything here is measured against world_to_camera_view, not assumed: the
    resulting projection lands within 0.0002 px of the game's.
    """
    right = Vector(data["right"])
    up = Vector(data["up"])
    forward = Vector(data["forward"])
    kx, ky, kz = right.length, up.length, forward.length

    tan_x = math.tan(data["fovXRadians"] / 2.0) * kx / kz
    tan_y = math.tan(data["fovYRadians"] / 2.0) * ky / kz
    angular_aspect = tan_x / tan_y

    width = float(data["renderWidth"])
    height = float(data["renderHeight"])
    # How much the pixel aspect has to be corrected to reach the angular one.
    # Blender only expresses pixel aspect on the axis that ends up >= 1: putting
    # the other one below 1 does absolutely nothing.
    needed = (width / height) / angular_aspect
    if needed >= 1.0:
        pixel_aspect_x, pixel_aspect_y = 1.0, needed
    else:
        pixel_aspect_x, pixel_aspect_y = 1.0 / needed, 1.0

    # Blender applies the shift with the opposite sign to the game's frame
    # offset. On Y the factor is the angular aspect, not the pixel one: measured,
    # d(u)/d(shift_x) = -1 and d(v)/d(shift_y) = -1.5257.
    shift_x = -data["ndcOffsetX"] / 2.0
    shift_y = -data["ndcOffsetY"] / 2.0 / angular_aspect

    return {
        "right": right / kx,
        "up": up / ky,
        "forward": forward / kz,
        "scales": (kx, ky, kz),
        "tanX": tan_x,
        "tanY": tan_y,
        "angularAspect": angular_aspect,
        "pixelAspectX": pixel_aspect_x,
        "pixelAspectY": pixel_aspect_y,
        "shiftX": shift_x,
        "shiftY": shift_y,
    }


def set_target_collection(collection):
    """Makes a collection active, which is where objects created from now on will land.

    The constructors link into bpy.context.collection, so it is enough to move the view pointer
    before calling them. The lookup happens in the active scene view layer: the collection has to be
    linked there already.
    """
    layer = bpy.context.view_layer.layer_collection
    for child in layer.children:
        if child.collection is collection:
            bpy.context.view_layer.active_layer_collection = child
            return
    bpy.context.view_layer.active_layer_collection = layer


def build_camera(data, geo, name="FieldCamera"):
    position = unity_to_blender(data["position"]) / data["sceneScale"]
    right = unity_to_blender(geo["right"])
    up = unity_to_blender(geo["up"])
    # A Blender camera looks down its local -Z; a Unity one down its +Z.
    back = -unity_to_blender(geo["forward"])

    camera_data = bpy.data.cameras.new(name)
    camera_data.sensor_fit = "HORIZONTAL"
    camera_data.angle = 2.0 * math.atan(geo["tanX"])
    camera_data.shift_x = geo["shiftX"]
    camera_data.shift_y = geo["shiftY"]
    camera_data.clip_start = 0.01
    camera_data.clip_end = 10000.0

    camera = bpy.data.objects.new(name, camera_data)
    camera.matrix_world = Matrix((
        (right.x, up.x, back.x, position.x),
        (right.y, up.y, back.y, position.y),
        (right.z, up.z, back.z, position.z),
        (0.0, 0.0, 0.0, 1.0),
    ))
    bpy.context.collection.objects.link(camera)
    bpy.context.scene.camera = camera
    return camera


def background_scale(data):
    """How many times the frame spans background.png.

    The exporter captures the whole background, not only what fits on screen: a field background is
    larger than the window and the game scrolls by moving the orthographic camera. The image grows
    equally on both axes and centred on the frame, which is what allows placing it with a single
    uniform scale and no offset.
    """
    return float(data.get("backgroundScale", 1.0))


def frame_corners(data, geo, camera, distance, span=1.0):
    """The four frame corners at a given distance, in world coordinates.

    They come from inverting the projection:
        ndc.x = (vx / -vz) / tan_x + offset   ->   vx = (ndc.x - offset) * tan_x * d
    and can be verified by reprojecting them, which is what main() does.
    """
    corners = []
    for ndc_x, ndc_y in ((-span, -span), (span, -span), (span, span), (-span, span)):
        local = Vector((
            (ndc_x - data["ndcOffsetX"]) * geo["tanX"] * distance,
            (ndc_y - data["ndcOffsetY"]) * geo["tanY"] * distance,
            -distance,
        ))
        corners.append(camera.matrix_world @ local)
    return corners


def attach_camera_layers(data, camera, image):
    """Background layers on the camera: they are not geometry, so nothing you model
    ever covers them, and they toggle with one click in the camera properties.

    No offset. The image is fitted to the camera frame, and that frame already
    carries the lens shift, so it matches the render with nothing to correct. The
    misalignment seen earlier was not the image: it was the camera, which had the
    shift sign flipped and a 7% scale on its Y axis.
    """
    camera.data.show_background_images = True

    def add(depth, alpha, enabled):
        layer = camera.data.background_images.new()
        layer.image = image
        layer.alpha = alpha
        layer.display_depth = depth
        # STRETCH and not FIT: the image and the frame already share the same ratio,
        # and this way no rounding introduces bands at the sides.
        layer.frame_method = "STRETCH"
        layer.offset = (0.0, 0.0)
        # STRETCH fits the image to the frame and scale grows it uniformly from the centre, which
        # is exactly how it was captured: with no offset to correct.
        layer.scale = background_scale(data)
        if hasattr(layer, "show_background_image"):
            layer.show_background_image = enabled
        return layer

    add("BACK", 1.0, True)
    add("FRONT", 0.35, False)


def attach_background(data, geo, camera, distance, name="BackgroundPlate"):
    path = os.path.join(data["_folder"], data.get("_background", "background.png"))
    if not os.path.exists(path):
        print("No background.png, skipping the background.")
        return None
    image = bpy.data.images.load(path)

    corners = frame_corners(data, geo, camera, distance, background_scale(data))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([tuple(c) for c in corners], [], [(0, 1, 2, 3)])
    mesh.update()
    uv = mesh.uv_layers.new(name="UVMap")
    for i, coord in enumerate(((0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0))):
        uv.data[i].uv = coord

    material = bpy.data.materials.new(name)
    # use_nodes goes away in Blender 6.0. In versions where a material is born with a node tree
    # there is nothing to enable, and touching it only produces the deprecation warning.
    if getattr(material, "node_tree", None) is None:
        material.use_nodes = True
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    texture.interpolation = "Closest"
    emission = nodes.new("ShaderNodeEmission")
    output = nodes.new("ShaderNodeOutputMaterial")
    links.new(texture.outputs["Color"], emission.inputs["Color"])
    links.new(emission.outputs["Emission"], output.inputs["Surface"])
    mesh.materials.append(material)

    plate = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(plate)
    # Reference plate with a verified frame. Left hidden: for actual work there are the camera
    # layers, which geometry never covers.
    plate.hide_select = True
    plate.hide_render = False
    plate.hide_viewport = True

    attach_camera_layers(data, camera, image)
    return plate


def build_walkmesh(data):
    path = os.path.join(data["_folder"], "walkmesh.obj")
    if not os.path.exists(path):
        print("No walkmesh.obj, skipping the collision mesh.")
        return None

    scale = data["sceneScale"]
    vertices = []
    faces = []
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            parts = line.split()
            if not parts:
                continue
            if parts[0] == "v":
                raw = (float(parts[1]), float(parts[2]), float(parts[3]))
                vertices.append(unity_to_blender(raw) / scale)
            elif parts[0] == "f":
                faces.append(tuple(int(p.split("/")[0]) - 1 for p in parts[1:4]))

    mesh = bpy.data.meshes.new("Walkmesh")
    mesh.from_pydata([tuple(v) for v in vertices], [], faces)
    mesh.validate()
    mesh.update()

    obj = bpy.data.objects.new("Walkmesh", mesh)
    bpy.context.collection.objects.link(obj)
    obj.display_type = "WIRE"
    obj.show_wire = True
    # It is a navigation reference, not scenery geometry.
    obj.hide_render = True
    return obj


def build_reference_markers(data, walkmesh):
    """Markers to validate the Blender -> FBX -> Unity round trip.

    It is the only leg of the chain that cannot be checked from here: Blender and
    Unity have opposite chirality, and depending on the export settings the depth
    axis can end up flipped. Exporting these markers and looking at where they
    land in the game settles it once and for all.
    """
    scale = data["sceneScale"]
    markers = []

    origin = bpy.data.objects.new("RefFieldOrigin", None)
    origin.empty_display_type = "PLAIN_AXES"
    origin.empty_display_size = 0.5
    origin.location = (0.0, 0.0, 0.0)
    bpy.context.collection.objects.link(origin)

    # Three points spread on different axes: if any one flips, it shows.
    field_points = [("X", (1000.0, 0.0, 0.0)), ("Y", (0.0, 500.0, 0.0)), ("Z", (0.0, 0.0, 1000.0))]
    for name, field in field_points:
        mesh = bpy.data.meshes.new("RefMarker" + name)
        size = 0.15
        verts = [(dx * size, dy * size, dz * size)
                 for dx in (-1, 1) for dy in (-1, 1) for dz in (-1, 1)]
        faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
        mesh.from_pydata(verts, [], faces)
        mesh.update()
        marker = bpy.data.objects.new("RefMarker" + name, mesh)
        marker.location = unity_to_blender(field) / scale
        bpy.context.collection.objects.link(marker)
        markers.append((name, field, tuple(marker.location)))

    return markers


def verify_projection(data, camera, walkmesh):
    """Projects the walkmesh with the Blender camera and compares it against the game.

    The ground truth is rebuilt with the INVERSE of the exported basis, which is
    what the game applies. Using the transpose looks equivalent and is not, as
    soon as the basis has scale: the error slips into both sides of the comparison
    and it agrees while being wrong.

    It always runs. If the exporter ever changes, or a Blender version moves a
    convention, this says so in the same run.
    """
    if walkmesh is None:
        return None

    scale = data["sceneScale"]
    right, up, forward = (Vector(data[k]) for k in ("right", "up", "forward"))
    position = Vector(data["position"])
    camera_to_world = Matrix((
        (right.x, up.x, forward.x, position.x),
        (right.y, up.y, forward.y, position.y),
        (right.z, up.z, forward.z, position.z),
        (0.0, 0.0, 0.0, 1.0),
    )).inverted()
    tan_x = math.tan(data["fovXRadians"] / 2.0)
    tan_y = math.tan(data["fovYRadians"] / 2.0)

    scene = bpy.context.scene
    errors_x = []
    errors_y = []
    for vertex in walkmesh.data.vertices:
        b = walkmesh.matrix_world @ vertex.co
        field = Vector((-b.x * scale, b.z * scale, -b.y * scale))
        q = camera_to_world @ field
        if q.z <= 0.0:
            continue
        expected_u = ((q.x / q.z) / tan_x + data["ndcOffsetX"]) * 0.5 + 0.5
        expected_v = ((q.y / q.z) / tan_y + data["ndcOffsetY"]) * 0.5 + 0.5
        got = world_to_camera_view(scene, camera, b)
        errors_x.append(abs(got.x - expected_u) * data["renderWidth"])
        errors_y.append(abs(got.y - expected_v) * data["renderHeight"])

    if not errors_x:
        return None

    # The median, not the maximum. On a map with the camera close and a wide field of view there
    # are walkmesh vertices almost against the camera plane, and there the perspective divide
    # magnifies any floating-point difference: the maximum spikes while the whole map is exact.
    # Measured on 153: median 0.16 px and maximum 2.87 px, with the camera correct.
    errors_x.sort()
    errors_y.sort()
    middle = len(errors_x) // 2
    return (len(errors_x), errors_x[middle], errors_y[middle], errors_x[-1], errors_y[-1])


def check_one(data, camera, walkmesh):
    check = verify_projection(data, camera, walkmesh)
    if not check:
        return
    count, median_x, median_y, worst_x, worst_y = check
    print("  camera      : %s walkmesh vertices, median deviation X %.4f px  Y %.4f px"
          " (max %.2f / %.2f)" % (count, median_x, median_y, worst_x, worst_y))
    if max(median_x, median_y) > 0.5:
        print("  *** WRONG. This camera does not reproduce the game's. ***")
    elif max(worst_x, worst_y) > 2.0:
        print("                (a few stray vertices spike: the ones landing almost on the camera")
        print("                 plane, where perspective magnifies rounding.)")


def configure_scene(data, geo):
    scene = bpy.context.scene
    scene.render.resolution_x = data["renderWidth"]
    scene.render.resolution_y = data["renderHeight"]
    scene.render.resolution_percentage = 100
    # The game camera's angular aspect does not match its pixel aspect (inherited
    # from the PSX 320x224 at 4:3). Without this the view comes out squashed by
    # 6.6% vertically and no offset fixes it.
    scene.render.pixel_aspect_x = geo["pixelAspectX"]
    scene.render.pixel_aspect_y = geo["pixelAspectY"]
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    if not argv:
        raise SystemExit("Missing the export folder (MemoriaSceneExport/<map>)")

    root = os.path.abspath(argv[0])

    # A folder with no field.json but with subfolders that do have one is the dump root: all of
    # them get processed. EXPORTSCENE leaves a map behind every time you enter one, so they pile up
    # as soon as you play at all, and doing them one by one does not scale.
    if not os.path.exists(os.path.join(root, "field.json")):
        pending = sorted(name for name in os.listdir(root)
                         if os.path.exists(os.path.join(root, name, "field.json")))
        if not pending:
            raise SystemExit("No field.json in %s, neither loose nor in subfolders." % root)
        for name in pending:
            folder = os.path.join(root, name)
            exports = read_exports(folder)
            data = exports[0]
            output = os.path.join(folder, "field_%s.blend" % data["map"])
            # An existing project is never overwritten: it may hold modelling work, and this
            # script starts from an empty scene. To update one without losing that work there is
            # update_field_project.py.
            if os.path.exists(output):
                print("map %s: %s already exists, skipping (use update_field_project.py to update it)"
                      % (name, os.path.basename(output)))
                continue
            build_project(exports, folder, output)
        return

    folder = root
    exports = read_exports(folder)
    output = argv[1] if len(argv) > 1 else os.path.join(folder, "field_%s.blend" % exports[0]["map"])
    build_project(exports, folder, output)


def build_project(exports, folder, output):
    """One file with the shared geometry and one Blender scene per camera.

    Resolution and pixel aspect belong to the SCENE, not to the camera, and each BGCAM can have its
    own framing: two of them do not fit in a single scene.

    What ties them together is a COLLECTION, not a scene copy. There used to be a
    scene.new(LINK_COPY) here and it was wrong for two reasons, both confirmed by opening the
    generated file: it copies the object list at the instant it is created, so anything modelled
    afterwards appeared ONLY in the active scene -precisely what it was supposed to solve- and on
    top of that the second scene inherited the first one's BackgroundPlate, a huge plate with the
    background painted on it planted in the middle of the room. With collections there is no
    instant to worry about: "Scenery" is linked into every scene and everything that goes into it
    appears in all of them, now and later. Each camera carries its own collection, with its camera
    and its background, linked only into its own scene.
    """
    bpy.ops.wm.read_factory_settings(use_empty=True)

    primary = exports[0]
    walkmesh = None
    markers = []
    cameras = []

    # The shared part. It is also where you have to model: anything landing outside this collection
    # will only be visible from one of the cameras.
    shared = bpy.data.collections.new("Scenery")

    for index, data in enumerate(exports):
        if index > 0:
            bpy.ops.scene.new(type="EMPTY")
        scene = bpy.context.scene
        scene.name = "Camera %s" % data.get("cameraIndex", index)
        scene.collection.children.link(shared)

        # This camera's own content, which must not be visible from the others.
        own = bpy.data.collections.new("Camera %s" % data.get("cameraIndex", index))
        scene.collection.children.link(own)

        geo = camera_geometry(data)
        configure_scene(data, geo)

        suffix = data.get("_suffix", "")
        set_target_collection(own)
        camera = build_camera(data, geo, "FieldCamera%s" % suffix)
        if walkmesh is None:
            # The walkmesh belongs to the field, not the camera: one only, shared by the scenes.
            set_target_collection(shared)
            walkmesh = build_walkmesh(data)
            markers = build_reference_markers(data, walkmesh)

        # The plate goes behind the whole walkmesh so it does not cover what you model.
        distance = 20.0
        if walkmesh:
            depths = [-(camera.matrix_world.inverted() @ v.co).z for v in walkmesh.data.vertices]
            distance = max(depths) * 1.25 if depths else 20.0
        set_target_collection(own)
        plate = attach_background(data, geo, camera, distance, "BackgroundPlate%s" % suffix)
        cameras.append((data, geo, camera, plate, distance))

    # Saved with the first camera active and "Scenery" as the active collection, which is where
    # modelling has to land for every camera to see it.
    bpy.context.window.scene = bpy.data.scenes[0]
    set_target_collection(shared)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(output))

    data = primary
    camera = cameras[0][2]
    geo = cameras[0][1]
    plate = cameras[0][3]
    distance = cameras[0][4]

    scale = data["sceneScale"]
    print("")
    print("Map %s (%s), camera %s" % (data["map"], data["mapName"], data["cameraIndex"]))
    print("  scale       : %s field units per metre" % scale)
    print("  render      : %sx%s" % (data["renderWidth"], data["renderHeight"]))
    print("  horiz. FOV  : %.2f degrees" % math.degrees(data["fovXRadians"]))
    print("  lens shift  : (%.4f, %.4f)" % (camera.data.shift_x, camera.data.shift_y))
    print("  camera at   : (%.2f, %.2f, %.2f) m" % tuple(camera.location))
    print("  aspect      : angular %.5f, pixel %.5f  ->  pixel aspect (%.5f, %.5f)"
          % (geo["angularAspect"], data["renderWidth"] / float(data["renderHeight"]),
             geo["pixelAspectX"], geo["pixelAspectY"]))
    print("  exported basis scale: |right| %.6f  |up| %.6f  |forward| %.6f"
          % geo["scales"])
    if walkmesh:
        print("  walkmesh    : %s vertices, %s faces" % (len(walkmesh.data.vertices), len(walkmesh.data.polygons)))
    if plate:
        span = background_scale(data)
        print("  background  : camera layers with no offset (scale %.3f), and BackgroundPlate at %.2f m" % (span, distance))
        if span > 1.0:
            print("                background.png covers %.1fx the frame: what lies outside the" % span)
            print("                camera is visible too, which is where the background scrolls.")
    print("  saved to    : %s" % os.path.abspath(output))

    if len(cameras) > 1:
        print("  cameras     : %d, one Blender scene for each (%s)"
              % (len(cameras), ", ".join(sc.name for sc in bpy.data.scenes)))
        print("                You model once, in the 'Scenery' collection: that one is linked into")
        print("                all of them, so whatever you put there is seen by every camera. Each")
        print("                camera also has its own collection with its camera and background.")

    for data, geo, camera, plate, distance in cameras:
        if len(cameras) > 1:
            print("")
            print("  -- camera %s: render %sx%s, FOV %.2f degrees"
                  % (data.get("cameraIndex", 0), data["renderWidth"], data["renderHeight"],
                     math.degrees(data["fovXRadians"])))
        check_one(data, camera, walkmesh)

    data = primary
    check = None
    if check:
        count, median_x, median_y, worst_x, worst_y = check
        print("")
        print("Check 1 (the camera): %s walkmesh vertices projected with the Blender" % count)
        print("camera against the game's projection.")
        print("  median deviation:  X %.4f px   Y %.4f px   (max %.2f / %.2f)"
              % (median_x, median_y, worst_x, worst_y))
        if max(median_x, median_y) > 0.5:
            print("  *** WRONG. This camera does not reproduce the game's. ***")
        elif max(worst_x, worst_y) > 2.0:
            print("  (a few stray vertices spike: the ones landing almost on the camera plane,")
            print("   where the perspective divide magnifies rounding. It is not the camera.)")

    print("")
    print("To model: Numpad 0. The background is two camera layers, under")
    print("Object Data Properties > Background Images. They are not geometry, so")
    print("nothing you model ever covers them:")
    print("  - 'Back'  opaque and enabled: visible where there is no geometry yet")
    print("  - 'Front' at 35% and disabled: enable it to trace over the model")
    print("BackgroundPlate is that same background as geometry, hidden. If you want")
    print("to check the layers framing against it, unhide it: they must match.")
    print("")
    print("Check 2 (Unity round trip): export the RefMarker objects to FBX, drop them")
    print("into the Unity scene without moving them and look at where they land in game.")
    for name, field, blender_pos in markers:
        print("  RefMarker%s  Blender (%.4f, %.4f, %.4f)  ->  expected field (%.0f, %.0f, %.0f)"
              % (name, blender_pos[0], blender_pos[1], blender_pos[2], field[0], field[1], field[2]))
    print("  In Unity they must land at field/%s, and the game log confirms it." % scale)


if __name__ == "__main__":
    main()
