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


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    if len(argv) < 2:
        raise SystemExit("Usage: ... -- <file.blend> <export_folder>")

    blend_path = os.path.abspath(argv[0])
    folder = os.path.abspath(argv[1])
    bpy.ops.wm.open_mainfile(filepath=blend_path)

    data = bfp.read_export(folder)
    geo = bfp.camera_geometry(data)

    removed = []
    for name in OWNED:
        obj = bpy.data.objects.get(name)
        if obj is None:
            continue
        removed.append(name)
        bpy.data.objects.remove(obj, do_unlink=True)

    # The image is deliberately reloaded: the game's file changes size when the map's scroll range
    # changes, and the stale copy Blender holds in memory would otherwise keep winning.
    for image in list(bpy.data.images):
        if image.name.startswith("background"):
            bpy.data.images.remove(image)

    bfp.configure_scene(data, geo)
    camera = bfp.build_camera(data, geo)
    walkmesh = bfp.build_walkmesh(data)

    distance = 20.0
    if walkmesh:
        depths = [-(camera.matrix_world.inverted() @ v.co).z for v in walkmesh.data.vertices]
        distance = max(depths) * 1.25 if depths else 20.0
    plate = bfp.attach_background(data, geo, camera, distance)
    markers = bfp.build_reference_markers(data, walkmesh)

    kept = [o.name for o in bpy.data.objects if not is_owned(o.name)]
    # Generated objects belonging to the other cameras. This script only refreshes camera 0, so on
    # a multi-camera map these stay as they were and the project is only half up to date.
    other_cams = sorted(o.name for o in bpy.data.objects
                        if is_owned(o.name) and o.name not in OWNED)
    bpy.ops.wm.save_mainfile(filepath=blend_path)

    print("")
    print("Updated %s" % blend_path)
    print("  rebuilt   : %s" % (", ".join(removed) if removed else "nothing (none present)"))
    print("  untouched : %d object(s)%s" % (len(kept), (" -> " + ", ".join(kept[:12])) if kept else ""))
    print("  background: background.png covers %.4fx the frame" % bfp.background_scale(data))
    print("  plate at  : %.2f m" % distance)
    if other_cams:
        print("  *** NOT REFRESHED: %s" % ", ".join(other_cams))
        print("      This map has more than one BGCAM and this script only handles camera 0, so")
        print("      those keep the old camera and background. Camera 0 is correct; the other")
        print("      scenes in this file are not. Rebuild from scratch with build_field_project.py")
        print("      if you have no modelling to lose.")

    check = bfp.verify_projection(data, camera, walkmesh)
    if check:
        count, median_x, median_y, worst_x, worst_y = check
        print("  camera    : %d walkmesh vertices, std deviation X %.4f px  Y %.4f px"
              " (max %.2f / %.2f)" % (count, median_x, median_y, worst_x, worst_y))
        if max(median_x, median_y) > 0.5:
            print("  *** WRONG. This camera does not reproduce the game's. ***")

    # Anything resting on the floor must have its minimum Z at zero: that is where the walkmesh is.
    print("")
    print("  Heights of your geometry (the game floor is at Z = 0):")
    scale = data["sceneScale"]
    rows = []
    for obj in bpy.data.objects:
        if obj.type != "MESH" or is_owned(obj.name) or not obj.data.vertices:
            continue
        zs = [(obj.matrix_world @ v.co).z for v in obj.data.vertices]
        rows.append((obj.name, min(zs), max(zs)))
    rows.sort(key=lambda r: r[1])
    for name, low, high in rows:
        note = ""
        if abs(low) > 0.02:
            note = "   <- %+.0f mm above the floor" % (low * 1000.0)
        print("    %-22s Z [%7.4f, %7.4f] m   (field %7.1f)%s" % (name, low, high, low * scale, note))


if __name__ == "__main__":
    main()
