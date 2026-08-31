# Construye un proyecto de Blender a partir de lo que exporta EXPORTSCENE:
# camara colocada como la del juego, fondo del mapa encuadrado en esa camara,
# walkmesh como malla, y todo a escala metrica.
#
# Uso (desde la raiz del proyecto):
#   blender --background --factory-startup --python tools/blender/build_field_project.py -- <carpeta_export> [salida.blend]
#
# Ejemplo:
#   blender --background --factory-startup --python tools/blender/build_field_project.py -- ^
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
    """Campo (zurdo, Y arriba) -> Blender (diestro, Z arriba).

    Intercambiar Y y Z es una permutacion impar (determinante -1), y ese cambio
    de quiralidad es el que hace falta entre sistemas de distinta mano.

    Las negaciones de X y Z salen de medirlo: marcadores colocados en ejes
    conocidos y llevados por Blender -> FBX -> Unity -> juego volvian con X y Z
    cambiadas de signo. Es una rotacion de 180 grados sobre el eje vertical
    (determinante +1), que introduce la cadena de exportacion FBX. Compensarla
    aqui deja el viaje de ida y vuelta exacto.
    """
    return Vector((-v[0], -v[2], v[1]))


def read_export(folder, name="field.json"):
    with open(os.path.join(folder, name), "r", encoding="utf-8") as handle:
        data = json.load(handle)
    data["_folder"] = folder
    # La camara 0 se escribio sin sufijo, para no romper los proyectos ya hechos.
    suffix = "" if name == "field.json" else name[len("field"):-len(".json")]
    data["_suffix"] = suffix
    data["_background"] = "background%s.png" % suffix
    return data


def read_exports(folder):
    """Todas las camaras volcadas de un mapa, ordenadas por indice.

    Un field no es una vista: BGSCENE guarda una lista de BGCAM_DEF y el juego cambia entre ellas,
    asi que la misma habitacion puede tener varios fondos y varias proyecciones. La geometria es la
    misma; lo unico que cambia es desde donde se mira.
    """
    names = ["field.json"] if os.path.exists(os.path.join(folder, "field.json")) else []
    names += sorted(n for n in os.listdir(folder)
                    if re.match(r"^field_cam\d+\.json$", n))
    return [read_export(folder, name) for name in names]


def camera_geometry(data):
    """Lo que hace falta para reproducir la camara del juego en Blender.

    La base exportada es ortogonal pero NO ortonormal: |up| vale 1.0713. Ese
    numero es 15/14, el estiramiento del framebuffer de 320x224 de PSX mostrado
    en 4:3, y FFIX lo lleva dentro de la matriz de camara para que los modelos
    casen con los fondos, que se pintaron para esa proporcion.

    Una camara de Blender es ortonormal por construccion, asi que la escala hay
    que sacarla de la base y ponerla donde le corresponde, en las tangentes del
    campo de vision. El juego proyecta con la INVERSA de esta base, no con su
    traspuesta, y para una base escalada no son lo mismo: la inversa divide por
    el cuadrado de la norma. De ahi que el factor sea k/kz y no kz/k.

    Eso deja un aspecto angular (1.5257) que ya no coincide con el de pixeles
    (1.6343). La diferencia se declara como pixel aspect, y sale 1.0711, el
    reciproco de (4/3)/(320/224) = 0.93333 de PSX, que confirma de donde viene.

    Todo lo de aqui esta medido contra world_to_camera_view, no supuesto: la
    proyeccion resultante cae a 0.0002 px de la del juego.
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
    # Cuanto hay que corregir el aspecto de pixeles para llegar al angular.
    # Blender solo expresa el pixel aspect en el eje que queda >= 1: poner el
    # otro por debajo de 1 no hace absolutamente nada.
    needed = (width / height) / angular_aspect
    if needed >= 1.0:
        pixel_aspect_x, pixel_aspect_y = 1.0, needed
    else:
        pixel_aspect_x, pixel_aspect_y = 1.0 / needed, 1.0

    # Blender aplica el shift con el signo contrario al desplazamiento de
    # encuadre del juego. En Y el factor es el aspecto angular, no el de
    # pixeles: medido, d(u)/d(shift_x) = -1 y d(v)/d(shift_y) = -1.5257.
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
    """Hace activa una coleccion, que es donde caeran los objetos que se creen a partir de ahora.

    Los constructores enlazan en bpy.context.collection, asi que basta con mover el puntero de la
    vista antes de llamarlos. Se busca en la capa de vista de la escena activa: la coleccion tiene
    que estar ya enlazada en ella.
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
    # La camara de Blender mira por su -Z local; la de Unity por su +Z.
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
    """Cuantas veces el encuadre abarca background.png.

    El exportador captura el fondo entero, no solo lo que cabe en pantalla: un fondo de field es
    mayor que la ventana y el juego hace scroll moviendo la camara ortografica. La imagen crece por
    igual en los dos ejes y centrada en el encuadre, que es lo que permite colocarla con una sola
    escala uniforme y sin desplazamiento.
    """
    return float(data.get("backgroundScale", 1.0))


