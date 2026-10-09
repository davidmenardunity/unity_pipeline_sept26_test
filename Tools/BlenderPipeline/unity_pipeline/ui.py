"""The sidebar (3D Viewport > N > Pipeline), the branch and workbench menus, and the preferences."""

import os
import time

import bpy
from bpy.props import BoolProperty, IntProperty, StringProperty

from . import client, formats
from .ops import session
from .state import S, busy_text

ICON = {"INFO": "INFO", "OK": "CHECKMARK", "ERROR": "ERROR"}


def version():
    """The add-on's version, from its manifest: shown in the sidebar, to tell which code Blender loaded."""
    try:
        with open(os.path.join(os.path.dirname(__file__), "blender_manifest.toml"), encoding="utf8") as f:
            return next((l.split('"')[1] for l in f if l.startswith("version")), "?")
    except OSError:
        return "?"


VERSION = version()


def size_text(n):
    return f"{n} B" if n < 1024 else f"{n / 1024:.1f} KB" if n < 1048576 else f"{n / 1048576:.1f} MB"


class UNITY_PIPELINE_MT_branches(bpy.types.Menu):
    bl_idname = "UNITY_PIPELINE_MT_branches"
    bl_label = "Branch"

    def draw(self, context):
        for b in S.branches:
            n = len(S.workbenches_on(b))
            self.layout.operator("unity_pipeline.pick_branch", text=f"{b}{'' if n else '  (no workbench)'}",
                                 icon="CHECKMARK" if b == S.branch else "BLANK1").branch = b


class UNITY_PIPELINE_MT_workbenches(bpy.types.Menu):
    bl_idname = "UNITY_PIPELINE_MT_workbenches"
    bl_label = "Workbench"

    def draw(self, context):
        wbs = S.workbenches_on(S.branch)
        if not wbs:
            self.layout.label(text=f"No workbench on {S.branch}: create one in Pipeline Explorer")

        def item(w):
            commit = (w.get("upstreamRevision") or "")[:7]
            note = (f"  ({' or '.join(w['gitBranchCandidates'])}?)" if not w.get("gitBranch") and w.get("gitBranchCandidates")
                    else f"  (behind {S.branch} @{S.heads[S.branch][:7]})" if S.behind(w) else "")
            name = w.get("name") or ""
            label = f"{name}  ({w['workbenchId'][:8]})" if name and not (name.startswith("wb-") and len(name) == 35) else w["workbenchId"][:8]
            self.layout.operator("unity_pipeline.pick_workbench", text=f"{label}  @{commit}{note}",
                                 icon="CHECKMARK" if w["workbenchId"] == S.wb else "BLANK1").workbench = w["workbenchId"]

        for w in wbs:
            item(w)
        unknown = S.workbenches_unknown()
        if unknown:
            self.layout.separator()
            self.layout.label(text="Branch unknown (made elsewhere, from an older commit)")
            for w in unknown:
                item(w)


def wrap(text, context):
    """Lines that fit the sidebar's width."""
    width = max(18, int(context.region.width / (7.5 * context.preferences.system.ui_scale)) - 7)
    lines, line = [], ""
    for w in text.split():
        if line and len(line) + 1 + len(w) > width:
            lines.append(line)
            line = ""
        line = f"{line} {w}".strip()
    return lines + [line] if line else lines


def status_line(layout, context):
    busy = busy_text()
    box = layout.box()
    if busy or S.last:
        level, text = ("BUSY", busy) if busy else S.last[:2]
        col = box.column(align=True)
        for i, l in enumerate(wrap(text, context)[:8]):
            col.label(text=l, icon=("SORTTIME" if level == "BUSY" else ICON[level]) if i == 0 else "BLANK1")
    else:
        box.label(text="Connect to start.", icon="INFO")


