# Generates the test FBX for Memoria.
# Usage:  blender --background --python DynamicShadows/Tools/make_cube_fbx.py -- <out_path.fbx> [half_size]
#
# What Memoria's importer requires (ModelImporter.CreateCustomModelFromFbx):
#   - The object must have a material assigned; without one GetMaterialIndex returns -1
#     and CreateCustomModel throws IndexOutOfRangeException.
#   - It must have UVs for the texture channel to be created.
#   - Coordinates are read raw: 1 FBX unit = 1 FFIX field unit.

import sys
import bpy

argv = sys.argv[sys.argv.index("--") + 1:]
out_path = argv[0]
half = float(argv[1]) if len(argv) > 1 else 50.0

# Clean scene
bpy.ops.wm.read_factory_settings(use_empty=True)

# Blender writes the metre->centimetre conversion as "Lcl Scaling = 100" on the Model node, and
# FbxBone.GetLocalToWorldMatrix bakes it into the vertices on import. That is, the model ends up
# 100 times bigger in the game than in Blender: compensated for here.
# Verified with: python DynamicShadows/Tools/dump_fbx.py <file.fbx>
blender_size = (half * 2.0) / 100.0

bpy.ops.mesh.primitive_cube_add(size=blender_size, location=(0.0, 0.0, 0.0))
cube = bpy.context.active_object
cube.name = "TestCube"
cube.data.name = "TestCubeMesh"

# UVs (the primitive cube already has one, but force it just in case)
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

print("EXPORTED %s (requested half-size: %.2f field units)" % (out_path, half))