def frame_corners(data, geo, camera, distance, span=1.0):
    """Las cuatro esquinas del encuadre a una distancia dada, en coordenadas de mundo.

    Se obtienen invirtiendo la proyeccion:
        ndc.x = (vx / -vz) / tan_x + offset   ->   vx = (ndc.x - offset) * tan_x * d
    y se pueden verificar reproyectandolas, que es lo que hace main().
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
    """Capas de fondo sobre la camara: no son geometria, asi que nunca las tapa lo
    que se modele, y se activan con un clic en las propiedades de la camara.

    Sin offset. La imagen se ajusta al encuadre de la camara, y ese encuadre ya
    lleva el lens shift, asi que coincide con el render sin corregir nada. El
    desfase que se veia antes no era de la imagen: era la camara, que tenia el
    shift con el signo cambiado y una escala del 7% en su eje Y.
    """
    camera.data.show_background_images = True

    def add(depth, alpha, enabled):
        layer = camera.data.background_images.new()
        layer.image = image
        layer.alpha = alpha
        layer.display_depth = depth
        # STRETCH y no FIT: la imagen y el encuadre tienen ya la misma proporcion,
        # y asi ningun redondeo mete bandas por los lados.
        layer.frame_method = "STRETCH"
        layer.offset = (0.0, 0.0)
        # STRETCH ajusta la imagen al encuadre y scale la agranda uniformemente desde el centro,
        # que es exactamente como esta capturada: sin desplazamiento que corregir.
        layer.scale = background_scale(data)
        if hasattr(layer, "show_background_image"):
            layer.show_background_image = enabled
        return layer

    add("BACK", 1.0, True)
    add("FRONT", 0.35, False)


def attach_background(data, geo, camera, distance, name="BackgroundPlate"):
    path = os.path.join(data["_folder"], data.get("_background", "background.png"))
    if not os.path.exists(path):
        print("Sin background.png, se omite el fondo.")
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
    # use_nodes desaparece en Blender 6.0. En las versiones donde el material ya nace con arbol
    # de nodos no hay nada que activar, y tocarlo solo produce el aviso de deprecacion.
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
    # Patron de referencia con encuadre verificado. Se queda oculto: para trabajar
    # estan las capas de camara, que no las tapa la geometria.
    plate.hide_select = True
    plate.hide_render = False
    plate.hide_viewport = True

    attach_camera_layers(data, camera, image)
    return plate


def build_walkmesh(data):
    path = os.path.join(data["_folder"], "walkmesh.obj")
    if not os.path.exists(path):
        print("Sin walkmesh.obj, se omite la malla de colision.")
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
    # Es una referencia de navegacion, no geometria del escenario.
    obj.hide_render = True
    return obj


def build_reference_markers(data, walkmesh):
    """Marcadores para validar el viaje de ida y vuelta Blender -> FBX -> Unity.

    Es el unico tramo de la cadena que no se puede comprobar desde aqui: Blender
    y Unity tienen quiralidad distinta, y segun los ajustes de exportacion el
    eje de profundidad puede acabar invertido. Exportando estos marcadores y
    mirando donde caen en el juego se resuelve de una vez.
    """
    scale = data["sceneScale"]
    markers = []

    origin = bpy.data.objects.new("RefFieldOrigin", None)
    origin.empty_display_type = "PLAIN_AXES"
    origin.empty_display_size = 0.5
    origin.location = (0.0, 0.0, 0.0)
    bpy.context.collection.objects.link(origin)

    # Tres puntos separados en ejes distintos: si alguno se invierte, se ve.
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
    """Proyecta el walkmesh con la camara de Blender y lo compara con el juego.

    La verdad de referencia se reconstruye con la INVERSA de la base exportada,
    que es lo que aplica el juego. Usar la traspuesta parece equivalente y no lo
    es en cuanto la base tiene escala: el error se cuela en los dos lados de la
    comparacion y esta cuadra estando mal.

    Se ejecuta siempre. Si algun dia cambia el exportador o una version de
    Blender mueve un convenio, esto lo dice en la misma ejecucion.
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

    # La mediana, no el maximo. En un mapa con la camara cerca y el campo de vision ancho hay
    # vertices del walkmesh casi pegados al plano de la camara, y ahi la division en perspectiva
    # amplia cualquier diferencia de coma flotante: el maximo se dispara mientras el mapa entero
    # esta exacto. Medido en el 153: mediana 0.16 px y maximo 2.87 px, con la camara correcta.
    errors_x.sort()
    errors_y.sort()
    middle = len(errors_x) // 2
    return (len(errors_x), errors_x[middle], errors_y[middle], errors_x[-1], errors_y[-1])


