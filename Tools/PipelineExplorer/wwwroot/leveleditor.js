// Level editor: open a .unity scene from the workbench, rebuild it in the level editor's WebGL runtime
// (each asset from its Pipeline-built content archive), drag prefabs in from the file tree, move things
// with the runtime's widget or the transform fields, and save the scene back as a new workbench revision.
// The scene model (what's placed where, and the edits that save it) is in levelscene.js.
"use strict";

(() => {
  const Y = UnityYaml, L = LevelScene;
  const LE = {
    path: null, wb: null, rev: null, parsed: null, scene: null,
    items: [],                 // placeables, plus added ones (kind "new")
    skipped: [],
    prefabs: new Map(),        // guid → readPrefab() (or null)
    assets: new Map(),         // asset guid → { path, state: "building"|"ready"|"failed", error }
    selected: null,
    filter: "",
    message: "",
    openOp: null, saveOp: null,
  };
  let viewer = null;
  const dirtyCount = () => LE.items.filter((i) => i.dirty || i.kind === "new").length;

  const tree = fileTree($("leTree"), {
    show: (e) => e.isFolder || /\.(unity|prefab)$/i.test(e.path),
    draggable: (path) => /\.prefab$/i.test(path),
    onFile: (path) => (/\.unity$/i.test(path) ? openScene(path) : banner(`Drag ${fileName(path)} into the view to add it to the scene.`, "info")),
  });

  App.register({ id: "level", name: "Level editor", desc: "Rebuild a scene in a web runtime, add and move prefabs, save it back to the workbench." });

  App.on("revision", async ({ wb, revision }) => {
    $("leRev").textContent = revision ? `rev ${revision}` : "";
    await tree.open(wb, revision);
    if (!LE.path || !revision || wb !== LE.wb || revision === LE.rev) return;
    if (LE.saveOp?.running) return;          // our own save: it re-reads the scene itself
    if (dirtyCount()) { renderPanel(); return; }   // keep the edits; the save bar says the base moved
    openScene(LE.path, { force: true });
  });
  App.on("ops", () => { if (LE.path) { renderOverlay(); renderBar(); } });

  // ── the runtime ──────────────────────────────────────────────────────

  function runtime() {
    viewer ??= new Viewer($("lePlayer"), "level editor", "levelplayer.html");
    viewer.ensure("level");
    return viewer;
  }
  // A command for the runtime (SendMessage("LevelEditor", method, json)), sent once it's ready.
  const rt = (method, arg = {}) => runtime().post({ type: "le", method, arg: JSON.stringify(arg) });
  // Each object's id in the runtime: its fileID in the scene, or a temporary id for one added since the last save.
  const rid = (item) => item.rid ?? item.id;
  const placeArgs = (item) => ({
    id: rid(item), key: item.assetGuid ?? "", name: item.name, primitive: item.primitive ?? "",
    pos: item.world.p, rot: item.world.q, scale: item.world.s,
  });

  addEventListener("message", (e) => {
    if (e.origin !== location.origin || !viewer || e.source !== viewer.iframe.contentWindow) return;
    const m = e.data ?? {};
    if (m.type === "le-drop") drop(m);
    else if (m.type === "level-editor") fromRuntime(m.data ?? {});
    else if (m.type === "player-ready" || m.type === "player-progress" || m.type === "player-error") renderOverlay();
  });

  function fromRuntime(r) {
    if (r.type === "archive") {
      const a = LE.assets.get(r.key);
      if (a) Object.assign(a, r.ok ? { state: "ready", error: null } : { state: "failed", error: r.error });
      renderList();
    } else if (r.type === "selected") {
      LE.selected = LE.items.find((i) => rid(i) === r.id) ?? null;
      renderPanel();
    } else if (r.type === "moving" || r.type === "moved" || r.type === "placed") {
      const item = LE.items.find((i) => rid(i) === r.id);
      if (!item) return;
      item.world = { p: r.pos, q: r.rot, s: r.scale };
      if (item.kind !== "new") item.dirty = true;
      if (r.type === "placed") LE.selected = item;
      renderPanel();
    } else if (r.type === "delete") {
      const item = LE.items.find((i) => rid(i) === r.id);
      if (item?.kind === "new") remove(item);
      else if (item) banner(`${item.name} is part of the saved scene. Removing saved objects isn't supported yet; only objects you added can be removed.`, "info");
    }
  }

  // ── opening a scene ──────────────────────────────────────────────────

  async function openScene(path, { force = false } = {}) {
    if (!force && path === LE.path && LE.rev === App.revision) return;
    if (dirtyCount() && path !== LE.path) {
      const ok = await ask({ title: `Discard ${dirtyCount()} unsaved change${dirtyCount() === 1 ? "" : "s"}?`,
        message: `Your changes to ${fileName(LE.path)} haven't been saved to the workbench.`, ok: "Discard", danger: true });
      if (!ok) { tree.select(LE.path); return; }
    }
    LE.openOp?.stop(`You opened ${fileName(path)}.`);
    const wb = App.wb, rev = App.revision;
    Object.assign(LE, { path, wb, rev, parsed: null, scene: null, items: [], skipped: [], selected: null, message: "" });
    tree.select(path);
    rt("Clear");
    const op = LE.openOp = Ops.start({ title: `Open ${fileName(path)}`, kind: "level-open", asset: path,
      steps: [{ id: "get", label: "Download the scene" }, { id: "read", label: "Read its objects" },
        { id: "prefabs", label: "Read the prefabs it uses" }, { id: "place", label: "Place them" }, { id: "archives", label: "Pipeline builds each asset's archive" }] });
    renderPanel();
    renderOverlay();
    try {
      op.step("get", "active");
      const text = await (await call("GET", `${App.revUrl(wb, rev)}/file?path=${enc(path)}`, null, op)).text();
      if (LE.openOp !== op) return;
      op.step("get", "done", fmtBytes(text.length));
      op.step("read", "active");
      if (!text.startsWith("%YAML")) throw new Error(`${fileName(path)} isn't saved as text (YAML), so it can't be read here.`);
      LE.parsed = Y.parse(text);
      LE.scene = L.readScene(LE.parsed);
      const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
      op.step("read", "done", `${plural(LE.scene.instances.length, "prefab instance", "prefab instances")}, ${plural(LE.scene.objects.length, "object", "objects")} with a mesh`);

      // Each prefab's root (its default transform, and the ids saving needs).
      const guids = [...new Set(LE.scene.instances.map((i) => i.sourceGuid))].filter((g) => !LE.prefabs.has(g));
      let done = 0;
      op.step("prefabs", "active", `0/${guids.length}`);
      await pool(guids, 6, async (g) => {
        LE.prefabs.set(g, await readPrefabAt(wb, rev, g, op));
        op.step("prefabs", "active", `${++done}/${guids.length}`);
      });
      if (LE.openOp !== op) return;
      op.step("prefabs", "done", `${guids.length} read`);

      op.step("place", "active");
      const { items, skipped } = L.placeables(LE.scene, LE.prefabs);
      LE.items = items;
      LE.skipped = skipped;
      for (const item of items) rt("Place", placeArgs(item));
      rt("Frame");
      op.step("place", "done", `${items.length} placed`);
      renderPanel();

      // Archives: one per distinct asset; placeholders show until each is ready.
      const assets = [...new Set(items.map((i) => i.assetGuid).filter(Boolean))];
      await buildArchives(assets, op, wb, rev);
      if (LE.openOp !== op) return;
      const failed = assets.filter((g) => LE.assets.get(g)?.state === "failed").length;
      op.done(`${items.length} objects from ${assets.length} assets${failed ? `, ${failed} couldn't be built` : ""}`);
    } catch (e) {
      if (LE.openOp === op) op.fail(e);
    }
    renderPanel();
  }

  async function pool(list, n, fn) {
    const queue = list.slice();
    await Promise.all(Array.from({ length: Math.min(n, queue.length) }, async () => { while (queue.length) await fn(queue.shift()); }));
  }

  async function readPrefabAt(wb, rev, guid, op) {
    try {
      const a = await getJson(`${App.revUrl(wb, rev)}/assets/${guid}`, op);
      const path = (a.path ?? "").replace(/^\/+/, "");
      LE.assets.set(guid, { ...(LE.assets.get(guid) ?? {}), path });
      if (!path.endsWith(".prefab")) return null;
      const text = await (await call("GET", `${App.revUrl(wb, rev)}/file?path=${enc(path)}`, null, op)).text();
      return L.readPrefab(Y.parse(text));
    } catch { return null; }
  }

  async function buildArchives(guids, op, wb, rev) {
    let done = 0;
    const total = guids.length;
    op.step("archives", "active", `0/${total} (the first build of an asset can take a few minutes)`);
    await pool(guids, 3, async (g) => {
      const a = LE.assets.get(g) ?? {};
      LE.assets.set(g, { ...a, state: "building", error: null });
      renderList();
      try {
        const r = await postJson(`/api/workbenches/${wb}/revisions/${enc(rev)}/player-archive`, { guid: g, platform: "WebGL" }, op);
        Archives.built.set(Archives.key(wb, rev, g), r);
        if (LE.wb === wb && LE.rev === rev) rt("LoadArchive", { key: g, url: r.url });
        LE.assets.get(g).state = "loading";
      } catch (e) {
        Object.assign(LE.assets.get(g), { state: "failed", error: describeError(e) });
      }
      op.step("archives", "active", `${++done}/${total}`);
      renderList();
    });
    op.step("archives", "done", `${total} built`);
  }

  // ── adding prefabs (dropped from the file tree) ──────────────────────

  async function drop({ path, u, v }) {
    if (!LE.scene) { banner("Open a .unity scene first, then drag prefabs into it.", "info"); return; }
    if (!/\.prefab$/i.test(path)) { banner(`Only prefabs can be added (${fileName(path)} isn't one).`, "info"); return; }
    const wb = LE.wb, rev = LE.rev;
    const op = Ops.start({ title: `Add ${fileName(path)}`, kind: "level-add", asset: path,
      steps: [{ id: "read", label: "Read the prefab" }, { id: "archives", label: "Pipeline builds its archive" }] });
    try {
      op.step("read", "active");
      const r = await getJson(`${App.revUrl(wb, rev)}/asset?path=${enc(path)}`, op);
      if (!r.guid) throw new Error(`${fileName(path)} has no GUID at revision ${rev}.`);
      if (!LE.prefabs.has(r.guid)) LE.prefabs.set(r.guid, await readPrefabAt(wb, rev, r.guid, op));
      const info = LE.prefabs.get(r.guid);
      if (!info) throw new Error(`${fileName(path)} has no plain root (a prefab variant?), so it can't be added here.`);
      op.step("read", "done");
      const item = { id: `new-${Date.now().toString(36)}`, kind: "new", name: info.name ?? fileName(path).replace(/\.prefab$/i, ""), assetGuid: r.guid,
        parentWorld: L.IDENTITY(), world: L.IDENTITY() };
      LE.items.push(item);
      LE.selected = item;
      rt("DropAt", { id: rid(item), key: r.guid, name: item.name, u, v });
      renderPanel();
      if (LE.assets.get(r.guid)?.state !== "loading" && LE.assets.get(r.guid)?.state !== "ready") await buildArchives([r.guid], op, wb, rev);
      else op.step("archives", "done", "already built");
      op.done(`${item.name} added: move it, then save`);
    } catch (e) { op.fail(e); }
  }

  function remove(item) {
    LE.items = LE.items.filter((i) => i !== item);
    if (LE.selected === item) LE.selected = null;
    rt("Remove", { id: rid(item) });
    renderPanel();
  }

  // ── saving ───────────────────────────────────────────────────────────

  async function save() {
    if (!dirtyCount() || LE.saveOp?.running) return;
    const path = LE.path, wb = LE.wb, base = LE.rev;
    const moved = LE.items.filter((i) => i.dirty), added = LE.items.filter((i) => i.kind === "new");
    const message = LE.message.trim() || `Level editor: ${[moved.length ? `move ${moved.length}` : "", added.length ? `add ${added.length}` : ""].filter(Boolean).join(", ")} in ${fileName(path)}`;
    const edits = L.saveEdits(LE.scene, LE.items.filter((i) => i.kind !== "new"), added, LE.prefabs);
    const text = Y.apply(LE.parsed, edits);
    const op = LE.saveOp = Ops.start({ title: `Save ${fileName(path)}`, kind: "edit-save", asset: path,
      steps: [{ id: "upload", label: "Upload and commit" }, { id: "validating", label: "Validate the new revision" }, { id: "settled", label: "Ready to read" }] });
    renderBar();
    try {
      op.step("upload", "active", `${edits.length} edits`);
      const r = await postJson(`${App.revUrl(wb, base)}/save`, { files: [{ path, text }], message }, op);
      op.step("upload", "done", `revision ${r.revision}`);
      const settled = await waitUntilSettled(wb, op, r.revision);
      // The scene now is the saved text: re-read it without rebuilding what's on screen. Added instances
      // become the scene's own (their new fileIDs), and keep their runtime ids.
      const runtimeIdOf = new Map(added.map((a) => [a.fileId, rid(a)]));
      const selected = LE.selected ? rid(LE.selected) : null;
      LE.parsed = Y.parse(text);
      LE.scene = L.readScene(LE.parsed);
      LE.items = L.placeables(LE.scene, LE.prefabs).items.map((i) => ({ ...i, rid: runtimeIdOf.get(i.id) ?? i.id }));
      LE.selected = LE.items.find((i) => rid(i) === selected) ?? null;
      LE.rev = settled;
      if (App.wb === wb) await openRevision(wb, settled);
      op.done(`Saved as revision ${settled}`);
    } catch (e) { op.fail(e); }
    renderPanel();
  }

  // ── panels ───────────────────────────────────────────────────────────

  function renderOverlay() {
    const ov = $("leOverlay");
    const run = LE.openOp?.running ? LE.openOp : null;
    let over = null, bottom = true;
    if (!LE.path) { over = h("div", {}, h("b", {}, "Open a scene."), h("div", {}, "Click a .unity file on the left. Then drag prefabs into the view to add them.")); bottom = false; }
    else if (viewer?.failed) { over = h("div", {}, viewer.failed); bottom = false; }
    else {
      const starting = playerStartingEl(viewer);
      if (run || starting) over = h("div", {}, starting, run ? stepsEl(run) : null);
    }
    ov.hidden = !over;
    ov.className = "stage-overlay" + (bottom ? "" : " center");
    put(ov, over);
  }

  function renderPanel() { renderHead(); renderBar(); renderList(); renderTransform(); renderOverlay(); }

  function renderHead() {
    put($("leHead"),
      h("div", { class: "path" }, LE.path ? [folderOf(LE.path) + "/", h("b", {}, fileName(LE.path))] : "No scene open"),
      h("span", { class: "grow" }),
      LE.path ? h("span", { class: "muted small" }, `${LE.items.length} objects · revision ${LE.rev}`) : null,
      h("button", { class: "btn small", disabled: !LE.path ? true : null, onclick: () => rt("Frame") }, "Frame all"),
      h("span", { class: "muted small", title: "Left-click selects; drag the arrows (or the object) to move it. Right-drag orbits, middle-drag (or Shift+right) pans, the wheel zooms. R rotates 15° (Shift+R back), F frames, Delete removes an added object." },
        "Left: select & move · Right: orbit · Wheel: zoom · R: rotate · F: frame"));
  }

  function renderBar() {
    const bar = $("leBar");
    if (!bar) return;
    const n = dirtyCount(), op = LE.saveOp;
    bar.classList.toggle("dirty", n > 0 && !op?.running);
    if (op?.running) {
      put(bar, h("span", { class: "spin" }), h("b", {}, op.activeStep?.label ?? "Saving"), op.activeStep?.sub ? h("span", { class: "muted small" }, op.activeStep.sub) : null,
        h("span", { class: "grow" }), h("span", { class: "muted small num", "data-op": op.id }, secs(op.elapsed)));
      return;
    }
    if (!n) {
      put(bar, op?.state === "done" ? h("span", { class: "dot ok" }) : null,
        h("span", { class: "muted small" }, op?.state === "done" ? `Saved as revision ${LE.rev}. ` : "", LE.path ? "Move or add objects, then save the scene as a new revision." : ""));
      return;
    }
    const moved = LE.rev !== App.revision && App.wb === LE.wb && App.revision;
    put(bar,
      h("b", {}, `${n} change${n === 1 ? "" : "s"}`),
      h("input", { placeholder: "Revision message", value: LE.message, oninput: (e) => (LE.message = e.target.value), onkeydown: (e) => e.key === "Enter" && save() }),
      h("button", { class: "btn small primary", onclick: save }, "Save as a new revision"),
      op?.state === "failed" ? h("div", { class: "callout bad", style: "flex-basis:100%" }, h("span", { class: "dot bad" }), h("div", {}, h("p", {}, h("b", {}, "Not saved. "), op.error))) : null,
      moved ? h("div", { class: "callout warn", style: "flex-basis:100%" }, h("span", { class: "dot warn" }), h("div", {},
        h("p", {}, `The workbench is now at revision ${App.revision}; these edits are on revision ${LE.rev}. Saving writes this whole scene, replacing any newer change to it.`))) : null);
  }

  const STATE = { building: ["spin", "building"], loading: ["spin", "loading"], ready: ["dot ok", "ready"], failed: ["dot bad", "failed"] };
  function renderList() {
    const list = $("leList");
    if (!list) return;
    const f = LE.filter.toLowerCase();
    const rows = LE.items.filter((i) => !f || i.name.toLowerCase().includes(f)).slice(0, 600).map((i) => {
      const a = i.assetGuid ? LE.assets.get(i.assetGuid) : null;
      const [cls, label] = i.primitive ? ["dot", "built-in mesh"] : a ? STATE[a.state] ?? ["dot", "waiting"] : ["dot", "waiting"];
      return h("li", {}, h("button", { class: "item", type: "button", "aria-current": LE.selected === i ? "true" : null,
        title: `${i.kind === "new" ? "added" : i.kind === "instance" ? "prefab instance" : "object"} · ${a?.path ?? i.primitive ?? ""}${a?.error ? `\n${a.error}` : ""}`,
        onclick: () => { LE.selected = i; rt("Select", { id: rid(i) }); renderPanel(); },
        ondblclick: () => rt("Frame", { id: rid(i) }) },
        h("span", { class: cls, title: label }), h("span", { class: "n" }, i.name),
        i.kind === "new" ? h("span", { class: "badge" }, "new") : i.dirty ? h("span", { class: "dot warn", title: "Moved" }) : null));
    });
    const failed = [...LE.assets.values()].filter((a) => a.state === "failed");
    put(list,
      LE.path ? h("li", { class: "grp" }, `Objects (${LE.items.length})`) : null,
      rows,
      LE.skipped.length ? h("li", { class: "note" }, `${LE.skipped.length} not shown: ${LE.skipped[0].reason}`) : null,
      failed.length ? h("li", { class: "note" }, `${failed.length} asset${failed.length === 1 ? "" : "s"} couldn't be built (grey boxes). Hover an object to see why.`) : null);
  }

  function renderTransform() {
    const box = $("leTransform");
    if (!box) return;
    const i = LE.selected;
    if (!i) { put(box, h("div", { class: "muted small", style: "padding:12px 16px" }, LE.path ? "Select an object in the view or the list." : "")); return; }
    const e = L.eulerOf(i.world.q);
    const input = (label, value, onchange) => h("span", { class: "axis" }, h("span", {}, label),
      h("input", { type: "text", inputmode: "decimal", value: L.fmt(Math.round(value * 1e4) / 1e4), onchange: (ev) => {
        const v = Number(ev.target.value);
        if (!Number.isFinite(v)) { ev.target.setCustomValidity("Enter a number"); ev.target.reportValidity(); return; }
        ev.target.setCustomValidity("");
        onchange(v);
        if (i.kind !== "new") i.dirty = true;
        rt("SetTransform", { id: rid(i), pos: i.world.p, rot: i.world.q, scale: i.world.s });
        renderList(); renderBar();
      } }));
    const vec = (label, values, set) => h("div", { class: "f" }, h("label", {}, label), h("div", { class: "v" },
      ["x", "y", "z"].map((k, n) => input(k, values[n], (v) => set(n, v)))));
    const a = i.assetGuid ? LE.assets.get(i.assetGuid) : null;
    put(box,
      h("div", { class: "section", style: "padding:12px 16px" },
        h("h2", {}, i.name, h("span", { class: "grow" }), i.kind === "new" ? h("button", { class: "btn small ghost", onclick: () => remove(i) }, "Remove") : null),
        h("div", { class: "muted small", style: "margin-bottom:8px;overflow-wrap:anywhere" },
          i.kind === "new" ? "Added (not saved yet) · " : i.kind === "instance" ? "Prefab instance · " : "Object · ", a?.path ?? i.primitive ?? "", a?.error ? h("div", { class: "s5" }, a.error) : null),
        h("div", { class: "fields", style: "padding:0" },
          vec("Position", i.world.p, (n, v) => (i.world.p = i.world.p.map((x, k) => (k === n ? v : x)))),
          vec("Rotation", e, (n, v) => { const ne = e.slice(); ne[n] = v; i.world.q = L.quatOf(ne); }),
          vec("Scale", i.world.s, (n, v) => (i.world.s = i.world.s.map((x, k) => (k === n ? v : x))))),
        h("div", { class: "muted small", style: "margin-top:6px" }, "World values. Saving writes them back relative to the object's parent.")));
  }

  // ── wiring ───────────────────────────────────────────────────────────

  $("leFilter").addEventListener("input", (e) => { LE.filter = e.target.value; renderList(); });
  renderPanel();
})();
