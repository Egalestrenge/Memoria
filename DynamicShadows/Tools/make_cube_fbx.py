# Genera el FBX de prueba para Memoria.
# Uso:  blender --background --python DynamicShadows/Tools/make_cube_fbx.py -- <ruta_salida.fbx> [semi-tamano]
#
# Requisitos del importador de Memoria (ModelImporter.CreateCustomModelFromFbx):
#   - El objeto debe tener un material asignado; si no, GetMaterialIndex devuelve -1
#     y CreateCustomModel lanza IndexOutOfRangeException.
#   - Debe tener UVs para que se cree el canal de textura.
#   - Las coordenadas se leen en crudo: 1 unidad FBX = 1 unidad de campo de FFIX.

import sys
import bpy

argv = sys.argv[sys.argv.index("--") + 1:]
out_path = argv[0]
half = float(argv[1]) if len(argv) > 1 else 50.0

# Escena limpia
bpy.ops.wm.read_factory_settings(use_empty=True)

# Blender escribe la conversion metro->centimetro como "Lcl Scaling = 100" en el nodo Model,
# y FbxBone.GetLocalToWorldMatrix la hornea en los vertices al importar. Es decir, el modelo
# acaba 100 veces mas grande en el juego que en Blender: compensamos aqui.
# Verificado con: python DynamicShadows/Tools/dump_fbx.py <archivo.fbx>
blender_size = (half * 2.0) / 100.0

bpy.ops.mesh.primitive_cube_add(size=blender_size, location=(0.0, 0.0, 0.0))
cube = bpy.context.active_object
cube.name = "TestCube"
cube.data.name = "TestCubeMesh"

# UVs (el cubo primitivo ya trae uno, pero lo forzamos por si acaso)
if not cube.data.uv_layers:
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.uv.smart_project()
    bpy.ops.object.mode_set(mode="OBJECT")

# Material obligatorio
mat = bpy.data.materials.new(name="TestCubeMat")
mat.use_nodes = True
bsdf = mat.node_tree.nodes.get("Principled BSDF")
if bsdf:
    bsdf.inputs["Base Color"].default_value = (0.9, 0.1, 0.1, 1.0)
cube.data.materials.append(mat)

bpy.ops.export_scene.fbx(
    filepath=out_path,
    use_selection=False,
    apply_unit_scale=True,
    global_scale=1.0,
    axis_forward="-Z",
    axis_up="Y",
    mesh_smooth_type="FACE",
    use_triangles=True,
    path_mode="COPY",
)

print("EXPORTED %s (semi-tamano solicitado: %.2f unidades de campo)" % (out_path, half))
