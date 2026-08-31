# Actualiza la camara, el fondo y el walkmesh de un .blend que ya existe, SIN TOCAR lo que hayas
# modelado.
#
# Es lo que hay que usar cuando el export cambia y ya tienes trabajo hecho: build_field_project.py
# arranca de una escena vacia y se lo llevaria por delante.
#
#   blender --background --factory-startup ^
#     --python tools\blender\update_field_project.py -- <archivo.blend> <carpeta_export>
#
# Solo borra y rehace los objetos que genera la propia herramienta (los de OWNED) y la imagen del
# fondo. Todo lo demas se queda como esta.
#
# Cierra Blender antes de ejecutarlo: si lo tienes abierto y guardas despues, tu version en memoria
# sobrescribe esta. Blender deja un .blend1 con la version anterior por si acaso.

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

    # La imagen se recarga a proposito: el archivo del juego cambia de tamano cuando cambia el
    # rango de scroll del mapa, y la copia vieja que Blender tiene en memoria seguiria mandando.
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
    print("  rehecho   : %s" % (", ".join(removed) if removed else "nada (no habia)"))
    print("  intacto   : %d objeto(s)%s" % (len(kept), (" -> " + ", ".join(kept[:12])) if kept else ""))
    print("  fondo     : background.png cubre %.4fx el encuadre" % bfp.background_scale(data))
    print("  plano a   : %.2f m" % distance)

    check = bfp.verify_projection(data, camera, walkmesh)
    if check:
        count, median_x, median_y, worst_x, worst_y = check
        print("  camara    : %d vertices del walkmesh, desviacion tipica X %.4f px  Y %.4f px"
              " (maxima %.2f / %.2f)" % (count, median_x, median_y, worst_x, worst_y))
        if max(median_x, median_y) > 0.5:
            print("  *** MAL. La camara no reproduce la del juego. ***")

    # Lo que apoya en el suelo tiene que tener su Z minima en cero: el walkmesh esta ahi.
    print("")
    print("  Alturas de tu geometria (el suelo del juego esta en Z = 0):")
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
            note = "   <- %+.0f mm sobre el suelo" % (low * 1000.0)
        print("    %-22s Z [%7.4f, %7.4f] m   (campo %7.1f)%s" % (name, low, high, low * scale, note))


if __name__ == "__main__":
    main()
