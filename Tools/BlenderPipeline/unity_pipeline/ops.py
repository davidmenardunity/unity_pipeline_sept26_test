"""Operators: connect, pick a branch and workbench, browse the tree, open an asset, push it back."""

import json
import os
import tempfile
import time
import webbrowser

import bpy
from bpy.props import BoolProperty, StringProperty

from . import client, formats
from .state import S, SESSION_KEY, api, prefs, redraw, run, say

# ── connecting and picking a workbench ──────────────────────────────────────


def connect():
    def work(job):
        c = api()
        job.step = "reading the configuration"
        cfg = c.config()
        if "UNITY_JWT" in cfg.get("missing", []) or (cfg.get("token") or {}).get("isExpired"):
            raise client.PipelineError("Pipeline Explorer has no usable bearer token. Paste one in its page (Bearer token…), then connect again.")
        if cfg.get("missing"):
            raise client.PipelineError("Pick an org and project in Pipeline Explorer first, then connect again.")
        job.step = "listing branches and workbenches"
        return cfg, c.branches(), c.workbenches()

    def done(result):
        cfg, (branches, heads), workbenches = result
        S.config, S.branches, S.heads, S.workbenches = cfg, branches, heads, workbenches
        branch = S.branch if S.branch in branches else cfg.get("branch")
        say("OK", f"Connected to project {cfg.get('projectId', '')[:8]} in org {cfg.get('organizationId')}")
        pick_branch(branch)

    run("Connect to Pipeline", work, done)


def remembered():
    try:
        return json.loads(prefs().picked or "{}")
    except ValueError:
        return {}


def pick_branch(branch):
    S.branch = branch
    wbs = S.workbenches_on(branch)
    # The one picked last time on this branch, else an up-to-date one (they come first).
    last = remembered().get(branch)
    keep = next((w for w in wbs if w["workbenchId"] in (S.wb, last)), None)
    pick_workbench((keep or (wbs[0] if wbs else {})).get("workbenchId"))


def pick_workbench(wb):
    S.wb, S.rev = wb, None
    S.tree.clear()
    S.expanded = {"Assets"}
    S.loading.clear()
    S.selected = S.selected_info = None
    if not wb:
        S.wb_status = f"No workbench on {S.branch}. Create one in Pipeline Explorer."
        redraw()
        return
    S.wb_status = "checking…"

    def work(job):
        c = api()
        deadline = time.time() + 20 * 60
        while True:
            rev, where = c.readiness(wb)
            if rev:
                return rev
            job.step = where   # e.g. settling, validation running
            if time.time() > deadline:
                raise client.PipelineError(f"The workbench isn't ready after 20 minutes ({where}).")
            time.sleep(5)

    def done(rev):
        if S.wb != wb:
            return
        S.rev = rev
        S.wb_status = f"ready at revision {rev}"
        S.tree[""] = [(root, True) for root in ("Assets", "Packages", "ProjectSettings")]
        load_folder("Assets")

    def failed(e):
        if S.wb == wb:
            S.wb_status = str(e)
        say("ERROR", f"Workbench {wb[:8]}: {e}")

    run(f"Open workbench {wb[:8]}", work, done, failed)


def load_folder(folder):
    if folder in S.loading:
        return
    S.loading.add(folder)
    wb, rev = S.wb, S.rev

    def done(entries):
        S.loading.discard(folder)
        if (S.wb, S.rev) == (wb, rev):
            S.tree[folder] = entries

    def failed(e):
        S.loading.discard(folder)
        say("ERROR", f"Couldn't list {folder}: {e}")

    run(f"List {folder}", lambda job: api().tree(wb, rev, folder), done, failed)


def refresh_tree():
    """Reload the open folders (after a push, the tree is read at the new revision)."""
    S.tree = {"": [(r, True) for r in ("Assets", "Packages", "ProjectSettings")]}
    for folder in sorted(S.expanded, key=len):
        load_folder(folder)


