# Updates the camera, background and walkmesh of an existing .blend, WITHOUT TOUCHING anything you
# have modelled.
#
# This is what to use when the export changes and you already have work in there:
# build_field_project.py starts from an empty scene and would wipe it out.
#
#   blender --background --factory-startup ^
#     --python DynamicShadows\Tools\blender\update_field_project.py -- <file.blend> <export_folder>
#
# It only deletes and rebuilds the objects the tool itself generates (those in OWNED) and the
# background image. Everything else is left as it is.
#
# Close Blender before running it: if it is open and you save afterwards, your in-memory version
# overwrites this one. Blender leaves a .blend1 with the previous version just in case.

import os
import re
import sys

import bpy

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import build_field_project as bfp

OWNED = ("FieldCamera", "BackgroundPlate", "BackgroundOverlay", "Walkmesh",
         "RefFieldOrigin", "RefMarkerX", "RefMarkerY", "RefMarkerZ")

# A field with several BGCAMs gets one camera and one background plate per camera, named
# "<base>_cam<N>" by build_field_project. Matching OWNED by exact name missed those: they were
# never refreshed AND they were counted as geometry the user had modelled, which produced a
# nonsense height warning about a background plate sitting 12 metres under the floor.
_OWNED_RE = re.compile(r"^(?:%s)(?:_cam\d+)?$" % "|".join(OWNED))


def is_owned(name):
    """True for anything this tool generates, including the per-camera variants."""
    return _OWNED_RE.match(name) is not None


def collections_of(obj):
    """Where an object currently lives. Rebuilding puts the new one back in the same place.

    This is what makes the script safe on any project layout. build_field_project puts each
    camera in its own collection and the shared geometry in another, but a project made before
    that, or reorganised by hand, may have everything directly in the scene collection. Reading
    the position off the object being replaced works for all of them, instead of guessing by name.
    """
    return list(obj.users_collection)


