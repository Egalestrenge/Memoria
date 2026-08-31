# Updates the camera, background and walkmesh of an existing .blend, WITHOUT TOUCHING anything you
# modelado.
#
# This is what to use when the export changes and you already have work in there:
# build_field_project.py starts from an empty scene and would wipe it out.
#
#   blender --background --factory-startup ^
#     --python tools\blender\update_field_project.py -- <archivo.blend> <carpeta_export>
#
# It only deletes and rebuilds the objects the tool itself generates (those in OWNED) and the
# background image. Everything else is left as it is.
#
# Close Blender before running it: if it is open and you save afterwards, your in-memory version
# overwrites this one. Blender leaves a .blend1 with the previous version just in case.

import os
import sys

import bpy

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
import build_field_project as bfp

OWNED = ("FieldCamera", "BackgroundPlate", "BackgroundOverlay", "Walkmesh",
         "RefFieldOrigin", "RefMarkerX", "RefMarkerY", "RefMarkerZ")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    if len(argv) < 2:
        raise SystemExit("Uso: ... -- <archivo.blend> <carpeta_export>")

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

    kept = [o.name for o in bpy.data.objects if o.name not in OWNED]
    bpy.ops.wm.save_mainfile(filepath=blend_path)

    print("")
    print("Actualizado %s" % blend_path)
    print("  rebuilt   : %s" % (", ".join(removed) if removed else "nothing (none present)"))
    print("  intacto   : %d objeto(s)%s" % (len(kept), (" -> " + ", ".join(kept[:12])) if kept else ""))
    print("  background: background.png covers %.4fx the frame" % bfp.background_scale(data))
    print("  plano a   : %.2f m" % distance)

    check = bfp.verify_projection(data, camera, walkmesh)
    if check:
        count, median_x, median_y, worst_x, worst_y = check
        print("  camera    : %d walkmesh vertices, std deviation X %.4f px  Y %.4f px"
              " (maxima %.2f / %.2f)" % (count, median_x, median_y, worst_x, worst_y))
        if max(median_x, median_y) > 0.5:
            print("  *** WRONG. This camera does not reproduce the game's. ***")

    # Anything resting on the floor must have its minimum Z at zero: that is where the walkmesh is.
    print("")
    print("  Heights of your geometry (the game floor is at Z = 0):")
    scale = data["sceneScale"]
    rows = []
    for obj in bpy.data.objects:
        if obj.type != "MESH" or obj.name in OWNED or not obj.data.vertices:
            continue
        zs = [(obj.matrix_world @ v.co).z for v in obj.data.vertices]
        rows.append((obj.name, min(zs), max(zs)))
    rows.sort(key=lambda r: r[1])
    for name, low, high in rows:
        note = ""
        if abs(low) > 0.02:
            note = "   <- %+.0f mm above the floor" % (low * 1000.0)
        print("    %-22s Z [%7.4f, %7.4f] m   (campo %7.1f)%s" % (name, low, high, low * scale, note))


if __name__ == "__main__":
    main()