class UNITY_PIPELINE_OT_connect(bpy.types.Operator):
    bl_idname = "unity_pipeline.connect"
    bl_label = "Connect"
    bl_description = "Connect to Pipeline Explorer (which holds your token) and list the branches and workbenches"

    def execute(self, context):
        connect()
        return {"FINISHED"}


class UNITY_PIPELINE_OT_pick_branch(bpy.types.Operator):
    bl_idname = "unity_pipeline.pick_branch"
    bl_label = "Branch"
    bl_description = "Work on this branch (through its newest workbench)"
    branch: StringProperty()

    def execute(self, context):
        pick_branch(self.branch)
        return {"FINISHED"}


class UNITY_PIPELINE_OT_pick_workbench(bpy.types.Operator):
    bl_idname = "unity_pipeline.pick_workbench"
    bl_label = "Workbench"
    bl_description = "Read the project through this workbench"
    workbench: StringProperty()

    def execute(self, context):
        picks = remembered()
        picks[S.branch] = self.workbench
        prefs().picked = json.dumps(picks)
        pick_workbench(self.workbench)
        return {"FINISHED"}


class UNITY_PIPELINE_OT_open_explorer(bpy.types.Operator):
    bl_idname = "unity_pipeline.open_explorer"
    bl_label = "Open Pipeline Explorer"
    bl_description = "Open Pipeline Explorer in your browser (to paste a token, create a workbench, or see every call)"

    def execute(self, context):
        webbrowser.open(prefs().server)
        return {"FINISHED"}


# ── the tree ────────────────────────────────────────────────────────────────


class UNITY_PIPELINE_OT_toggle(bpy.types.Operator):
    bl_idname = "unity_pipeline.toggle"
    bl_label = "Open or close the folder"
    bl_description = "Open or close this folder"
    path: StringProperty()

    def execute(self, context):
        if self.path in S.expanded:
            S.expanded.discard(self.path)
        else:
            S.expanded.add(self.path)
            if self.path not in S.tree:
                load_folder(self.path)
        redraw()
        return {"FINISHED"}


class UNITY_PIPELINE_OT_select(bpy.types.Operator):
    bl_idname = "unity_pipeline.select"
    bl_label = "Select"
    bl_description = "Select this file. Double-click, or press Open in Blender below, to open it"
    path: StringProperty()

    def invoke(self, context, event):
        # A second click on the selected file opens it.
        if S.selected == self.path and formats.can_open(self.path):
            bpy.ops.unity_pipeline.open("INVOKE_DEFAULT", path=self.path)
            return {"FINISHED"}
        return self.execute(context)

    def execute(self, context):
        path, wb, rev = self.path, S.wb, S.rev
        S.selected, S.selected_info = path, None

        def done(info):
            if S.selected == path:
                S.selected_info = info

        run(f"Read {os.path.basename(path)}", lambda job: api().asset(wb, rev, path), done,
            lambda e: say("ERROR", f"Couldn't read {os.path.basename(path)}: {e}"))
        return {"FINISHED"}


# ── opening an asset ────────────────────────────────────────────────────────


def ui_context():
    """A window with a 3D view, for running import operators from a timer."""
    for win in bpy.context.window_manager.windows:
        for area in win.screen.areas:
            if area.type == "VIEW_3D":
                region = next((r for r in area.regions if r.type == "WINDOW"), None)
                return win, dict(window=win, area=area, region=region)
    win = bpy.context.window_manager.windows[0]
    return win, dict(window=win)


def session(scene):
    s = scene.get(SESSION_KEY)
    return dict(s) if s else None


def find_scene(path, wb):
    return next((sc for sc in bpy.data.scenes if (s := session(sc)) and s["path"] == path and s["wb"] == wb), None)


def cache_root():
    return prefs().cache_dir or os.path.join(tempfile.gettempdir(), "unity_pipeline_blender")


