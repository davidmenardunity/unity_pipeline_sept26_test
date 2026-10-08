"""What the add-on knows (connection, branch, workbench, the project tree), and background work.

Network calls run on worker threads so Blender never freezes; their results come back to the main
thread through a timer, which is where Blender data may be touched. The sidebar always shows what
is running (and for how long) or the last result.
"""

import queue
import threading
import time

import bpy

from . import client

SESSION_KEY = "unity_pipeline"   # on a scene: which asset it holds, from where


class State:
    def __init__(self):
        self.reset()

    def reset(self):
        self.config = None
        self.branches = []
        self.heads = {}           # branch → latest commit
        self.workbenches = []
        self.branch = None
        self.wb = None
        self.rev = None
        self.wb_status = ""
        self.tree = {}            # folder → [(path, is_folder)]
        self.expanded = set()
        self.loading = set()
        self.selected = None      # a file path
        self.selected_info = None
        self.jobs = {}            # id → Job
        self.last = None          # (level, text, at) of the last finished job

    @property
    def connected(self):
        return self.config is not None

    def behind(self, w):
        """Was this workbench made from an older commit than its branch's latest? (It won't have the newer files.)"""
        head = self.heads.get(w.get("gitBranch"))
        return bool(head and w.get("upstreamRevision") and head != w["upstreamRevision"])

    def workbenches_on(self, branch):
        """The workbenches made from this git branch (or maybe made from it, when several branches share
        the commit), up to date ones first, then newest first."""
        on = [w for w in self.workbenches
              if w.get("gitBranch") == branch or (not w.get("gitBranch") and branch in (w.get("gitBranchCandidates") or []))]
        on.sort(key=lambda w: w.get("createdAt") or "", reverse=True)
        return sorted(on, key=self.behind)

    def workbenches_unknown(self):
        """Workbenches whose git branch can't be told (made elsewhere, from an older commit)."""
        return [w for w in self.workbenches if not w.get("gitBranch") and not w.get("gitBranchCandidates")]


class Job:
    def __init__(self, label, done, failed):
        self.label = label
        self.step = ""            # what it's doing now; set from the worker thread
        self.started = time.time()
        self.done = done
        self.failed = failed


S = State()
_results = queue.Queue()
_seq = 0


def prefs():
    return bpy.context.preferences.addons[__package__].preferences


def api():
    return client.Client(prefs().server)


def redraw():
    wm = bpy.context.window_manager
    for win in (wm.windows if wm else []):
        for area in win.screen.areas:
            if area.type == "VIEW_3D":
                area.tag_redraw()


def say(level, text):
    """The status line: level is INFO, OK or ERROR."""
    S.last = (level, text, time.time())
    redraw()


def run(label, work, done=None, failed=None):
    """Run work(job) on a thread, then done(result) or failed(error) on the main thread.

    work may set job.step to say where it is; the sidebar shows it with the elapsed time.
    """
    global _seq
    _seq += 1
    key = _seq
    job = S.jobs[key] = Job(label, done, failed)

    def thread():
        try:
            _results.put((key, True, work(job)))
        except Exception as e:   # shown in the sidebar, never raised into Blender
            _results.put((key, False, e))

    threading.Thread(target=thread, daemon=True).start()
    if not bpy.app.timers.is_registered(_pump):
        bpy.app.timers.register(_pump, first_interval=0.1)
    redraw()
    return job


def _pump():
    while True:
        try:
            key, ok, value = _results.get_nowait()
        except queue.Empty:
            break
        job = S.jobs.pop(key, None)
        if job is None:
            continue
        try:
            if ok:
                if job.done:
                    job.done(value)
            elif job.failed:
                job.failed(value)
            else:
                say("ERROR", f"{job.label}: {value}")
        except Exception as e:
            say("ERROR", f"{job.label}: {e}")
    redraw()   # also ticks the elapsed time while something runs
    return 0.25 if S.jobs or not _results.empty() else None


def busy_text():
    if not S.jobs:
        return None
    job = max(S.jobs.values(), key=lambda j: j.started)
    more = f" (+{len(S.jobs) - 1} more)" if len(S.jobs) > 1 else ""
    step = f": {job.step}" if job.step else ""
    return f"{job.label}{step} · {time.time() - job.started:.0f} s{more}"


def stop_all():
    """On unregister: forget pending work (threads finish on their own; their results are dropped)."""
    S.jobs.clear()
    if bpy.app.timers.is_registered(_pump):
        bpy.app.timers.unregister(_pump)
