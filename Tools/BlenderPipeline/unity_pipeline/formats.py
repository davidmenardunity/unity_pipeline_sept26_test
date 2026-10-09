"""Opening an asset in a scene of its own, and writing that scene back in the asset's format.

Import and export use the same axis and unit settings, so a file round-trips: an FBX opened from
Unity and pushed back unchanged comes out with the same orientation and scale.
"""

import os
import re
from contextlib import contextmanager

import bpy

# Blender 4.2+ names. glTF must be .glb: a .gltf's separate .bin and textures aren't downloaded.
LABELS = {
    ".fbx": "FBX", ".obj": "OBJ", ".glb": "glTF binary", ".stl": "STL", ".ply": "PLY",
    ".usd": "USD", ".usda": "USD", ".usdc": "USD", ".usdz": "USD", ".blend": "Blender",
}


def ext_of(path):
    return os.path.splitext(path)[1].lower()


def can_open(path):
    return ext_of(path) in LABELS


def import_file(filepath, ext):
    """Import into the context's scene (the caller makes the new scene current first)."""
    if ext == ".fbx":
        # The add-on importer: its axis conversion matches export_scene.fbx below.
        bpy.ops.import_scene.fbx(filepath=filepath)
    elif ext == ".obj":
        bpy.ops.wm.obj_import(filepath=filepath)
    elif ext == ".glb":
        bpy.ops.import_scene.gltf(filepath=filepath)
    elif ext == ".stl":
        bpy.ops.wm.stl_import(filepath=filepath)
    elif ext == ".ply":
        bpy.ops.wm.ply_import(filepath=filepath)
    elif ext in (".usd", ".usda", ".usdc", ".usdz"):
        bpy.ops.wm.usd_import(filepath=filepath)
    else:
        raise ValueError(f"Blender can't open {ext} files here")


def open_blend(filepath, name):
    """A .blend: its first scene becomes the asset's scene."""
    with bpy.data.libraries.load(filepath, link=False) as (src, dst):
        if not src.scenes:
            raise ValueError("this .blend has no scene")
        dst.scenes = [src.scenes[0]]
    scene = dst.scenes[0]
    scene.name = name
    return scene


@contextmanager
def unsuffixed_material_names(scene):
    """Blender names a material "Tree_6A_D.004" when the file already has a "Tree_6A_D" (an asset opened
    twice). Unity maps the model's materials by name, so the suffix would make it import a new, blank
    material: for the export, give the scene's materials their names back, then restore them."""
    used = {slot.material for o in scene.objects for slot in getattr(o, "material_slots", []) if slot.material}
    renamed = []
    try:
        for mat in sorted(used, key=lambda m: m.name):
            base = re.sub(r"\.\d{3}$", "", mat.name)
            if base == mat.name:
                continue
            other = bpy.data.materials.get(base)
            if other is not None and other in used:
                continue   # two different materials of that name in this scene: leave them apart
            if other is not None:
                renamed.append((other, other.name))
                other.name = base + ".pipeline-export"
            renamed.append((mat, mat.name))
            mat.name = base
        yield
    finally:
        for item, name in reversed(renamed):
            item.name = name


def export_scene(scene, filepath, ext):
    """Write everything in `scene` (the context's scene) to `filepath` in the asset's own format."""
    if ext in (".fbx", ".glb"):
        with unsuffixed_material_names(scene):
            _export(scene, filepath, ext)
    else:
        _export(scene, filepath, ext)


def _export(scene, filepath, ext):
    if ext == ".blend":
        bpy.data.libraries.write(filepath, {scene}, fake_user=True)
    elif ext == ".fbx":
        bpy.ops.export_scene.fbx(filepath=filepath, use_selection=False, add_leaf_bones=False)
    elif ext == ".obj":
        # No .mtl: it would appear in Unity as a new asset next to the model.
        bpy.ops.wm.obj_export(filepath=filepath, export_selected_objects=False, export_materials=False)
    elif ext == ".glb":
        bpy.ops.export_scene.gltf(filepath=filepath, export_format="GLB", use_selection=False)
    elif ext == ".stl":
        bpy.ops.wm.stl_export(filepath=filepath, export_selected_objects=False)
    elif ext == ".ply":
        bpy.ops.wm.ply_export(filepath=filepath, export_selected_objects=False)
    elif ext in (".usd", ".usda", ".usdc", ".usdz"):
        bpy.ops.wm.usd_export(filepath=filepath, selected_objects_only=False)
    else:
        raise ValueError(f"Blender can't write {ext} files here")