def open_asset(path, replace=None):
    wb, rev, branch = S.wb, S.rev, S.branch
    name = os.path.basename(path)
    ext = formats.ext_of(path)

    def work(job):
        c = api()
        job.step = "finding the asset"
        info = c.asset(wb, rev, path)
        size = (info.get("info") or {}).get("size")
        job.step = f"downloading{f' {size / 1048576:.1f} MB' if size else ''}"
        data = c.download(wb, rev, path)
        file = client.cache_path(cache_root(), wb, rev, path)
        os.makedirs(os.path.dirname(file), exist_ok=True)
        with open(file, "wb") as f:
            f.write(data)
        return info, file

    def done(result):
        info, file = result
        win, override = ui_context()
        title = f"{name} · {branch}"
        if ext == ".blend":
            scene = formats.open_blend(file, title)
            win.scene = scene
        else:
            scene = bpy.data.scenes.new(title)
            win.scene = scene
            with bpy.context.temp_override(**override):
                formats.import_file(file, ext)
        scene[SESSION_KEY] = {
            "path": path, "wb": wb, "rev": str(rev), "branch": branch or "", "ext": ext,
            "guid": info.get("guid") or "", "hash": (info.get("info") or {}).get("fileHash") or "", "file": file,
        }
        if replace and replace in bpy.data.scenes and bpy.data.scenes[replace] != scene:
            bpy.data.scenes.remove(bpy.data.scenes[replace])
        count = len(scene.objects)
        say("OK", f"Opened {name} from {branch} (revision {rev}) in scene “{scene.name}”: {count} object{'s' if count != 1 else ''}")

    run(f"Open {name}", work, done, lambda e: say("ERROR", f"Couldn't open {name}: {e}"))


class UNITY_PIPELINE_OT_open(bpy.types.Operator):
    bl_idname = "unity_pipeline.open"
    bl_label = "Open in Blender"
    bl_description = "Download the asset at the workbench's revision and open it in a scene of its own"
    path: StringProperty()

    @classmethod
    def poll(cls, context):
        return S.wb and S.rev

    def execute(self, context):
        path = self.path or S.selected
        if not path or not formats.can_open(path):
            self.report({"ERROR"}, "Pick a file Blender can open (" + ", ".join(sorted(formats.LABELS)) + ")")
            return {"CANCELLED"}
        existing = find_scene(path, S.wb)
        if existing and session(existing)["rev"] == str(S.rev):
            context.window.scene = existing
            say("INFO", f"{os.path.basename(path)} is already open in scene “{existing.name}”. Use Reopen to discard your changes.")
            return {"FINISHED"}
        open_asset(path)
        return {"FINISHED"}


class UNITY_PIPELINE_OT_reopen(bpy.types.Operator):
    bl_idname = "unity_pipeline.reopen"
    bl_label = "Reopen"
    bl_description = "Discard the changes in this scene and open the asset again at the workbench's current revision"

    @classmethod
    def poll(cls, context):
        return session(context.scene) and S.wb == session(context.scene)["wb"] and S.rev

    def invoke(self, context, event):
        return context.window_manager.invoke_confirm(self, event, title="Reopen from Pipeline?",
                                                     message="Your changes in this scene are discarded.", confirm_text="Reopen")

    def execute(self, context):
        open_asset(session(context.scene)["path"], replace=context.scene.name)
        return {"FINISHED"}


# ── pushing it back ─────────────────────────────────────────────────────────


class Conflict(client.PipelineError):
    pass