def place(obj, targets, fallback):
    for c in list(obj.users_collection):
        c.objects.unlink(obj)
    for c in (targets or [fallback]):
        c.objects.link(obj)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    if len(argv) < 2:
        raise SystemExit("Usage: ... -- <file.blend> <export_folder>")

    blend_path = os.path.abspath(argv[0])
    folder = os.path.abspath(argv[1])
    bpy.ops.wm.open_mainfile(filepath=blend_path)

    # Every camera of the map, not just camera 0. A field with several BGCAMs has one Blender scene
    # per camera, and refreshing only the first left the rest pointing at the previous export -
    # with nothing saying so.
    exports = bfp.read_exports(folder)
    fallback = bpy.context.scene.collection

    # Where each generated object lives right now, before anything is removed.
    homes = {}
    for name in list(bpy.data.objects.keys()):
        if is_owned(name):
            homes[name] = collections_of(bpy.data.objects[name])

    # How each camera's background layers were being displayed. attach_camera_layers recreates
    # them from scratch with the defaults, so without this an update quietly undoes the one thing
    # you are meant to change by hand: putting the reference in FRONT at partial alpha to trace
    # over the model. Losing that on every refresh is not acceptable.
    layer_prefs = {}
    for name in list(bpy.data.objects.keys()):
        obj = bpy.data.objects[name]
        if is_owned(name) and obj.type == "CAMERA":
            layer_prefs[name] = [
                (l.alpha, l.display_depth, getattr(l, "show_background_image", True))
                for l in obj.data.background_images]

    # Which scene each camera drives, so it can be pointed at the rebuilt one.
    scene_of = {}
    for name in homes:
        obj = bpy.data.objects[name]
        for scene in bpy.data.scenes:
            if scene.camera is obj:
                scene_of[name] = scene

    removed = sorted(homes)
    for name in removed:
        bpy.data.objects.remove(bpy.data.objects[name], do_unlink=True)

    # The image is deliberately reloaded: the game's file changes size when the map's scroll range
    # changes, and the stale copy Blender holds in memory would otherwise keep winning.
    for image in list(bpy.data.images):
        if image.name.startswith("background"):
            bpy.data.images.remove(image)

    walkmesh = None
    markers = []
    report = []

    for index, data in enumerate(exports):
        suffix = data.get("_suffix", "")
        geo = bfp.camera_geometry(data)
        cam_name = "FieldCamera%s" % suffix
        plate_name = "BackgroundPlate%s" % suffix

        camera = bfp.build_camera(data, geo, cam_name)
        place(camera, homes.get(cam_name), fallback)

        if walkmesh is None:
            # The walkmesh belongs to the field, not to the camera: one only, shared.
            walkmesh = bfp.build_walkmesh(data)
            if walkmesh is not None:
                place(walkmesh, homes.get("Walkmesh"), fallback)
            # An object that did not exist before has no home to remember. The shared geometry
            # keeps together: markers follow the walkmesh rather than landing loose in whichever
            # scene happened to be active.
            shared_home = homes.get("Walkmesh") or homes.get("RefFieldOrigin")
            markers = bfp.build_reference_markers(data, walkmesh)
            for name, field, blender_pos in markers:
                obj = bpy.data.objects.get("RefMarker" + name)
                if obj is not None:
                    place(obj, homes.get("RefMarker" + name) or shared_home, fallback)
            origin = bpy.data.objects.get("RefFieldOrigin")
            if origin is not None:
                place(origin, homes.get("RefFieldOrigin") or shared_home, fallback)

        distance = 20.0
        if walkmesh:
            depths = [-(camera.matrix_world.inverted() @ v.co).z for v in walkmesh.data.vertices]
            distance = max(depths) * 1.25 if depths else 20.0
        plate = bfp.attach_background(data, geo, camera, distance, plate_name)
        if plate is not None:
            place(plate, homes.get(plate_name), fallback)

        # Put the display settings back on the freshly built layers.
        for layer, (alpha, depth, shown) in zip(camera.data.background_images,
                                                layer_prefs.get(cam_name, [])):
            layer.alpha = alpha
            layer.display_depth = depth
            if hasattr(layer, "show_background_image"):
                layer.show_background_image = shown

        # Resolution and pixel aspect belong to the SCENE, and each camera can frame differently.
        scene = scene_of.get(cam_name)
        if scene is None:
            scene = bpy.data.scenes[min(index, len(bpy.data.scenes) - 1)]
        scene.camera = camera
        scene.render.resolution_x = data["renderWidth"]
        scene.render.resolution_y = data["renderHeight"]
        scene.render.resolution_percentage = 100
        scene.render.pixel_aspect_x = geo["pixelAspectX"]
        scene.render.pixel_aspect_y = geo["pixelAspectY"]

        report.append((data, geo, camera, scene, distance))

    kept = [o.name for o in bpy.data.objects if not is_owned(o.name)]
    bpy.ops.wm.save_mainfile(filepath=blend_path)

    print("")
    print("Updated %s" % blend_path)
    print("  rebuilt   : %s" % (", ".join(removed) if removed else "nothing (none present)"))
    print("  untouched : %d object(s)%s" % (len(kept), (" -> " + ", ".join(kept[:12])) if kept else ""))

    for data, geo, camera, scene, distance in report:
        label = "camera %s" % data.get("cameraIndex", 0)
        print("  %-10s: scene %r, %sx%s, plate at %.2f m, background covers %.4fx the frame"
              % (label, scene.name, data["renderWidth"], data["renderHeight"], distance,
                 bfp.background_scale(data)))
        check = bfp.verify_projection(data, camera, walkmesh)
        if check:
            count, median_x, median_y, worst_x, worst_y = check
            print("              %d walkmesh vertices, median deviation X %.4f px  Y %.4f px"
                  " (max %.2f / %.2f)" % (count, median_x, median_y, worst_x, worst_y))
            if max(median_x, median_y) > 0.5:
                print("              *** WRONG. This camera does not reproduce the game's. ***")

    scale = exports[0]["sceneScale"]
    print("")
    print("  Heights of your geometry (the game floor is at Z = 0):")
    for obj in bpy.data.objects:
        if obj.type != "MESH" or is_owned(obj.name) or not obj.data.vertices:
            continue
        zs = [(obj.matrix_world @ v.co).z for v in obj.data.vertices]
        low, high = min(zs), max(zs)
        note = ""
        if abs(low) > 0.001:
            note = "   <- %+.0f mm above the floor" % (low * 1000.0)
        print("    %-22s Z [%7.4f, %7.4f] m   (field %7.1f)%s" % (obj.name, low, high, low * scale, note))


if __name__ == "__main__":
    main()
