// The embedded Scene Preview player (WebGL), one per <iframe>, and the content archives it shows.
// The server has the pipeline build an asset's archive (.ca) for WebGL and answers the URL to download
// it from; the player (player.html) downloads and shows it.
"use strict";

const PREVIEWABLE = new Set(["prefab", "fbx", "obj", "mat"]);   // PreviewLoader.CanPresent
const canPreview = (path) => PREVIEWABLE.has(ext(path));

class Viewer {
  static all = [];

  constructor(iframe, label) {
    this.iframe = iframe;
    this.label = label;
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
    this.iframe.src = "player.html";
  }

  load(url, name, key, example) {
    this.ensure(example);
    this.shown = { url, name, key };
    if (!this.ready) { this.pending = url; return; }
    this.iframe.contentWindow.postMessage({ type: "load", url }, location.origin);
  }

  clear() { this.shown = null; }

  static {
    addEventListener("message", (e) => {
      if (e.origin !== location.origin) return;
      const v = Viewer.all.find((x) => x.iframe.contentWindow === e.source);
      if (!v) return;
      const m = e.data ?? {};
      if (m.type === "player-progress") v.startOp?.step("load", "active", `${Math.round(m.progress * 100)}%`);
      else if (m.type === "player-error") { v.startOp?.fail(m.message); v.failed = m.message; App.emit("viewer", v); }
      else if (m.type === "player-ready") {
        v.ready = true;
        v.startOp?.step("load", "done").done("Player ready");
        if (v.pending) { const url = v.pending; v.pending = null; v.iframe.contentWindow.postMessage({ type: "load", url }, location.origin); }
        App.emit("viewer", v);
      }
    });
  }
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
  async build(wb, rev, guid, op) {
    const k = this.key(wb, rev, guid);
    if (this.built.has(k)) { op.step("archive", "done", "built earlier in this session"); return this.built.get(k); }
    op.step("archive", "active", "the first one for an asset can take a few minutes");
    const r = await postJson(`/api/workbenches/${wb}/revisions/${enc(rev)}/player-archive`, { guid, platform: "WebGL" }, op);
    for (const s of r.steps ?? []) op.note(s);
    op.step("archive", "done", r.artifact);
    this.built.set(k, r);
    return r;
  },
};