def check_one(data, camera, walkmesh):
    check = verify_projection(data, camera, walkmesh)
    if not check:
        return
    count, median_x, median_y, worst_x, worst_y = check
    print("  camara      : %s vertices del walkmesh, desviacion tipica X %.4f px  Y %.4f px"
          " (maxima %.2f / %.2f)" % (count, median_x, median_y, worst_x, worst_y))
    if max(median_x, median_y) > 0.5:
        print("  *** MAL. La camara no reproduce la del juego. ***")
    elif max(worst_x, worst_y) > 2.0:
        print("                (algun vertice suelto se dispara: los que caen casi sobre el plano")
        print("                 de la camara, donde la perspectiva amplifica el redondeo.)")


def configure_scene(data, geo):
    scene = bpy.context.scene
    scene.render.resolution_x = data["renderWidth"]
    scene.render.resolution_y = data["renderHeight"]
    scene.render.resolution_percentage = 100
    # El aspecto angular de la camara del juego no coincide con el de pixeles
    # (herencia del 320x224 de PSX en 4:3). Sin esto la vista sale achatada un
    # 6.6% en vertical y no hay offset que lo arregle.
    scene.render.pixel_aspect_x = geo["pixelAspectX"]
    scene.render.pixel_aspect_y = geo["pixelAspectY"]
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    if not argv:
        raise SystemExit("Falta la carpeta de exportacion (MemoriaSceneExport/<mapa>)")

    root = os.path.abspath(argv[0])

    # Una carpeta sin field.json pero con subcarpetas que si lo tienen es la raiz del volcado:
    # se procesan todas. EXPORTSCENE deja un mapa cada vez que entras en uno, asi que a poco que
    # juegues se acumulan, y hacerlos de uno en uno no escala.
    if not os.path.exists(os.path.join(root, "field.json")):
        pending = sorted(name for name in os.listdir(root)
                         if os.path.exists(os.path.join(root, name, "field.json")))
        if not pending:
            raise SystemExit("En %s no hay ningun field.json, ni suelto ni en subcarpetas." % root)
        for name in pending:
            folder = os.path.join(root, name)
            exports = read_exports(folder)
            data = exports[0]
            output = os.path.join(folder, "field_%s.blend" % data["map"])
            # No se pisa un proyecto que ya existe: puede tener modelado dentro, y este script
            # arranca de una escena vacia. Para actualizar uno sin perder el trabajo esta
            # update_field_project.py.
            if os.path.exists(output):
                print("mapa %s: ya existe %s, se omite (usa update_field_project.py para actualizarlo)"
                      % (name, os.path.basename(output)))
                continue
            build_project(exports, folder, output)
        return

    folder = root
    exports = read_exports(folder)
    output = argv[1] if len(argv) > 1 else os.path.join(folder, "field_%s.blend" % exports[0]["map"])
    build_project(exports, folder, output)


