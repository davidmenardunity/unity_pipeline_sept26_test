// Explorer: browse a workbench, inspect an asset (facts, .meta, source, cloud preview, import results),
// preview it in the Scene Preview player, and see the pipeline calls made for it.
// Picking an asset updates the whole panel; work for the previous asset stops, except a preview that is
// building, which finishes in the background and marks its row.
"use strict";

(() => {
  const EX = {
    folder: null,        // the folder listed in the middle
    entries: [],
    sel: null,           // { path, guid, info, meta, results: { source, preview, imports } }
    guids: new Map(),    // path → guid, as resolved this session
    tab: readPref("explorer.tab") ?? "details",
    autoPreview: readPref("explorer.autoPreview") ?? true,
    viewer: null,
    openCall: null,
  };
  const tree = fileTree($("exTree"), {
    show: (e) => !e.path.endsWith(".meta") || $("showMeta").checked,
    onFolder: (path) => openFolder(path),
    onFile: (path) => { if (folderOf(path) !== EX.folder) openFolder(folderOf(path), false); selectAsset(path); },
    onDrop: (folder, files) => upload(folder, files),
  });

  App.register({
    id: "explorer", name: "Explorer", desc: "Browse a workbench, inspect assets and preview them in the player.",
    shown: () => { if (EX.tab === "viewer" && EX.sel) renderViewer(); },
  });

  App.on("revision", async ({ wb, revision }) => {
    $("exRev").textContent = revision ? `rev ${revision}` : "";
    await tree.open(wb, revision);
    if (!revision) { EX.folder = null; EX.entries = []; renderRows(); clearSelection(); return; }
    if (EX.folder) await openFolder(EX.folder, false);
    else await openFolder("Assets");
    if (EX.sel) selectAsset(EX.sel.path, { force: true });
  });
  App.on("viewer", () => { if (EX.tab === "viewer") StageComments.render(); });
  App.on("ops", () => { renderRows(); if (EX.sel) { renderBadges(); if (EX.tab === "viewer") renderViewer(); if (EX.tab === "calls") renderCalls(); } });
  App.on("calls", () => { if (EX.sel) { renderBadges(); if (EX.tab === "calls") renderCalls(); } });

  // ── the folder list ──────────────────────────────────────────────────────

  async function openFolder(path, reveal = true) {
    EX.folder = path;
    EX.entries = null;
    renderRows();
    if (reveal) tree.reveal(path).then(async (li) => { await li?.expand?.(); tree.select(path); });
    try {
      const entries = await tree.children(path);
      if (EX.folder !== path) return;
      EX.entries = entries;
    } catch (e) {
      if (EX.folder !== path) return;
      EX.entries = [];
      EX.error = describeError(e);
    }
    renderRows();
  }

  function archiveCell(path) {
    if (!canPreview(path)) return h("span", { class: "arch" }, "");
    const ops = Ops.list.filter((o) => o.kind === "preview" && o.asset === path && o.wb === App.wb && o.rev === App.revision);
    const run = ops.find((o) => o.running);
    if (run) return h("span", { class: "arch busy" }, h("span", { class: "spin" }), EX.sel?.path === path ? "Building…" : "Building (background)");
    const guid = EX.guids.get(path);
    if (guid && Archives.get(App.wb, App.revision, guid)) return h("span", { class: "arch ok" }, h("span", { class: "dot ok" }), "Ready");
    if (ops[0]?.state === "failed") return h("span", { class: "arch bad" }, h("span", { class: "dot bad" }), "Failed");
    return h("span", { class: "arch" }, "Not built");
  }

  function renderRows() {
    const path = EX.folder;
    if (!path) { put($("exPath"), "Pick a folder"); put($("exRows")); return; }
    const segs = path.split("/");
    put($("exPath"), segs.map((s, i) => i === segs.length - 1 ? h("b", {}, s) : [h("a", { href: "#", onclick: (e) => { e.preventDefault(); openFolder(segs.slice(0, i + 1).join("/")); } }, s), "/"]));
    if (EX.entries === null) { put($("exRows"), h("li", { class: "loading-line", style: "padding:12px 8px" }, h("span", { class: "spin" }), "Listing the folder…")); return; }
    const f = $("exFilter").value.trim().toLowerCase();
    const showMeta = $("showMeta").checked;
    const focused = document.activeElement?.closest?.("#exRows .row")?.dataset.path;
    const rows = EX.entries
      .filter((e) => (showMeta || !e.path.endsWith(".meta")) && (!f || fileName(e.path).toLowerCase().includes(f)))
      .map((e) => h("li", {}, h("button", { class: "row" + (e.path.endsWith(".meta") ? " meta" : ""), type: "button", role: "option",
        "aria-selected": EX.sel?.path === e.path ? "true" : "false", "data-path": e.path,
        onclick: () => (e.isFolder ? openFolder(e.path) : selectAsset(e.path)) },
        h("span", { class: "kind" }, kindOf(e.path, e.isFolder)), h("span", { class: "name" }, fileName(e.path)),
        e.isFolder ? h("span", { class: "arch" }, "") : archiveCell(e.path))));
    put($("exRows"), rows.length ? rows : h("li", { class: "loading-line", style: "padding:12px 8px" }, EX.error ? `Couldn't list: ${EX.error}` : f ? `Nothing here matches “${f}”.` : "This folder is empty."));
    if (focused) $("exRows").querySelector(`.row[data-path="${CSS.escape(focused)}"]`)?.focus();
  }

  // ── selection ────────────────────────────────────────────────────────────

  function clearSelection() {
    EX.sel = null;
    $("exAsset").hidden = true;
    $("exEmpty").hidden = false;
  }

  async function selectAsset(path, { force = false } = {}) {
    if (!force && EX.sel?.path === path && EX.sel.rev === App.revision) return;
    const wb = App.wb, rev = App.revision;
    // Reads for another asset stop; a preview being built carries on in the background.
    for (const o of Ops.running()) if (o.kind === "read" && o.asset !== path) o.stop(`You picked ${fileName(path)}.`);
    EX.sel = { path, wb, rev, guid: null, info: null, meta: null, results: {} };
    tree.select(path);
    renderRows();
    $("exEmpty").hidden = true;
    $("exAsset").hidden = false;
    $("exName").textContent = fileName(path);
    $("exKind").textContent = kindOf(path);
    $("exGuid").textContent = "resolving GUID…";
    showTab(EX.tab);

    const sel = EX.sel;
    const op = Ops.start({ title: `Read ${fileName(path)}`, kind: "read", asset: path,
      steps: [{ id: "guid", label: "Resolve the GUID and facts" }, { id: "meta", label: "Read the .meta file" }] });
    renderDetails();
    try {
      op.step("guid", "active");
      const r = await getJson(`${App.revUrl(wb, rev)}/asset?path=${enc(path)}`, op);
      if (EX.sel !== sel) return;
      sel.guid = r.guid;
      sel.info = r.info;
      if (r.guid) EX.guids.set(path, r.guid);
      op.step("guid", "done", r.guid ? short(r.guid) : "not an asset");
      $("exGuid").textContent = r.guid ?? "no GUID (not an asset?)";
      renderDetails();
      setCommentCount(null);
      if (EX.tab === "comments") CommentsTab.load();
      if (EX.tab === "viewer") renderViewer();
      if (r.guid) {
        op.step("meta", "active");
        const res = await call("GET", `${App.revUrl(wb, rev)}/meta?guid=${r.guid}`, null, op);
        if (EX.sel !== sel) return;
        sel.meta = await res.text();
        op.step("meta", "done");
      } else op.step("meta", "done", "skipped");
      op.done();
      op.quiet = true;   // routine: the status bar keeps showing the last notable result
      renderDetails();
      if (EX.tab === "viewer" && EX.autoPreview && canPreview(path) && !previewOp(path)) preview();
    } catch (e) {
      if (EX.sel !== sel) return;
      op.fail(e);
      sel.error = describeError(e);
      $("exGuid").textContent = "couldn't resolve";
      renderDetails();
    }
  }

  function showTab(tab) {
    EX.tab = tab;
    writePref("explorer.tab", tab);
    for (const t of document.querySelectorAll("#exAsset .tab")) t.setAttribute("aria-selected", t.dataset.tab === tab ? "true" : "false");
    for (const p of document.querySelectorAll("#exAsset [data-panel]")) p.hidden = p.dataset.panel !== tab;
    if (tab === "details") renderDetails();
    if (tab === "viewer") {
      renderViewer();
      if (EX.autoPreview && EX.sel?.guid && canPreview(EX.sel.path) && !previewOp(EX.sel.path) && !Archives.get(EX.sel.wb, EX.sel.rev, EX.sel.guid)) preview();
      else if (EX.sel?.guid) showArchiveIfBuilt();
    }
    if (tab === "calls") renderCalls();
    if (tab === "comments") CommentsTab.load();
    renderBadges();
  }

  function renderBadges() {
    const s = EX.sel;
    if (!s) return;
    const run = previewOp(s.path);
    put($("exViewerBadge"), run ? h("span", { class: "spin" }) : viewerFor(s.path, false)?.shown?.key === Archives.key(s.wb, s.rev, s.guid) ? h("span", { class: "dot ok", title: "Showing this asset" }) : null);
    $("exCallsBadge").textContent = callsFor(s.path).filter((c) => !c.isPoll).length;
  }

  // ── details ──────────────────────────────────────────────────────────────

  const loadingLine = (text) => h("div", { class: "loading-line" }, h("span", { class: "spin" }), text);

  function renderDetails() {
    const s = EX.sel;
    if (!s || EX.tab !== "details") return;
    const facts = s.info || s.guid !== null
      ? h("dl", { class: "facts" },
        [["Path", s.path], ["GUID", s.guid ?? "none (not an asset?)"], ["Revision", s.rev], ["Size", s.info?.size != null ? fmtBytes(s.info.size) : null],
          ["File hash", s.info?.fileHash], ["Meta hash", s.info?.metafileHash]].filter(([, v]) => v != null).map(([k, v]) => [h("dt", {}, k), h("dd", {}, v)]))
      : s.error ? h("div", { class: "callout bad" }, h("span", { class: "dot bad" }), h("div", {}, h("p", {}, s.error), h("div", { class: "actions" }, h("button", { class: "btn small", onclick: () => selectAsset(s.path, { force: true }) }, "Retry"))))
      : h("dl", { class: "facts" }, ["Path", "GUID", "Size", "File hash"].map((k, i) => [h("dt", {}, k), h("dd", {}, h("span", { class: "skel", style: `width:${[90, 70, 30, 80][i]}%` }))]));
    put($("exDetails"),
      h("div", { class: "section" }, h("h2", {}, "Asset"), facts),
      h("div", { class: "section" }, h("h2", {}, ".meta file"),
        s.meta != null ? h("pre", { class: "code" }, s.meta) : s.guid === null && !s.error ? loadingLine("Resolving the asset first…") : s.guid ? loadingLine("Reading the .meta file…") : h("div", { class: "muted small" }, "No .meta: this path isn't an asset.")),
      resultSection("source", "Source file", "Show the file", "Downloads the file at this revision.", () => runSource()),
      resultSection("preview", "Cloud preview image", "Render a preview", "Renders in the cloud in the selected environment. A cold render takes about 2 minutes.", () => runPreviewImage()),
      importsSection());
  }

  // A section whose content comes from one on-demand action; it shows its own progress and errors.
  function resultSection(key, title, label, hint, run) {
    const r = EX.sel.results[key];
    let body;
    if (!r) body = h("div", { style: "display:flex;gap:8px;align-items:center;flex-wrap:wrap" }, h("button", { class: "btn small", onclick: run, disabled: !EX.sel.guid && key !== "source" ? true : null }, label), h("span", { class: "muted small" }, hint));
    else if (r.op?.running) body = h("div", {}, loadingLine(`${r.op.activeStep?.label ?? "Working"}…`), h("span", { class: "muted small num", "data-op": r.op.id }, secs(r.op.elapsed)));
    else if (r.error) body = h("div", { class: "callout bad" }, h("span", { class: "dot bad" }), h("div", {}, h("p", {}, r.error), h("div", { class: "actions" }, h("button", { class: "btn small", onclick: run }, "Retry"))));
    else body = h("div", { class: "result" }, r.node);
    return h("div", { class: "section" }, h("h2", {}, title, h("span", { class: "grow" }), r && !r.op?.running ? h("button", { class: "btn small ghost", onclick: () => { delete EX.sel.results[key]; renderDetails(); } }, "Clear") : null), body);
  }

  async function runResult(key, title, steps, fn) {
    const s = EX.sel;
    const op = Ops.start({ title: `${title} · ${fileName(s.path)}`, kind: "read-" + key, asset: s.path, steps });
    s.results[key] = { op };
    renderDetails();
    try {
      const node = await op.run(fn);
      s.results[key] = { node };
    } catch (e) {
      s.results[key] = { error: describeError(e) };
    }
    if (EX.sel === s) renderDetails();
  }

  function runSource() {
    const s = EX.sel;
    runResult("source", "Source", [{ id: "get", label: "Download the file" }], async (op) => {
      op.step("get", "active");
      const res = await call("GET", `${App.revUrl(s.wb, s.rev)}/file?path=${enc(s.path)}`, null, op);
      return bytesNode(res, fileName(s.path));
    });
  }

  function runPreviewImage() {
    const s = EX.sel;
    runResult("preview", "Preview image", [{ id: "render", label: "Render in the cloud" }], async (op) => {
      if (!App.env) throw new Error("Previews render in an environment. Add one at the top first.");
      op.step("render", "active", "a cold render takes about 2 minutes");
      const res = await call("GET", `/api/workbenches/${s.wb}/environments/${App.env}/revisions/${enc(s.rev)}/preview?guid=${s.guid}`, null, op);
      if (res.status === 204) return h("div", { class: "muted small" }, "This asset type has no preview image (scenes, scripts… don't render).");
      return bytesNode(res, `${fileName(s.path)}.png`);
    });
  }

  async function bytesNode(res, name) {
    const type = res.headers.get("Content-Type") || "";
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const save = h("a", { class: "save-link", href: url, download: name }, `Save ${name} (${fmtBytes(blob.size)})`);
    if (type.startsWith("image/")) return h("div", {}, h("img", { src: url, alt: name }), h("div", {}, save));
    if (type.startsWith("text/")) {
      let text = await blob.text();
      if (text.length > 200_000) text = text.slice(0, 200_000) + "\n… (trimmed; save the file to see all of it)";
      return h("div", {}, h("pre", { class: "code" }, text), h("div", {}, save));
    }
    const head = new Uint8Array(await blob.slice(0, 16).arrayBuffer());
    return h("div", {}, h("div", { class: "small" }, `Binary, ${fmtBytes(blob.size)}. Starts with `, h("code", {}, [...head].map((b) => (b >= 32 && b < 127 ? String.fromCharCode(b) : ".")).join(""))), h("div", {}, save));
  }

  // Import results: the primary import (G:) or one importer's (T:), and their artifacts.
  function importsSection() {
    const s = EX.sel;
    const r = s.results.imports;
    const mode = s.importMode ?? "G";
    const importer = h("input", { id: "exImporter", class: "mono", style: "flex:1;min-width:200px", value: s.importer ?? "", placeholder: "Unity.Pipeline.Samples.ScenePreview.Importer.PreviewContentImporter",
      title: "Full type name of the importer, namespace included", oninput: (e) => (s.importer = e.target.value) });
    const controls = h("div", { style: "display:flex;gap:6px;align-items:center;flex-wrap:wrap" },
      h("select", { onchange: (e) => { s.importMode = e.target.value; renderDetails(); } },
        h("option", { value: "G", selected: mode === "G" ? true : null }, "Primary import (G:)"),
        h("option", { value: "T", selected: mode === "T" ? true : null }, "One importer (T:)")),
      mode === "T" ? importer : null,
      h("button", { class: "btn small", disabled: !s.guid || r?.op?.running ? true : null, onclick: runImports }, r ? "Import again" : "Import"));
    let body = null;
    if (r?.op?.running) body = loadingLine(`${r.op.activeStep?.label ?? "Importing"}… (the first import after a cold start can take minutes)`);
    else if (r?.error) body = h("div", { class: "callout bad" }, h("span", { class: "dot bad" }), h("div", {}, h("p", {}, r.error)));
    else if (r?.result) {
      const x = r.result;
      body = x.error
        ? h("div", { class: "callout warn" }, h("span", { class: "dot warn" }), h("div", {}, h("p", {}, `${x.error.code}: ${x.error.message ?? ""}`),
          x.error.code === "import_not_found" ? h("p", { class: "muted" }, "This project has no importer of that type for this asset.") : null))
        : h("div", {}, h("ul", { class: "list" }, x.artifacts.length ? x.artifacts.map((name) => artifactRow(r, name)) : h("li", { class: "muted" }, "The manifest lists no artifacts.")),
          h("details", {}, h("summary", { class: "muted small" }, "Import manifest (raw)"), h("pre", { class: "code" }, JSON.stringify(x.manifest, null, 2))));
    }
    return h("div", { class: "section" }, h("h2", {}, "Import results"), controls, body ? h("div", { style: "margin-top:8px" }, body) : h("div", { class: "muted small", style: "margin-top:6px" }, "Runs in the selected environment."));
  }

  function artifactRow(r, name) {
    const st = r.fetched?.[name];
    return h("li", {},
      h("span", { class: "what mono" }, name || h("span", { class: "muted" }, "(main import result)")),
      st?.op?.running ? loadingLine("fetching…") : h("button", { class: "btn small", onclick: () => fetchArtifact(r, name) }, st?.node ? "Fetch again" : "Fetch"),
      st?.node ? h("div", { class: "out result" }, st.node) : st?.error ? h("div", { class: "out callout bad" }, h("p", {}, st.error)) : null);
  }

  async function runImports() {
    const s = EX.sel;
    if (!App.env) { s.results.imports = { error: "Imports run in an environment. Add one at the top first." }; renderDetails(); return; }
    const type = (s.importer ?? "").trim() || $("exImporter")?.placeholder;
    const address = (s.importMode ?? "G") === "T" ? `T:${s.guid}+${type}` : `G:${s.guid}`;
    const op = Ops.start({ title: `Import ${fileName(s.path)} (${address.slice(0, 2)})`, kind: "read-imports", asset: s.path, steps: [{ id: "import", label: `Import ${address.length > 48 ? address.slice(0, 48) + "…" : address}` }] });
    s.results.imports = { op, address, env: App.env };
    renderDetails();
    try {
      op.step("import", "active");
      const result = await op.run((o) => postJson(`/api/workbenches/${s.wb}/environments/${App.env}/revisions/${enc(s.rev)}/imports`, { address }, o));
      s.results.imports = { result, address, env: s.results.imports.env, fetched: {} };
    } catch (e) { s.results.imports = { error: describeError(e) }; }
    if (EX.sel === s) renderDetails();
  }

  async function fetchArtifact(r, name) {
    const s = EX.sel;
    const op = Ops.start({ title: `Fetch ${name || "main import result"} · ${fileName(s.path)}`, kind: "read-artifact", asset: s.path });
    r.fetched[name] = { op };
    renderDetails();
    try {
      const res = await op.run((o) => call("GET", `/api/workbenches/${s.wb}/environments/${r.env}/revisions/${enc(s.rev)}/imports/artifact?address=${enc(r.address)}&name=${enc(name)}`, null, o));
      // Name the saved file after the asset: ".ca" alone would be saved as "ca", and the main result has no name.
      const base = fileName(s.path).replace(/\.[^.]+$/, "");
      r.fetched[name] = { node: await bytesNode(res, !name ? `${base}.bin` : name.startsWith(".") ? `${base}${name}` : fileName(name)) };
    } catch (e) { r.fetched[name] = { error: describeError(e) }; }
    if (EX.sel === s) renderDetails();
  }

  // ── viewer ───────────────────────────────────────────────────────────────

  const previewOp = (path) => Ops.list.find((o) => o.kind === "preview" && o.asset === path && o.running && o.wb === App.wb && o.rev === App.revision);

  // Objects show in the Scene Preview player; scenes (.unity) in the Scene Viewer player, in the same
  // stage (only one of the two frames is visible).
  function viewer() {
    EX.viewer ??= new Viewer($("exPlayer"), "Explorer");
    return EX.viewer;
  }

  function sceneViewer() {
    EX.sceneViewer ??= new Viewer($("exScenePlayer"), "Explorer, scenes", "sceneplayer.html?embedded=1");
    return EX.sceneViewer;
  }

  function viewerFor(path, create = true) {
    if (isScene(path)) return create ? sceneViewer() : EX.sceneViewer;
    return create ? viewer() : EX.viewer;
  }

  function showArchiveIfBuilt() {
    const s = EX.sel;
    const built = s?.guid && Archives.get(s.wb, s.rev, s.guid);
    const key = s?.guid && Archives.key(s.wb, s.rev, s.guid);
    if (built && viewerFor(s.path).shown?.key !== key) {
      viewerFor(s.path).load(built.url, fileName(s.path), key, "explorer");
      if (!isScene(s.path)) StageComments.setContext(s);
    }
    renderViewer();
  }

  async function preview({ rebuild = false } = {}) {
    const s = EX.sel;
    if (!s?.guid || !canPreview(s.path)) return;
    if (rebuild) Archives.built.delete(Archives.key(s.wb, s.rev, s.guid));
    viewerFor(s.path).ensure("explorer");
    const op = Ops.start({ title: `Preview ${fileName(s.path)}`, kind: "preview", asset: s.path,
      steps: [{ id: "archive", label: "Pipeline builds the WebGL content archive" }, { id: "load", label: "Load it in the viewer" }] });
    Object.assign(op, { wb: s.wb, rev: s.rev });
    op.onShow = () => { if (EX.sel?.path !== s.path) selectAsset(s.path); showTab("viewer"); };
    op.retry = { label: "Retry the preview", run: () => { if (EX.sel?.path !== s.path) selectAsset(s.path); showTab("viewer"); preview({ rebuild: true }); } };
    renderViewer();
    try {
      const r = await Archives.build(s.wb, s.rev, s.guid, op, { scene: isScene(s.path) });
      if (EX.sel?.path !== s.path) {
        op.step("load", "done", "skipped: another asset is selected");
        op.done("Archive ready (finished in the background)");
        renderRows();
        return;
      }
      op.step("load", "active", "sent to the player");
      viewerFor(s.path).load(r.url, fileName(s.path), Archives.key(s.wb, s.rev, s.guid), "explorer");
      if (!isScene(s.path)) StageComments.setContext(s);
      op.step("load", "done", r.artifact);
      op.done(`${r.artifact} in the viewer`);
    } catch (e) {
      op.fail(e);
    }
    renderViewer();
  }

  function renderViewer() {
    const s = EX.sel;
    if (!s || EX.tab !== "viewer") return;
    const overlay = $("exStageOverlay");
    const scene = isScene(s.path);
    $("exPlayer").hidden = scene;
    $("exScenePlayer").hidden = !scene;
    if (scene) viewerFor(s.path).ensure("explorer");
    const v = viewerFor(s.path, false);
    const run = previewOp(s.path);
    const last = Ops.list.find((o) => o.kind === "preview" && o.asset === s.path && o.wb === s.wb && o.rev === s.rev);
    const key = s.guid ? Archives.key(s.wb, s.rev, s.guid) : null;
    const showing = v?.shown?.key === key && key;
    let over = null, info = [];
    if (!canPreview(s.path)) over = h("div", {}, h("b", {}, "No 3D preview for this file type."), h("div", {}, "The viewer shows prefabs, models (.fbx, .obj), materials and scenes (.unity)."));
    else if (!s.guid) over = s.error ? h("div", {}, "Couldn't resolve this asset.") : loadingLine("Resolving the asset…");
    else if (run) over = h("div", {}, h("div", { style: "display:flex;gap:8px;align-items:center;margin-bottom:8px" }, h("span", { class: "spin" }),
      h("b", {}, run.activeStep?.label ?? "Working"), h("span", { class: "grow" }), h("span", { class: "num", "data-op": run.id }, secs(run.elapsed))), stepsEl(run));
    else if (last?.state === "failed" && !showing) {
      over = h("div", {}, "No archive for ", h("b", {}, fileName(s.path)), ". Details below.");
      info.push(h("div", { class: "callout bad" }, h("span", { class: "dot bad" }), h("div", {}, h("p", {}, h("b", {}, "The preview failed.")), h("p", {}, last.error),
        h("div", { class: "actions" }, h("button", { class: "btn small primary", onclick: () => preview({ rebuild: true }) }, "Retry"), h("button", { class: "btn small", onclick: () => showTab("calls") }, "See the calls")))));
    } else if (!showing) {
      const built = Archives.get(s.wb, s.rev, s.guid);
      over = h("div", {}, h("div", { style: "margin-bottom:8px" }, built ? "The archive for this asset is ready." : `Pipeline builds a WebGL content archive for ${fileName(s.path)}, then the viewer loads it.`),
        h("button", { class: "btn primary", onclick: () => (built ? showArchiveIfBuilt() : preview()) }, built ? "Load in the viewer" : "Build and preview"));
    }
    let progressBox = !!run;
    // A scene: its own loading, then how to play it (hidden while the mouse is captured).
    if (!over && scene && showing && v?.scene) {
      const st = v.scene;
      if (st.state === "downloading" || st.state === "loading") {
        over = h("div", { style: "display:flex;gap:8px;align-items:center" }, h("span", { class: "spin" }), h("b", {}, st.state === "downloading" ? "Downloading the scene" : "Loading the scene"),
          h("span", { style: "opacity:.75" }, st.state === "downloading" && st.progress >= 0 ? `${Math.round(st.progress * 100)}%` : st.detail ?? ""));
        progressBox = true;
      } else if (st.state === "error") {
        over = h("div", {}, h("b", {}, "The scene didn't load."), h("div", {}, st.detail));
        info.push(h("div", { class: "callout bad" }, h("span", { class: "dot bad" }), h("div", {}, h("p", {}, st.detail),
          h("div", { class: "actions" }, h("button", { class: "btn small", onclick: () => { v.shown = null; showArchiveIfBuilt(); } }, "Load again"), h("button", { class: "btn small", onclick: () => preview({ rebuild: true }) }, "Rebuild the archive")))));
      } else if (st.state === "loaded" && !st.captured) {
        over = h("div", {}, h("b", {}, "Click the view to play. "), st.player === "scene"
          ? "The scene's own player: mouse looks, WASD walks, Shift runs, Space jumps. Esc gives the mouse back."
          : `Mouse looks, WASD moves, Shift runs, Space jumps, F ${st.mode === "flying" ? "walks" : "flies"}. Esc gives the mouse back.`);
        progressBox = true;
      }
    }
    if (!over && playerStartingEl(v)) { over = playerStartingEl(v); progressBox = true; }
    else if (run && playerStartingEl(v)) over = h("div", {}, playerStartingEl(v), h("div", { style: "height:8px" }), over);
    if (v?.failed) info.unshift(h("div", { class: "callout bad" }, h("span", { class: "dot bad" }), h("div", {}, h("p", {}, v.failed))));
    overlay.hidden = !over;
    overlay.className = "stage-overlay" + (progressBox ? "" : " center");
    put(overlay, over);
    const built = s.guid && Archives.get(s.wb, s.rev, s.guid);
    put($("exStageBar"),
      showing ? h("span", { class: "chip" }, `${fileName(s.path).replace(/\.[^.]+$/, "")}${built?.artifact ?? ".ca"}`) : h("span", { class: "chip" }, v?.ready ? "player ready" : v ? "player starting…" : "player not started"),
      showing && built ? h("span", { class: "muted small" }, `from the ${built.platform} environment ${short(built.environmentId)}`) : null,
      scene && showing && v?.scene?.state === "loaded" ? h("span", { class: "muted small" }, `${v.scene.objects} objects${v.scene.camera ? ` · from its camera "${v.scene.camera}"` : ""}`) : null,
      h("span", { class: "grow" }),
      h("label", { class: "switch" }, h("input", { type: "checkbox", checked: EX.autoPreview ? true : null, onchange: (e) => { EX.autoPreview = e.target.checked; writePref("explorer.autoPreview", EX.autoPreview); } }), "Preview on select"),
      canPreview(s.path) && s.guid && !run ? h("button", { class: "btn small", onclick: () => preview({ rebuild: true }) }, showing ? "Rebuild" : "Build") : null);
    StageComments.render();
    put($("exViewerInfo"), info,
      h("p", { class: "muted small", style: "margin:0" }, scene
        ? "Scenes play in their own player (SceneViewer_WebGL), from an archive of the whole scene built by Pipeline (PreviewSceneImporter). Scripts the player wasn't built with load as missing components."
        : "Materials using shader model 4.5 render pink on WebGL 2; the player uses WebGPU when the browser offers it. You can also drop a .ca file on the player."));
    renderBadges();
  }

  // ── calls for this asset ─────────────────────────────────────────────────

  function callsFor(path) {
    const ids = new Set(Ops.list.filter((o) => o.asset === path).map((o) => o.id));
    return App.calls.filter((c) => ids.has(c.op)).slice().reverse();
  }

  function renderCalls() {
    const s = EX.sel;
    if (!s) return;
    const calls = callsFor(s.path).filter((c) => !c.isPoll);
    put($("exCalls"),
      h("div", { class: "section" }, h("h2", {}, `${calls.length} Pipeline call${calls.length === 1 ? "" : "s"} for ${fileName(s.path)}`),
        calls.length ? h("ol", { class: "calls" }, calls.map((c) => h("li", { "aria-current": EX.openCall === c ? "true" : null, onclick: () => { EX.openCall = c; showExchange(c); renderCalls(); } },
          h("span", {}, c.method), h("span", { class: "s" + String(c.status)[0] }, String(c.status || "ERR")), h("span", { class: "p", title: c.path }, c.path), h("span", { class: "muted" }, `${c.ms} ms`))))
          : h("div", { class: "muted small" }, "Nothing yet.")),
      EX.openCall && calls.includes(EX.openCall) ? h("div", { class: "section" }, h("h2", {}, "Exchange", h("span", { class: "grow" }),
        EX.openCall.curl ? h("button", { class: "btn small", onclick: () => navigator.clipboard?.writeText(EX.openCall.curl) }, "Copy as curl") : null),
        h("pre", { class: "code", style: "white-space:pre-wrap;word-break:break-all;max-height:none" }, EX.openCall.exchange ?? "(no details)")) : null);
  }

  // ── comments ─────────────────────────────────────────────────────────────

  function setCommentCount(n) {
    const b = $("exCommentsBadge");
    b.hidden = n == null || n === 0;
    b.textContent = n ?? "";
  }

  // Show a comment where it was made: the viewer, the asset loaded, the camera where it stood.
  async function focusComment(id) {
    const s = EX.sel;
    showTab("viewer");
    for (let i = 0; i < 600 && EX.sel === s && !StageComments.showing(s); i++) await sleep(200);
    if (EX.sel !== s || !StageComments.showing(s)) return;
    await sleep(300);   // the pins report their screen places
    const pin = StageComments.pins.find((p) => p.id === id) ?? { id };
    StageComments.openThread(pin);
  }

  CommentsTab.host = { el: $("exComments"), sel: () => EX.sel, viewer: () => viewer(), focus: focusComment,
    onCount: (n) => { if (CommentsTab.scope === "asset") setCommentCount(n); } };
  StageComments.init({ stage: $("exPlayer").parentElement, viewer: () => viewer(), sel: () => EX.sel,
    onCreated: () => { if (EX.tab === "comments") CommentsTab.load({ force: true }); },
    onCount: (n) => setCommentCount(n) });

  // ── uploads ──────────────────────────────────────────────────────────────

  async function upload(folder, files) {
    const r = await uploadFiles(folder, files, "explorer");
    if (r?.paths?.length) {
      await openFolder(folder);
      selectAsset(r.paths[0]);
    }
  }

  // ── resizing the asset panel ─────────────────────────────────────────────

  function wireSplitter() {
    const split = $("exSplit"), view = $("view-explorer");
    const MIN = 320, FILES = 250, MIN_FOLDER = 220;
    const setWidth = (px, save = true) => {
      const max = view.clientWidth - FILES - MIN_FOLDER - 6;
      const w = Math.round(Math.max(MIN, Math.min(max, px)));
      view.style.setProperty("--insp-w", `${w}px`);
      split.setAttribute("aria-valuenow", String(w));
      if (save) writePref("explorer.panelWidth", w);
    };
    const saved = readPref("explorer.panelWidth");
    if (saved) setWidth(saved, false);
    split.addEventListener("pointerdown", (e) => {
      split.setPointerCapture(e.pointerId);
      split.classList.add("dragging");
      document.body.classList.add("resizing");   // the player iframe would swallow the pointer otherwise
      const right = view.getBoundingClientRect().right;
      const move = (ev) => setWidth(right - ev.clientX);
      const up = () => {
        split.classList.remove("dragging");
        document.body.classList.remove("resizing");
        split.removeEventListener("pointermove", move);
        split.removeEventListener("pointerup", up);
      };
      split.addEventListener("pointermove", move);
      split.addEventListener("pointerup", up);
    });
    split.addEventListener("keydown", (e) => {
      const now = $("exInspector").getBoundingClientRect().width;
      if (e.key === "ArrowLeft") { e.preventDefault(); setWidth(now + 32); }
      if (e.key === "ArrowRight") { e.preventDefault(); setWidth(now - 32); }
    });
    // Back to half the window.
    split.addEventListener("dblclick", () => { view.style.removeProperty("--insp-w"); writePref("explorer.panelWidth", null); });
  }

  // ── wiring ───────────────────────────────────────────────────────────────

  wireSplitter();

  for (const t of document.querySelectorAll("#exAsset .tab")) t.addEventListener("click", () => showTab(t.dataset.tab));
  $("exFilter").addEventListener("input", renderRows);
  $("showMeta").addEventListener("change", () => { renderRows(); tree.open(App.wb, App.revision); });
  $("exCopyGuid").addEventListener("click", () => EX.sel?.guid && navigator.clipboard?.writeText(EX.sel.guid).catch(() => getSelection().selectAllChildren($("exGuid"))));
  dropTarget($("exFolder"), () => EX.folder, (folder, files) => folder && upload(folder, files));
  $("exRows").addEventListener("keydown", (e) => {
    if (!["ArrowDown", "ArrowUp"].includes(e.key)) return;
    const buttons = [...$("exRows").querySelectorAll(".row")];
    const i = buttons.indexOf(document.activeElement);
    const next = buttons[Math.max(0, Math.min(buttons.length - 1, i + (e.key === "ArrowDown" ? 1 : -1)))];
    if (next) { e.preventDefault(); next.focus(); next.click(); }
  });
})();
