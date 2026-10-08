// Compare branches: pick an asset in the selected workbench, pick a second branch, and see the asset from
// both side by side: two Scene Preview players, its facts, and a text diff when the file is text.
// The second branch is read through its own workbench (found, or created on request).
"use strict";

(() => {
  const TEXTY = /\.(prefab|unity|asset|mat|meta|cs|shader|json|txt|xml|asmdef|pipetxt|uss|uxml|inputactions|controller|anim|physicMaterial|lighting|shadergraph)$/i;
  const CM = {
    path: null,
    a: null, b: null,                       // { branch, wb, rev, guid, info, state, error }
    branchB: readPref("compare.branch"),
    diff: null,                             // { op, lines, error, same }
    built: false,
    viewers: null,
  };

  const tree = fileTree($("cmTree"), { show: (e) => !e.path.endsWith(".meta"), onFile: (path) => select(path) });

  App.register({ id: "compare", name: "Compare branches", desc: "One asset on two branches: two viewers side by side, its facts, and a diff." });

  App.on("revision", async ({ wb, revision }) => {
    $("cmRev").textContent = revision ? `rev ${revision}` : "";
    await tree.open(wb, revision);
    if (CM.path && revision) select(CM.path, true);
  });
  App.on("workbenches", () => { if (CM.path) renderHead(); });
  App.on("branches", () => { if (CM.path) renderHead(); });
  App.on("ops", () => { if (CM.path) { renderHead(); renderStages(); renderBody(); } });

  // ── the layout, built once so the players aren't reloaded ────────────────

  function build() {
    if (CM.built) return;
    CM.built = true;
    const stage = (side) => h("div", { class: "stage-wrap" },
      h("div", { class: "side-title", id: `cm${side}Title` }),
      h("div", { class: "stage" }, h("iframe", { id: `cm${side}Player`, title: `Scene Preview player, side ${side}`, allow: "fullscreen" }),
        h("div", { class: "stage-overlay center", id: `cm${side}Overlay` })));
    put($("cmMain"),
      h("div", { class: "cmp-head", id: "cmHead" }),
      h("div", { class: "pair" }, stage("A"), stage("B")),
      h("div", { class: "cmp-body", id: "cmBody" }));
    CM.viewers = { A: new Viewer($("cmAPlayer"), "compare, this branch"), B: new Viewer($("cmBPlayer"), "compare, other branch") };
  }

  // ── selection ────────────────────────────────────────────────────────────

  async function select(path, again = false) {
    if (!again && path === CM.path && CM.a?.rev === App.revision) return;
    for (const o of Ops.running()) if (o.kind === "compare-read" && o.asset !== path) o.stop(`You picked ${fileName(path)}.`);
    build();
    tree.select(path);
    CM.path = path;
    CM.diff = null;
    CM.a = { branch: App.branchOf(App.wb), wb: App.wb, rev: App.revision, state: "reading" };
    CM.b = null;
    renderAll();
    readSide(CM.a, path);
    await setupB();
  }

  function otherBranch() {
    if (CM.branchB && CM.branchB !== CM.a.branch && App.branches.includes(CM.branchB)) return CM.branchB;
    const withWb = App.branches.find((b) => b !== CM.a.branch && workbenchesOn(b).length);
    return withWb ?? App.branches.find((b) => b !== CM.a.branch) ?? null;
  }

  // Find the second branch's workbench and its readable revision, then read the asset there.
  async function setupB() {
    const path = CM.path;
    const branch = otherBranch();
    if (!branch) { CM.b = { state: "no-branch" }; renderAll(); return; }
    const wbs = workbenchesOn(branch).filter((w) => w.workbenchId !== CM.a.wb);
    CM.b = { branch, wb: wbs[0]?.workbenchId ?? null, state: wbs.length ? "settling" : "no-workbench" };
    renderAll();
    if (!CM.b.wb) return;
    const side = CM.b;
    const op = Ops.start({ title: `Open ${branch} for comparison`, kind: "compare-read", asset: path, example: "compare",
      steps: [{ id: "validating", label: `Workbench ${short(side.wb)} on ${branch}` }, { id: "settled", label: "Ready to read" }] });
    op.quiet = true;
    try {
      side.rev = await waitUntilSettled(side.wb, op);
      op.done(`revision ${side.rev}`);
    } catch (e) {
      op.fail(e);
      if (CM.b === side) { side.state = "error"; side.error = describeError(e); renderAll(); }
      return;
    }
    if (CM.b !== side || CM.path !== path) return;
    readSide(side, path);
  }

  async function readSide(side, path) {
    side.state = "reading";
    renderAll();
    const op = Ops.start({ title: `Read ${fileName(path)} on ${side.branch}`, kind: "compare-read", asset: path, example: "compare",
      steps: [{ id: "guid", label: "Find the asset" }] });
    op.quiet = true;
    try {
      op.step("guid", "active");
      const r = await getJson(`${App.revUrl(side.wb, side.rev)}/asset?path=${enc(path)}`, op);
      if (CM.path !== path) return;
      Object.assign(side, { guid: r.guid, info: r.info, state: r.guid || r.info ? "ready" : "missing" });
      op.step("guid", "done", r.guid ? short(r.guid) : "not there").done();
    } catch (e) {
      if (CM.path !== path) return;
      const missing = e.status === 404 || e.body?.code === "not_found";
      Object.assign(side, { state: missing ? "missing" : "error", error: describeError(e) });
      if (missing) op.done("not on this branch"); else op.fail(e);
    }
    renderAll();
    maybeContinue(path);
  }

  // Once both sides are read: preview both, and diff the text.
  function maybeContinue(path) {
    const { a, b } = CM;
    if (CM.path !== path || !a || !b || !["ready", "missing"].includes(a.state) || !["ready", "missing"].includes(b.state)) return;
    if (canPreview(path)) for (const [key, side] of [["A", a], ["B", b]]) if (side.state === "ready" && side.guid) preview(key, side, path);
    if (TEXTY.test(path) && a.state === "ready" && b.state === "ready") diff(path);
  }

  async function preview(key, side, path) {
    const viewer = CM.viewers[key];
    const k = Archives.key(side.wb, side.rev, side.guid);
    if (viewer.shown?.key === k) return;
    const running = Ops.running().find((o) => o.kind === "compare-preview" && o.archiveKey === k);
    if (running) return;
    viewer.ensure("compare");
    const op = Ops.start({ title: `Preview ${fileName(path)} on ${side.branch}`, kind: "compare-preview", asset: path, example: "compare",
      steps: [{ id: "archive", label: "Pipeline builds the WebGL content archive" }, { id: "load", label: "Load it in the viewer" }] });
    op.archiveKey = k;
    op.retry = { label: "Retry the preview", run: () => preview(key, side, path) };
    try {
      const r = await Archives.build(side.wb, side.rev, side.guid, op);
      if (CM.path !== path) { op.done("Archive ready (you picked another asset)"); return; }
      op.step("load", "active");
      viewer.load(r.url, fileName(path), k, "compare");
      op.step("load", "done").done(`${r.artifact} in the viewer`);
    } catch (e) { op.fail(e); }
  }

  async function diff(path) {
    const { a, b } = CM;
    if (a.info?.fileHash && a.info.fileHash === b.info?.fileHash) { CM.diff = { same: true }; renderBody(); return; }
    const op = Ops.start({ title: `Diff ${fileName(path)}`, kind: "compare-read", asset: path, example: "compare",
      steps: [{ id: "a", label: `Download from ${a.branch}` }, { id: "b", label: `Download from ${b.branch}` }, { id: "diff", label: "Compare lines" }] });
    CM.diff = { op };
    renderBody();
    try {
      const text = async (side, id) => {
        op.step(id, "active");
        const t = await (await call("GET", `${App.revUrl(side.wb, side.rev)}/file?path=${enc(path)}`, null, op)).text();
        op.step(id, "done", fmtBytes(t.length));
        return t;
      };
      const [ta, tb] = [await text(a, "a"), await text(b, "b")];
      op.step("diff", "active");
      const lines = lineDiff(ta, tb);
      op.step("diff", "done");
      op.done(lines ? `${lines.filter(([t]) => t === "+").length} added, ${lines.filter(([t]) => t === "-").length} removed` : "too big to diff");
      if (CM.path === path) CM.diff = { lines, tooBig: !lines, same: ta === tb };
    } catch (e) {
      op.fail(e);
      if (CM.path === path) CM.diff = { error: describeError(e) };
    }
    renderBody();
  }

  // ── rendering ────────────────────────────────────────────────────────────

  function renderAll() { renderHead(); renderStages(); renderBody(); }

  function renderHead() {
    const { a, b } = CM;
    if (!a) return;
    const creating = Ops.running().find((o) => o.kind === "workbench" && o.title.endsWith(` ${b?.branch}`));
    const branches = App.branches.filter((x) => x !== a.branch);
    const sel = h("select", { "aria-label": "Branch to compare with",
      onchange: (e) => { CM.branchB = e.target.value; writePref("compare.branch", CM.branchB); CM.diff = null; CM.viewers?.B.clear(); setupB(); } },
      branches.map((x) => h("option", { value: x, selected: x === b?.branch ? true : null }, `${x}${workbenchesOn(x).length ? "" : " (no workbench)"}`)));
    let bLine;
    if (!b) bLine = h("span", { class: "wb" }, h("span", { class: "spin" }), "finding a workbench…");
    else if (b.state === "no-branch") bLine = h("span", { class: "wb warn" }, "The repo has no other branch.");
    else if (b.state === "no-workbench") bLine = creating
      ? h("span", { class: "wb" }, h("span", { class: "spin" }), `creating a workbench · ${creating.activeStep?.label ?? ""}`, h("span", { class: "num", "data-op": creating.id }, secs(creating.elapsed)))
      : h("span", { class: "wb warn" }, "No workbench yet. ", h("button", { class: "btn small primary", onclick: () => createForB(b.branch) }, `Create one on ${b.branch}`));
    else if (b.state === "settling") bLine = h("span", { class: "wb" }, h("span", { class: "spin" }), `workbench ${short(b.wb)} is getting ready…`);
    else if (b.state === "error") bLine = h("span", { class: "wb warn", title: b.error }, `workbench ${short(b.wb)}: ${b.error.split("\n")[0]}`);
    else bLine = h("span", { class: "wb" }, `workbench ${short(b.wb)} · revision ${b.rev ?? "?"}`);
    put($("cmHead"),
      h("div", { class: "side" }, h("small", {}, "This branch (from the top)"), h("span", { class: "b" }, a.branch), h("span", { class: "wb" }, `workbench ${short(a.wb)} · revision ${a.rev}`)),
      h("div", { class: "side" }, h("small", {}, "Compare with"), branches.length ? sel : h("span", { class: "muted" }, "no other branch"), bLine),
      h("div", { class: "side", style: "flex:2" }, h("small", {}, "Asset"), h("span", { class: "b mono", style: "overflow-wrap:anywhere" }, CM.path)));
  }

  async function createForB(branch) {
    renderHead();
    const id = await createWorkbench(branch, App.repository, { select: false, example: "compare" });
    if (id && CM.b?.branch === branch) setupB();
  }

  function renderStages() {
    if (!CM.built || !CM.a) return;
    for (const [key, side] of [["A", CM.a], ["B", CM.b]]) {
      const v = CM.viewers[key];
      const title = $(`cm${key}Title`), overlay = $(`cm${key}Overlay`);
      const k = side?.guid ? Archives.key(side.wb, side.rev, side.guid) : null;
      const showing = k && v.shown?.key === k;
      const run = k && Ops.running().find((o) => o.kind === "compare-preview" && o.archiveKey === k);
      const failed = k && !run && Ops.list.find((o) => o.kind === "compare-preview" && o.archiveKey === k)?.state === "failed" ? Ops.list.find((o) => o.archiveKey === k) : null;
      put(title, h("b", {}, side?.branch ?? "…"), side?.rev ? h("span", { class: "muted mono" }, `rev ${side.rev}`) : null, h("span", { class: "grow" }),
        showing ? h("span", { class: "chip" }, "showing") : null,
        side?.guid && canPreview(CM.path) && !run ? h("button", { class: "btn small ghost", onclick: () => { Archives.built.delete(k); v.clear(); preview(key, side, CM.path); } }, "Rebuild") : null);
      let over = null;
      if (!side || ["reading", "settling"].includes(side.state)) over = h("div", { class: "loading-line", style: "color:inherit;justify-content:center" }, h("span", { class: "spin" }), side?.state === "settling" ? "Waiting for the workbench…" : "Finding the asset…");
      else if (side.state === "no-workbench" || side.state === "no-branch") over = h("div", {}, "Pick a branch with a workbench, or create one, above.");
      else if (side.state === "missing") over = h("div", {}, h("b", {}, "Not on this branch."), h("div", {}, `${fileName(CM.path)} isn't in ${side.branch}'s workbench at this revision.`));
      else if (side.state === "error") over = h("div", {}, h("b", {}, "Couldn't read this side."), h("div", {}, side.error));
      else if (!canPreview(CM.path)) over = h("div", {}, h("b", {}, "No 3D preview for this file type."), h("div", {}, "The facts and the diff below compare it."));
      else if (run) { over = h("div", { style: "text-align:left" }, stepsEl(run)); }
      else if (failed) over = h("div", {}, h("b", {}, "The preview failed."), h("div", { style: "margin:6px 0" }, failed.error?.split("\n")[0]),
        h("button", { class: "btn small primary", onclick: () => preview(key, side, CM.path) }, "Retry"));
      else if (!showing) over = h("button", { class: "btn primary", onclick: () => preview(key, side, CM.path) }, "Build and preview");
      if (v.failed) over = h("div", {}, v.failed);
      overlay.hidden = !over;
      put(overlay, over);
    }
  }

  function renderBody() {
    if (!CM.built || !CM.a) return;
    const { a, b } = CM;
    const rows = [
      ["GUID", a.guid, b?.guid],
      ["Size", a.info?.size != null ? fmtBytes(a.info.size) : null, b?.info?.size != null ? fmtBytes(b.info.size) : null],
      ["File hash", a.info?.fileHash, b?.info?.fileHash],
      ["Meta hash", a.info?.metafileHash, b?.info?.metafileHash],
    ];
    const ready = a.state === "ready" && b?.state === "ready";
    const sameFile = ready && a.info?.fileHash && a.info.fileHash === b.info?.fileHash;
    const sameMeta = ready && a.info?.metafileHash && a.info.metafileHash === b.info?.metafileHash;
    let summary = null;
    if (ready) summary = h("div", { class: "callout " + (sameFile && sameMeta ? "ok" : "warn") }, h("span", { class: "dot " + (sameFile && sameMeta ? "ok" : "warn") }),
      h("div", {}, h("p", {}, h("b", {}, sameFile && sameMeta ? "Identical on both branches." : sameFile ? "Same content; the .meta differs." : "The file differs between the branches.")),
        a.guid !== b.guid ? h("p", {}, "The GUIDs differ: references to this asset won't carry over between the branches.") : null));
    else if (b?.state === "missing") summary = h("div", { class: "callout warn" }, h("span", { class: "dot warn" }), h("div", {}, h("p", {}, h("b", {}, `Only on ${a.branch}. `), `${b.branch} doesn't have ${fileName(CM.path)}.`)));
    else if (a.state === "missing") summary = h("div", { class: "callout warn" }, h("span", { class: "dot warn" }), h("div", {}, h("p", {}, h("b", {}, `Not on ${a.branch} at revision ${a.rev}.`))));
    const d = CM.diff;
    let diffBody = null;
    if (TEXTY.test(CM.path ?? "")) {
      if (!d) diffBody = ready ? h("button", { class: "btn small", onclick: () => diff(CM.path) }, "Compare the text") : h("div", { class: "muted small" }, "Waiting for both sides…");
      else if (d.op?.running) diffBody = h("div", { class: "loading-line" }, h("span", { class: "spin" }), `${d.op.activeStep?.label ?? "Comparing"}…`);
      else if (d.error) diffBody = h("div", { class: "callout bad" }, h("span", { class: "dot bad" }), h("div", {}, h("p", {}, d.error), h("div", { class: "actions" }, h("button", { class: "btn small", onclick: () => diff(CM.path) }, "Retry"))));
      else if (d.same) diffBody = h("div", { class: "muted small" }, "The text is the same on both branches.");
      else if (d.tooBig) diffBody = h("div", { class: "muted small" }, "The files are too big to diff here.");
      else diffBody = diffEl(d.lines);
    }
    put($("cmBody"), summary,
      h("div", { class: "section" }, h("h2", {}, "Facts"),
        h("table", { class: "cmp-facts" }, h("thead", {}, h("tr", {}, h("th", {}), h("th", {}, a.branch), h("th", {}, b?.branch ?? "…"))),
          h("tbody", {}, rows.map(([k, x, y]) => h("tr", { class: ready && x !== y ? "differs" : "" }, h("th", {}, k), h("td", {}, x ?? "—"), h("td", {}, y ?? (b?.state === "missing" ? "not there" : "…"))))))),
      diffBody ? h("div", { class: "section" }, h("h2", {}, `Text changes from ${a.branch} to ${b?.branch ?? "…"}`), diffBody) : null);
  }
})();