def build_project(exports, folder, output):
    """Un archivo con la geometria compartida y una escena de Blender por camara.

    La resolucion y el pixel aspect son de la ESCENA, no de la camara, y cada BGCAM puede tener
    encuadre propio: no caben dos en una sola escena.

    Lo que las une es una COLECCION, no una copia de escena. Aqui hubo un scene.new(LINK_COPY) y
    estaba mal por dos motivos, los dos comprobados abriendo el archivo generado: copia la lista de
    objetos en el instante en que se crea, asi que lo que modelases despues aparecia SOLO en la
    escena activa -justo lo que se suponia que resolvia-, y ademas la segunda escena heredaba el
    BackgroundPlate de la primera, un plano enorme con el fondo pintado plantado en mitad de la
    sala. Con colecciones no hay instante que valga: "Escenario" se enlaza en todas las escenas y
    todo lo que entre en ella aparece en todas, ahora y luego. Cada camara se lleva la suya, con su
    camara y su fondo, enlazada solo en su escena.
    """
    bpy.ops.wm.read_factory_settings(use_empty=True)

    primary = exports[0]
    walkmesh = None
    markers = []
    cameras = []

    # Lo compartido. Es tambien donde tienes que modelar: lo que caiga fuera de esta coleccion solo
    # se vera desde una de las camaras.
    shared = bpy.data.collections.new("Escenario")

    for index, data in enumerate(exports):
        if index > 0:
            bpy.ops.scene.new(type="EMPTY")
        scene = bpy.context.scene
        scene.name = "Camara %s" % data.get("cameraIndex", index)
        scene.collection.children.link(shared)

        # Lo propio de esta camara, que no debe verse desde las demas.
        own = bpy.data.collections.new("Camara %s" % data.get("cameraIndex", index))
        scene.collection.children.link(own)

        geo = camera_geometry(data)
        configure_scene(data, geo)

        suffix = data.get("_suffix", "")
        set_target_collection(own)
        camera = build_camera(data, geo, "FieldCamera%s" % suffix)
        if walkmesh is None:
            # El walkmesh es del field, no de la camara: uno solo, compartido por las escenas.
            set_target_collection(shared)
            walkmesh = build_walkmesh(data)
            markers = build_reference_markers(data, walkmesh)

        # El plano va detras de todo el walkmesh para que no tape lo que modeles.
        distance = 20.0
        if walkmesh:
            depths = [-(camera.matrix_world.inverted() @ v.co).z for v in walkmesh.data.vertices]
            distance = max(depths) * 1.25 if depths else 20.0
        set_target_collection(own)
        plate = attach_background(data, geo, camera, distance, "BackgroundPlate%s" % suffix)
        cameras.append((data, geo, camera, plate, distance))

    # Se guarda con la primera camara activa y con "Escenario" como coleccion activa, que es donde
    # tiene que caer lo que se modele para que lo vean todas las camaras.
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
    print("Mapa %s (%s), camara %s" % (data["map"], data["mapName"], data["cameraIndex"]))
    print("  escala      : %s unidades de campo por metro" % scale)
    print("  render      : %sx%s" % (data["renderWidth"], data["renderHeight"]))
    print("  FOV horiz.  : %.2f grados" % math.degrees(data["fovXRadians"]))
    print("  lens shift  : (%.4f, %.4f)" % (camera.data.shift_x, camera.data.shift_y))
    print("  camara en   : (%.2f, %.2f, %.2f) m" % tuple(camera.location))
    print("  aspecto     : angular %.5f, de pixeles %.5f  ->  pixel aspect (%.5f, %.5f)"
          % (geo["angularAspect"], data["renderWidth"] / float(data["renderHeight"]),
             geo["pixelAspectX"], geo["pixelAspectY"]))
    print("  escala de la base exportada: |right| %.6f  |up| %.6f  |forward| %.6f"
          % geo["scales"])
    if walkmesh:
        print("  walkmesh    : %s vertices, %s caras" % (len(walkmesh.data.vertices), len(walkmesh.data.polygons)))
    if plate:
        span = background_scale(data)
        print("  fondo       : capas de camara sin offset (escala %.3f), y BackgroundPlate a %.2f m" % (span, distance))
        if span > 1.0:
            print("                background.png cubre %.1fx el encuadre: se ve tambien lo que queda" % span)
            print("                fuera de camara, que es donde el fondo hace scroll.")
    print("  guardado en : %s" % os.path.abspath(output))

    if len(cameras) > 1:
        print("  camaras     : %d, una escena de Blender por cada una (%s)"
              % (len(cameras), ", ".join(sc.name for sc in bpy.data.scenes)))
        print("                Se modela una vez, en la coleccion 'Escenario': esa esta enlazada")
        print("                en todas, asi que lo que metas ahi lo ven todas las camaras. Cada")
        print("                camara tiene ademas su propia coleccion con su camara y su fondo.")

    for data, geo, camera, plate, distance in cameras:
        if len(cameras) > 1:
            print("")
            print("  -- camara %s: render %sx%s, FOV %.2f grados"
                  % (data.get("cameraIndex", 0), data["renderWidth"], data["renderHeight"],
                     math.degrees(data["fovXRadians"])))
        check_one(data, camera, walkmesh)

    data = primary
    check = None
    if check:
        count, median_x, median_y, worst_x, worst_y = check
        print("")
        print("Comprobacion 1 (la camara): %s vertices del walkmesh proyectados con la camara" % count)
        print("de Blender contra la proyeccion del juego.")
        print("  desviacion tipica:  X %.4f px   Y %.4f px   (maxima %.2f / %.2f)"
              % (median_x, median_y, worst_x, worst_y))
        if max(median_x, median_y) > 0.5:
            print("  *** MAL. La camara no reproduce la del juego. ***")
        elif max(worst_x, worst_y) > 2.0:
            print("  (algun vertice suelto se dispara: son los que caen casi sobre el plano de la")
            print("   camara, donde la division en perspectiva amplifica el redondeo. No es la camara.)")

    print("")
    print("Para modelar: Numpad 0. El fondo son dos capas de la camara, en")
    print("Object Data Properties > Background Images. No son geometria, asi que")
    print("no las tapa nada de lo que modeles:")
    print("  - 'Back'  opaca y activa: se ve donde aun no hay geometria")
    print("  - 'Front' al 35% y desactivada: actívala para calcar por encima del modelo")
    print("BackgroundPlate es ese mismo fondo como geometria, oculto. Si quieres")
    print("contrastar el encuadre de las capas, muestralo: deben coincidir.")
    print("")
    print("Comprobacion 2 (ida y vuelta a Unity): exporta los RefMarker en FBX, metelos")
    print("en la escena de Unity sin moverlos y mira donde caen en el juego.")
    for name, field, blender_pos in markers:
        print("  RefMarker%s  Blender (%.4f, %.4f, %.4f)  ->  campo esperado (%.0f, %.0f, %.0f)"
              % (name, blender_pos[0], blender_pos[1], blender_pos[2], field[0], field[1], field[2]))
    print("  En Unity deben quedar en campo/%s, y el log del juego lo confirma." % scale)


if __name__ == "__main__":
    main()
