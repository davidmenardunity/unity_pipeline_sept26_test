// The embedded Scene Preview player (WebGL), one per <iframe>, and the content archives it shows.
// The server has the pipeline build an asset's archive (.ca) for WebGL and answers the URL to download
// it from; the player (player.html) downloads and shows it.
"use strict";

const PREVIEWABLE = new Set(["prefab", "fbx", "obj", "mat"]);   // PreviewLoader.CanPresent
// Scenes (.unity) play in their own player (SceneViewer_WebGL), from archives the scene importer builds.
const SCENE_IMPORTER = "Unity.Pipeline.SceneViewer.Editor.PreviewSceneImporter";
const isScene = (path) => ext(path) === "unity";
const canPreview = (path) => PREVIEWABLE.has(ext(path)) || isScene(path);
const canPreviewObject = (path) => PREVIEWABLE.has(ext(path));   // what the Scene Preview player shows

class Viewer {
  static all = [];

  constructor(iframe, label, page = "player.html?embedded=1") {
    this.iframe = iframe;
    this.label = label;
    this.page = page;
    this.outbox = [];
    this.ready = false;
    this.pending = null;
    this.startOp = null;
    this.shown = null;    // { key, url, name }
    Viewer.all.push(this);
  }

  // Load the player on first use: it's a big download, and it takes a few seconds to start.
  ensure(example) {
    if (this.iframe.getAttribute("src")) return;
    this.ready = false;
    this.startOp = Ops.start({ title: `Start the player${this.label ? ` (${this.label})` : ""}`, kind: "player", example,
      steps: [{ id: "load", label: "Download and start the Scene Preview player" }] });
    this.startOp.step("load", "active", "0%");
    this.iframe.src = this.page;   // the app draws the UI; the player draws none
  }

  load(url, name, key, example) {
    this.ensure(example);
    this.shown = { url, name, key };
    this.scene = null;
    if (!this.ready) { this.pending = url; return; }
    this.iframe.contentWindow.postMessage({ type: "load", url }, location.origin);
  }

  clear() { this.shown = null; }

  // A call to the player's PreviewAnnotations (comments on the preview); resolves with its value.
  annotate(method, arg = {}) {
    this.calls ??= new Map();
    const id = `a${Viewer.nextCall = (Viewer.nextCall ?? 0) + 1}`;
    return new Promise((resolve, reject) => {
      this.calls.set(id, { resolve, reject });
      this.post({ type: "annotations-call", id, method, arg });
    });
  }

  // Any message for the player page, sent once the player is ready.
  post(message) {
    if (!this.ready) { this.outbox.push(message); return; }
    this.iframe.contentWindow.postMessage(message, location.origin);
  }

  static {
    addEventListener("message", (e) => {
      if (e.origin !== location.origin) return;
      const v = Viewer.all.find((x) => x.iframe.contentWindow === e.source);
      if (!v) return;
      const m = e.data ?? {};
      if (m.type === "player-progress") {
        v.progress = m.progress;
        v.startOp?.step("load", "active", `${Math.round(m.progress * 100)}%`);
      }
      else if (m.type === "annotations") {
        const d = m.data ?? {};
        if (d.event === "result") {
          const c = v.calls?.get(d.id);
          v.calls?.delete(d.id);
          if (c) d.ok ? c.resolve(d.value) : c.reject(new Error(d.error));
        } else App.emit("annotations", { viewer: v, ...d });
      }
      else if (m.type === "scene-viewer") {
        v.scene = { ...(v.scene ?? {}), ...m.data, at: Date.now() };
        if (["captured", "released", "walking", "flying"].includes(m.data.state)) {
          v.scene.state = v.scene.loadedState ?? "loaded";
          if (m.data.state === "captured" || m.data.state === "released") v.scene.captured = m.data.state === "captured";
          else v.scene.mode = m.data.state;
        } else if (m.data.state === "loaded") v.scene.loadedState = "loaded";
        App.emit("viewer", v);
      }
      else if (m.type === "player-error") { v.startOp?.fail(m.message); v.failed = m.message; App.emit("viewer", v); }
      else if (m.type === "player-ready") {
        v.ready = true;
        v.startOp?.step("load", "done").done("Player ready");
        if (v.pending) { const url = v.pending; v.pending = null; v.iframe.contentWindow.postMessage({ type: "load", url }, location.origin); }
        for (const m of v.outbox.splice(0)) v.iframe.contentWindow.postMessage(m, location.origin);
        App.emit("viewer", v);
      }
    });
  }
}

// The player is downloading or starting: its progress, for the app's overlay (the player shows none).
function playerStartingEl(viewer) {
  if (!viewer || viewer.ready || viewer.failed || !viewer.iframe.getAttribute("src")) return null;
  const pct = Math.round((viewer.progress ?? 0) * 100);
  return h("div", { style: "display:flex;gap:8px;align-items:center" }, h("span", { class: "spin" }),
    h("b", {}, "Starting the player"), h("span", { style: "opacity:.7" }, pct ? `${pct}%` : "downloading"),
    h("span", { class: "grow" }),
    viewer.startOp ? h("span", { class: "num", "data-op": viewer.startOp.running ? viewer.startOp.id : null }, secs(viewer.startOp.elapsed)) : null);
}

// Archives built this session, per workbench, revision and asset: the URL to load them from.
const Archives = {
  built: new Map(),
  key: (wb, rev, guid) => `${wb}|${rev}|${guid}`,
  get(wb, rev, guid) { return this.built.get(this.key(wb, rev, guid)) ?? null; },

  /**
   * Have the pipeline build the asset's WebGL content archive, narrated on `op`'s "archive" step.
   * The first one for an asset (or in a new environment) can take minutes: the server re-sends
   * the request while the pipeline is still producing it.
   */
  async build(wb, rev, guid, op, { scene = false } = {}) {
    const k = this.key(wb, rev, guid);
    if (this.built.has(k)) { op.step("archive", "done", "built earlier in this session"); return this.built.get(k); }
    op.step("archive", "active", scene ? "a whole scene: the first build can take several minutes" : "the first one for an asset can take a few minutes");
    const r = await postJson(`/api/workbenches/${wb}/revisions/${enc(rev)}/player-archive`,
      { guid, platform: "WebGL", ...(scene ? { importer: SCENE_IMPORTER } : {}) }, op);
    for (const s of r.steps ?? []) op.note(s);
    op.step("archive", "done", r.artifact);
    this.built.set(k, r);
    return r;
  },
};
