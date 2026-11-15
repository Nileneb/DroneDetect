"""Blender headless: decimate every GLB in SRC to ~TARGET triangles and export to DST.
Usage: blender -b -P decimate.py -- SRC DST TARGET"""
import bpy, os, sys
src, dst, target = sys.argv[sys.argv.index("--") + 1:][:3]
target = int(target)
for name in sorted(os.listdir(src)):
    if not name.endswith(".glb"):
        continue
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=os.path.join(src, name))
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in meshes)
    ratio = min(1.0, target / max(1, tris))
    for o in meshes:
        modifier = o.modifiers.new("decimate", "DECIMATE")
        modifier.decimate_type = "COLLAPSE"
        modifier.ratio = ratio
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.modifier_apply(modifier=modifier.name)
    after = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in meshes)
    bpy.ops.export_scene.gltf(filepath=os.path.join(dst, name), export_format="GLB", export_apply=True)
    print(f"DECIMATED {name}: {tris} -> {after} triangles", flush=True)