class UNITY_PIPELINE_PT_main(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Pipeline"
    bl_label = "Unity Pipeline"

    def draw(self, context):
        layout = self.layout
        status_line(layout, context)
        if not S.connected:
            layout.operator("unity_pipeline.connect", icon="LINKED")
            layout.label(text=f"Through Pipeline Explorer at {context.preferences.addons[__package__].preferences.server}")
            layout.operator("unity_pipeline.open_explorer", icon="URL")
            layout.label(text=f"Add-on version {VERSION}")
            return
        cfg = S.config
        col = layout.column(align=True)
        col.label(text=f"Project {cfg.get('projectId', '')[:8]} · org {cfg.get('organizationId')}", icon="PACKAGE")
        row = layout.row(align=True)
        row.label(text="Branch")
        row.menu("UNITY_PIPELINE_MT_branches", text=S.branch or "(pick)")
        row = layout.row(align=True)
        row.label(text="Workbench")
        row.menu("UNITY_PIPELINE_MT_workbenches", text=(S.wb or "(none)")[:8])
        layout.label(text=S.wb_status, icon="CHECKMARK" if S.rev else "SORTTIME" if S.wb else "ERROR")
        current = next((w for w in S.workbenches if w["workbenchId"] == S.wb), None)
        if current and S.behind(current):
            layout.label(text=f"Older than {S.branch}'s latest commit", icon="INFO")
        layout.operator("unity_pipeline.publish", text="Publish to git…", icon="EXPORT")
        row = layout.row(align=True)
        row.operator("unity_pipeline.connect", text="Refresh", icon="FILE_REFRESH")
        row.operator("unity_pipeline.open_explorer", text="Explorer", icon="URL")
        layout.label(text=f"Add-on version {VERSION}")


class UNITY_PIPELINE_PT_scene(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Pipeline"
    bl_label = "This scene"
    bl_parent_id = "UNITY_PIPELINE_PT_main"

    @classmethod
    def poll(cls, context):
        return session(context.scene) is not None

    def draw(self, context):
        s = session(context.scene)
        layout = self.layout
        col = layout.column(align=True)
        col.label(text=os.path.basename(s["path"]), icon="FILE_3D")
        col.label(text=os.path.dirname(s["path"]))
        col.label(text=f"{s['branch']} · workbench {s['wb'][:8]} · revision {s['rev']}")
        if S.wb == s["wb"] and S.rev and S.rev != s["rev"]:
            layout.label(text=f"The workbench is now at revision {S.rev}.", icon="INFO")
        layout.operator("unity_pipeline.push", text=f"Push to workbench {s['wb'][:8]} ({s['branch']})", icon="EXPORT")
        layout.operator("unity_pipeline.reopen", icon="FILE_REFRESH")


class UNITY_PIPELINE_PT_tree(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Pipeline"
    bl_label = "Project"
    bl_parent_id = "UNITY_PIPELINE_PT_main"

    @classmethod
    def poll(cls, context):
        return S.connected and S.rev

    def draw(self, context):
        layout = self.layout
        wm = context.window_manager
        row = layout.row(align=True)
        row.prop(wm, "unity_pipeline_filter", text="", icon="VIEWZOOM")
        row.prop(wm, "unity_pipeline_openable", text="", icon="FILE_3D")
        limit = context.preferences.addons[__package__].preferences.max_rows
        flt = wm.unity_pipeline_filter.lower()
        rows = []

        def walk(folder, depth):
            for path, is_folder in S.tree.get(folder, []):
                name = path.rsplit("/", 1)[-1]
                if name.endswith(".meta"):
                    continue
                if not is_folder and ((wm.unity_pipeline_openable and not formats.can_open(path)) or (flt and flt not in name.lower())):
                    continue
                rows.append((path, name, is_folder, depth))
                if is_folder and path in S.expanded:
                    if path in S.loading and path not in S.tree:
                        rows.append((None, "loading…", False, depth + 1))
                    walk(path, depth + 1)

        walk("", 0)
        col = layout.column(align=True)
        for path, name, is_folder, depth in rows[:limit]:
            row = col.row(align=True)
            row.alignment = "LEFT"
            for _ in range(depth):
                row.separator(factor=1.6)
            if path is None:
                row.label(text=name, icon="SORTTIME")
            elif is_folder:
                op = row.operator("unity_pipeline.toggle", text=name, emboss=False,
                                  icon="DOWNARROW_HLT" if path in S.expanded else "RIGHTARROW")
                op.path = path
            else:
                ok = formats.can_open(path)
                sub = row.row(align=True)
                sub.active = ok
                sub.operator("unity_pipeline.select", text=name, emboss=path == S.selected, depress=path == S.selected,
                             icon="FILE_3D" if ok else "FILE").path = path
        if len(rows) > limit:
            col.label(text=f"… {len(rows) - limit} more: filter, or close some folders.", icon="INFO")
        if not rows:
            col.label(text="Nothing here." if not flt else f"Nothing matches “{flt}”.")


class UNITY_PIPELINE_PT_asset(bpy.types.Panel):
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Pipeline"
    bl_label = "Selected asset"
    bl_parent_id = "UNITY_PIPELINE_PT_main"

    @classmethod
    def poll(cls, context):
        return S.connected and S.selected

    def draw(self, context):
        layout = self.layout
        path = S.selected
        col = layout.column(align=True)
        col.label(text=os.path.basename(path), icon="FILE_3D" if formats.can_open(path) else "FILE")
        col.label(text=os.path.dirname(path))
        a = S.selected_info
        if a is None:
            col.label(text="Reading…", icon="SORTTIME")
        else:
            info = a.get("info") or {}
            col.label(text=f"GUID {a.get('guid') or 'none'}")
            if info.get("size") is not None:
                col.label(text=f"{size_text(info['size'])} · revision {S.rev}")
        if formats.can_open(path):
            layout.operator("unity_pipeline.open", icon="IMPORT").path = path
        else:
            layout.label(text=f"Blender can't open {formats.ext_of(path) or 'this'} files.", icon="INFO")


class UNITY_PIPELINE_Preferences(bpy.types.AddonPreferences):
    bl_idname = __package__

    server: StringProperty(name="Pipeline Explorer", default=client.DEFAULT_SERVER,
                           description="Where Pipeline Explorer runs (dotnet run --project Tools/PipelineExplorer). It holds the token and calls Pipeline")
    cache_dir: StringProperty(name="Download folder", subtype="DIR_PATH", default="",
                              description="Where opened assets are saved. Empty: a folder in the system temp folder")
    max_rows: IntProperty(name="Tree rows", default=300, min=50, max=5000, description="At most this many rows in the project tree")
    picked: StringProperty(name="Last workbenches", default="{}", options={"HIDDEN"},
                           description="The workbench last picked on each branch (JSON)")

    def draw(self, context):
        self.layout.prop(self, "server")
        self.layout.prop(self, "cache_dir")
        self.layout.prop(self, "max_rows")


classes = (UNITY_PIPELINE_MT_branches, UNITY_PIPELINE_MT_workbenches, UNITY_PIPELINE_PT_main, UNITY_PIPELINE_PT_scene,
           UNITY_PIPELINE_PT_tree, UNITY_PIPELINE_PT_asset, UNITY_PIPELINE_Preferences)


def register_props():
    bpy.types.WindowManager.unity_pipeline_filter = StringProperty(name="Filter", description="Show files whose name contains this", options={"TEXTEDIT_UPDATE"})
    bpy.types.WindowManager.unity_pipeline_openable = BoolProperty(name="Only files Blender opens", default=True,
                                                                   description="Hide files Blender can't open (folders stay)")


def unregister_props():
    del bpy.types.WindowManager.unity_pipeline_filter
    del bpy.types.WindowManager.unity_pipeline_openable