class UNITY_PIPELINE_OT_push(bpy.types.Operator):
    bl_idname = "unity_pipeline.push"
    bl_label = "Push to Pipeline"
    bl_description = ("Export this scene in the asset's format and overwrite the asset in the workbench it came from, "
                      "as a new workbench revision (git isn't changed). Its .meta (and GUID) stay, so references in Unity keep working")

    message: StringProperty(name="Message", description="Describes the new revision")
    overwrite: BoolProperty(name="Overwrite newer changes",
                            description="Push even if the asset changed on the workbench after you opened it")

    @classmethod
    def poll(cls, context):
        s = session(context.scene)
        if not s:
            cls.poll_message_set("This scene wasn't opened from Pipeline")
            return False
        if any(j.label.startswith("Push") for j in S.jobs.values()):
            cls.poll_message_set("A push is already running")
            return False
        return True

    def invoke(self, context, event):
        s = session(context.scene)
        self.message = f"Blender: update {os.path.basename(s['path'])}"
        self.overwrite = False
        return context.window_manager.invoke_props_dialog(self, width=460, title="Push to Pipeline", confirm_text="Push")

    def draw(self, context):
        s = session(context.scene)
        col = self.layout.column()
        col.label(text=s["path"], icon="FILE_3D")
        col.label(text=f"Overwrites it in workbench {s['wb'][:8]} (on {s['branch']}) as a new revision.")
        col.label(text=f"Exports {len(context.scene.objects)} objects from this scene as {formats.LABELS[s['ext']]}.")
        col.separator()
        col.prop(self, "message")
        col.prop(self, "overwrite")

    def execute(self, context):
        scene = context.scene
        s = session(scene)
        name = os.path.basename(s["path"])
        out_dir = os.path.join(cache_root(), "push", str(int(time.time() * 1000)))
        os.makedirs(out_dir, exist_ok=True)
        out = os.path.join(out_dir, name)
        try:
            formats.export_scene(scene, out, s["ext"])
            with open(out, "rb") as f:
                data = f.read()
        except Exception as e:
            self.report({"ERROR"}, f"Couldn't export {name}: {e}")
            return {"CANCELLED"}
        message, overwrite = self.message.strip() or None, self.overwrite

        def work(job):
            c = api()
            wb, path = s["wb"], s["path"]
            job.step = "checking the workbench"
            current, where = c.readiness(wb)
            if not current:
                raise client.PipelineError(f"The workbench isn't ready to take changes ({where}). Try again when it is.")
            if current != s["rev"] and not overwrite:
                try:
                    now = c.asset(wb, current, path)
                except client.PipelineError:
                    now = None
                if now and (now.get("info") or {}).get("fileHash") not in (None, s["hash"]):
                    raise Conflict(f"{name} changed on the workbench after you opened it (you have revision {s['rev']}, "
                                   f"it's at {current}). Reopen it, or push again with “Overwrite newer changes”.")
            job.step = f"uploading {len(data) / 1048576:.1f} MB"
            revision = c.upload(wb, current, path, data, s["branch"], message)
            deadline = time.time() + 20 * 60
            while True:
                job.step = f"revision {revision} is validating (about a minute)"
                settled, where = c.readiness(wb)
                if settled and client.newer_or_same(settled, revision):
                    break
                if time.time() > deadline:
                    raise client.PipelineError(f"Revision {revision} was committed but hasn't validated after 20 minutes ({where}).")
                time.sleep(4)
            job.step = "reading it back"
            info = c.asset(wb, settled, path)
            return settled, info

        def done(result):
            settled, info = result
            sc = find_scene(s["path"], s["wb"])
            if sc:
                sess = session(sc)
                sess.update(rev=str(settled), hash=(info.get("info") or {}).get("fileHash") or "")
                sc[SESSION_KEY] = sess
            if S.wb == s["wb"]:
                S.rev = settled
                S.wb_status = f"ready at revision {settled}"
                refresh_tree()
            say("OK", f"Pushed {name} to workbench {s['wb'][:8]} ({s['branch']}): revision {settled}")

        run(f"Push {name}", work, done, lambda e: say("ERROR", f"Push {name}: {e}"))
        self.report({"INFO"}, f"Pushing {name} to workbench {s['wb'][:8]} ({s['branch']})…")
        return {"FINISHED"}


classes = (
    UNITY_PIPELINE_OT_connect, UNITY_PIPELINE_OT_pick_branch, UNITY_PIPELINE_OT_pick_workbench,
    UNITY_PIPELINE_OT_open_explorer, UNITY_PIPELINE_OT_toggle, UNITY_PIPELINE_OT_select,
    UNITY_PIPELINE_OT_open, UNITY_PIPELINE_OT_reopen, UNITY_PIPELINE_OT_push,
)
