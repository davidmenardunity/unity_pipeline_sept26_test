"""Unity Pipeline for Blender: browse a Unity project through Pipeline, open an asset in Blender, and push
it back to the workbench it came from, as a new revision.

Talks to Pipeline Explorer (Tools/PipelineExplorer), which runs locally, holds the bearer token and
calls Pipeline. See ../README.md.
"""

import bpy

from . import ops, state, ui

_classes = ui.classes + ops.classes


def register():
    for cls in _classes:
        bpy.utils.register_class(cls)
    ui.register_props()


def unregister():
    state.stop_all()
    ui.unregister_props()
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
    state.S.reset()
