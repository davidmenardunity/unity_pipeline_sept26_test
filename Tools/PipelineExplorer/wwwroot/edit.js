// Edit a project: open a prefab, scene, ScriptableObject or material from the workbench, edit the values
// of its objects, and save them as a new workbench revision. Edits replace only the characters of the
// values that changed (unityyaml.js), so the rest of the file is saved exactly as it was.
"use strict";

(() => {
  const EDITABLE = /\.(prefab|unity|asset|mat)$/i;
  const INTERNAL = new Set(["m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject",
    "serializedVersion", "m_Component", "m_Children", "m_Father", "m_EditorHideFlags", "m_EditorClassIdentifier"]);
  const BUILTIN_GUIDS = { "0000000000000000e000000000000000": "Built-in extra resources", "0000000000000000f000000000000000": "Built-in resources" };
  const Y = UnityYaml;

  const ED = {
    path: null, wb: null, rev: null, text: null, parsed: null, model: null,
    sel: null,              // the selected object node (from model)
    edits: new Map(),       // "line:start" → { line, start, end, text, old, label, doc }
    showInternal: readPref("edit.internal") ?? false,
    filter: "",
    message: "",
    loadOp: null, saveOp: null,
    reviewing: false,
  };
  const paths = new Map();  // guid → asset path (or null), resolved on demand

  const tree = fileTree($("edTree"), {
    show: (e) => e.isFolder || EDITABLE.test(e.path),
    onFile: (path) => openFile(path),
  });

  App.register({ id: "edit", name: "Edit a project", desc: "Change values in prefabs, scenes and ScriptableObjects, and save them as a new workbench revision." });

  App.on("revision", async ({ wb, revision }) => {
    $("edRev").textContent = revision ? `rev ${revision}` : "";
    await tree.open(wb, revision);
    if (!ED.path) return;
    if (!revision || wb !== ED.wb) { closeFile(); return; }
    if (revision === ED.rev) return;
    if (ED.edits.size) { renderInspector(); return; }   // keep the edits; the bar says the base moved
    openFile(ED.path, { keepSelection: true, force: true });
  });
  App.on("ops", () => { if (ED.saveOp || ED.loadOp) renderBar(); });

  // ── opening a file ───────────────────────────────────────────────────────

  function closeFile() {
    Object.assign(ED, { path: null, text: null, parsed: null, model: null, sel: null });
    ED.edits.clear();
    put($("edObjects"), h("div", { class: "insp-empty muted" }, "Pick a .prefab, .unity, .asset or .mat file. Its objects show here, and their values on the right."));
    put($("edInspector"), h("div", { class: "insp-empty muted" }, "Pick an object to edit its values."));
  }

  async function openFile(path, { keepSelection = false, force = false } = {}) {
    if (!force && path === ED.path && ED.rev === App.revision) return;
    if (ED.edits.size && path !== ED.path) {
      const ok = await ask({ title: `Discard ${ED.edits.size} unsaved change${ED.edits.size === 1 ? "" : "s"}?`,
        message: `Your edits to ${fileName(ED.path)} haven't been saved to the workbench.`, ok: "Discard", danger: true });
      if (!ok) { tree.select(ED.path); return; }
    }
    const keepId = keepSelection ? ED.sel?.doc.fileId : null;
    ED.loadOp?.stop(`You opened ${fileName(path)}.`);
    const wb = App.wb, rev = App.revision;
    Object.assign(ED, { path, wb, rev, text: null, parsed: null, model: null, sel: null, reviewing: false, message: "" });
    ED.edits.clear();
    tree.select(path);
    const op = ED.loadOp = Ops.start({ title: `Open ${fileName(path)}`, kind: "edit-open", asset: path,
      steps: [{ id: "get", label: "Download the file" }, { id: "parse", label: "Read its objects" }] });
    put($("edObjects"), h("div", { class: "insp-empty" }, h("div", { class: "loading-line" }, h("span", { class: "spin" }), `Downloading ${fileName(path)} at revision ${rev}…`)));
    put($("edInspector"));
    try {
      op.step("get", "active");
      const res = await call("GET", `${App.revUrl(wb, rev)}/file?path=${enc(path)}`, null, op);
      const text = await res.text();
      if (ED.path !== path || ED.loadOp !== op) return;
      op.step("get", "done", fmtBytes(text.length));
      op.step("parse", "active");
      if (!text.startsWith("%YAML")) throw new Error(`${fileName(path)} isn't saved as text (YAML). Set Asset Serialization to Force Text in the project to edit it here.`);
      const parsed = Y.parse(text);
      ED.text = text;
      ED.parsed = parsed;
      ED.model = Y.model(parsed);
      op.step("parse", "done", `${parsed.docs.length} objects`);
      op.done(`${parsed.docs.length} objects`);
      op.quiet = true;
      const all = flat();
      ED.sel = (keepId && all.find((n) => n.doc.fileId === keepId)) || ED.model.roots[0] || ED.model.others[0] || null;
      renderObjects();
      renderInspector();
    } catch (e) {
      if (ED.loadOp !== op) return;
      op.fail(e);
      put($("edObjects"), h("div", { class: "insp-empty" }, h("div", { class: "callout bad" }, h("span", { class: "dot bad" }),
        h("div", {}, h("p", {}, describeError(e)), h("div", { class: "actions" }, h("button", { class: "btn small", onclick: () => openFile(path, { force: true }) }, "Retry"))))));
    }
  }

  const flat = () => {
    const out = [];
    const walk = (n) => { out.push(n); n.children.forEach(walk); };
    ED.model.roots.forEach(walk);
    out.push(...ED.model.others);
    return out;
  };

  // ── the objects list ─────────────────────────────────────────────────────

  const KIND_LABEL = { go: "GO", instance: "PI", object: "OBJ" };
  const docsOf = (n) => n.kind === "go" ? [n.doc, ...(ED.model.componentsOf.get(n.doc.fileId) ?? [])] : [n.doc];
  const editedDocs = () => new Set([...ED.edits.values()].map((e) => e.doc));
  const isActive = (n) => n.kind !== "go" || Y.child(n.doc.root, "m_IsActive")?.raw.trim() !== "0";

  function renderObjects() {
    if (!ED.model) return;
    const edited = editedDocs();
    const f = ED.filter.toLowerCase();
    const matches = (n) => !f || n.label.toLowerCase().includes(f) || n.children.some(matches);
    const item = (n) => !matches(n) ? null : h("li", {},
      h("button", { class: "item" + (isActive(n) ? "" : " inactive"), type: "button", "aria-current": ED.sel === n ? "true" : null, title: `${n.doc.type} &${n.doc.fileId}`,
        onclick: () => { ED.sel = n; renderObjects(); renderInspector(); } },
        h("span", { class: "k" }, KIND_LABEL[n.kind] === "OBJ" ? n.doc.type.slice(0, 3).toUpperCase() : KIND_LABEL[n.kind]),
        h("span", { class: "n" }, n.label),
        docsOf(n).some((d) => edited.has(d.fileId)) ? h("span", { class: "dot warn edited", title: "Edited" }) : null),
      n.children.length ? h("ul", {}, n.children.map(item)) : null);
    const kindName = { prefab: "Prefab", unity: "Scene", asset: "Asset", mat: "Material" }[ext(ED.path)] ?? "File";
    put($("edObjects"),
      h("div", { class: "col-head" }, kindName, h("span", { class: "grow" }), h("span", { class: "plain mono" }, `${ED.parsed.docs.length} objects`)),
      h("div", { style: "padding:8px 16px 0" }, h("div", { class: "path" }, folderOf(ED.path) + "/", h("b", {}, fileName(ED.path))),
        h("input", { class: "filter", type: "search", placeholder: "Filter objects", value: ED.filter, style: "width:100%;margin-top:8px",
          oninput: (e) => { ED.filter = e.target.value; renderObjects(); const el = $("edObjects").querySelector("input.filter"); el.focus(); el.setSelectionRange(el.value.length, el.value.length); } })),
      h("ul", { class: "otree" },
        ED.model.roots.length ? [h("li", { class: "grp" }, ext(ED.path) === "unity" ? "Hierarchy" : "GameObjects"), ED.model.roots.map(item)] : null,
        ED.model.others.length ? [h("li", { class: "grp" }, ED.model.roots.length ? "Other objects" : "Objects"), ED.model.others.map(item)] : null));
  }

  // ── the values panel ─────────────────────────────────────────────────────

  function renderInspector() {
    const panel = $("edInspector");
    if (!ED.model) return;
    const n = ED.sel;
    const bar = h("div", { class: "changebar", id: "edBar" });
    const body = h("div", { class: "values" });
    if (!n) put(body, h("div", { class: "insp-empty muted" }, "This file has no objects to edit."));
    else if (n.kind === "go") put(body, goSection(n), (ED.model.componentsOf.get(n.doc.fileId) ?? []).map((d) => docSection(d)));
    else if (n.kind === "instance") put(body, instanceSection(n));
    else put(body, docSection(n.doc, true));
    put(panel, bar, ED.reviewing ? reviewSection() : null, body);
    renderBar();
  }

  function goSection(n) {
    const root = n.doc.root;
    const keys = ["m_Name", "m_IsActive", "m_TagString", "m_Layer", "m_StaticEditorFlags"];
    const fields = keys.map((k) => Y.child(root, k)).filter(Boolean).map((c) => field(c, n.doc, []));
    if (ED.showInternal) fields.push(...root.children.filter((c) => !keys.includes(c.key)).map((c) => field(c, n.doc, [])));
    return h("details", { class: "comp", open: true },
      h("summary", {}, "GameObject", h("span", { class: "muted mono small" }, `&${n.doc.fileId}`)),
      h("div", { class: "fields" }, fields));
  }

  function docSection(doc, open = true) {
    const title = doc.classId === Y.CLASS.MonoBehaviour ? scriptTitle(doc) : doc.type;
    const fields = doc.root.children.filter((c) => ED.showInternal || !INTERNAL.has(c.key)).map((c) => field(c, doc, []));
    return h("details", { class: "comp", open: open ? true : null },
      h("summary", {}, title, h("span", { class: "muted mono small" }, `${doc.type !== title ? doc.type + " " : ""}&${doc.fileId}`), doc.stripped ? h("span", { class: "muted small" }, "(stripped)") : null),
      h("div", { class: "fields" }, fields.length ? fields : h("div", { class: "muted small" }, "No values.")));
  }

  // A MonoBehaviour is titled by its script, whose path is resolved from the script's GUID.
  function scriptTitle(doc) {
    const r = Y.ref(Y.child(doc.root, "m_Script"));
    const known = r?.guid ? paths.get(r.guid) : undefined;
    if (r?.guid && known === undefined) resolve(r.guid).then(() => renderInspector());
    const name = known ? fileName(known).replace(/\.cs$/, "") : null;
    return name ?? (scalar(Y.child(doc.root, "m_Name")) || "MonoBehaviour");
  }

  function instanceSection(n) {
    const mod = Y.child(n.doc.root, "m_Modification");
    const src = Y.ref(Y.child(n.doc.root, "m_SourcePrefab"));
    const mods = Y.child(mod, "m_Modifications")?.children ?? [];
    return [
      h("details", { class: "comp", open: true }, h("summary", {}, "Prefab instance", h("span", { class: "muted mono small" }, `&${n.doc.fileId}`)),
        h("div", { class: "fields" },
          h("div", { class: "f" }, h("label", {}, "Source prefab"), h("div", { class: "v" }, refEl(src))),
          h("div", { class: "f" }, h("label", {}, "Parent"), h("div", { class: "v" }, refEl(Y.ref(Y.child(mod, "m_TransformParent"))))))),
      h("details", { class: "comp", open: true }, h("summary", {}, "Overrides", h("span", { class: "muted small" }, `${mods.length} values changed from the source prefab`)),
        h("div", { class: "fields mods" }, mods.length ? mods.map((m) => {
          const prop = scalar(Y.child(m, "propertyPath"));
          const value = Y.child(m, "value");
          const objRef = Y.ref(Y.child(m, "objectReference"));
          const target = Y.ref(Y.child(m, "target"));
          return h("div", { class: "f", title: `target fileID ${target?.fileId}` },
            h("label", { title: prop }, prop),
            h("div", { class: "v" }, objRef && objRef.fileId !== "0" ? refEl(objRef) : value ? input(value, n.doc, [prop]) : h("span", { class: "ro" }, "—")));
        }) : h("div", { class: "muted small" }, "No overrides."))),
    ];
  }

  // ── one field ────────────────────────────────────────────────────────────

  const pretty = (key) => /^\d+$/.test(key) ? `Element ${key}` : key.replace(/^m_/, "").replace(/^_/, "").replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/^./, (c) => c.toUpperCase());
  const scalar = (nd) => Y.scalarText(nd?.raw);

  function field(nd, doc, trail) {
    const label = pretty(nd.key);
    const t = [...trail, nd.key];
    if (nd.kind === "seq" || (nd.kind === "block" && nd.children.length) || nd.kind === "map") {
      const many = nd.children.length;
      return h("details", { class: "group", open: trail.length === 0 && many <= 8 ? true : null },
        h("summary", {}, label, h("span", { class: "muted small" }, nd.kind === "seq" ? ` · ${many} item${many === 1 ? "" : "s"}` : "")),
        h("div", { class: "fields" }, nd.children.map((c) => {
          // A list item that is a one-key map (- _Color: {…}) reads better as that key.
          if (c.isItem && c.children.length === 1 && (c.children[0].kind === "scalar" || c.children[0].kind === "flow")) return field(c.children[0], doc, t);
          return field(c, doc, t);
        })));
    }
    return h("div", { class: "f", "data-key": keyOf(nd) },
      h("label", { title: [...t].join(".") }, label),
      h("div", { class: "v" }, input(nd, doc, t)));
  }

  const keyOf = (x) => `${x.line}:${x.start}`;
  const current = (x) => ED.edits.get(keyOf(x))?.text ?? x.raw;
  const NUM = /^-?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$/;
  // 0/1 values that are on/off switches (others, like m_CastShadows, are enums and stay numbers).
  const BOOL_KEY = /^m_(Is[A-Z]\w*|Enabled|Use[A-Z]\w*|Enable[A-Z]\w*|ReceiveShadows|DynamicOccludee|StaticShadowCaster|ConstrainProportionsScale)$|^_?\w*Enabled$/;

  function input(nd, doc, trail) {
    if (nd.kind === "block" && !nd.children.length) return h("span", { class: "ro" }, "(empty)");
    if (nd.readOnly || nd.kind === "flowseq") return h("span", { class: "ro", title: "This value spans several lines, so it's shown but not edited here." }, nd.raw.trim().slice(0, 120) || "—");
    if (nd.kind === "flow") {
      const r = Y.ref(nd);
      if (r) return refEl(r);
      const keys = nd.items.map((i) => i.key);
      if (keys.every((k) => "rgba".includes(k)) && keys.length >= 3) return colorInput(nd, doc, trail);
      return nd.items.map((it) => h("span", { class: "axis" }, h("span", {}, it.key), textInput(it, doc, [...trail, it.key], NUM.test(it.raw.trim()) ? "number" : "text")));
    }
    const v = nd.raw.trim();
    if ((v === "0" || v === "1") && BOOL_KEY.test(nd.key)) {
      return h("input", { type: "checkbox", checked: current(nd).trim() === "1" ? true : null, "aria-label": pretty(nd.key),
        onchange: (e) => setEdit(nd, doc, trail, e.target.checked ? "1" : "0", e.target) });
    }
    return textInput(nd, doc, trail, NUM.test(v) ? "number" : "text");
  }

  function textInput(x, doc, trail, type) {
    const shown = type === "number" ? current(x).trim() : Y.scalarText(current(x));
    const el = h("input", { type: "text", inputmode: type === "number" ? "decimal" : null, value: shown, spellcheck: "false", "aria-label": trail.join("."),
      onchange: (e) => {
        const v = e.target.value;
        if (type === "number") {
          if (!NUM.test(v.trim())) { e.target.setCustomValidity("Enter a number"); e.target.reportValidity(); return; }
          e.target.setCustomValidity("");
          setEdit(x, doc, trail, v.trim(), e.target);
        } else setEdit(x, doc, trail, Y.formatString(v, x.raw), e.target);
      } });
    if (ED.edits.has(keyOf(x))) queueMicrotask(() => el.closest(".f")?.classList.add("changed"));
    return el;
  }

  function colorInput(nd, doc, trail) {
    const item = (k) => nd.items.find((i) => i.key === k);
    const val = (k) => Number(current(item(k)) ?? 0);
    const hex = "#" + ["r", "g", "b"].map((k) => Math.round(Math.min(1, Math.max(0, val(k))) * 255).toString(16).padStart(2, "0")).join("");
    const picker = h("input", { type: "color", value: hex, "aria-label": `${trail.join(".")} colour`,
      onchange: (e) => {
        const c = e.target.value;
        ["r", "g", "b"].forEach((k, i) => setEdit(item(k), doc, [...trail, k], String(+(parseInt(c.slice(1 + i * 2, 3 + i * 2), 16) / 255).toFixed(7)), e.target));
        renderInspector();
      } });
    return [picker, ...nd.items.map((it) => h("span", { class: "axis" }, h("span", {}, it.key), textInput(it, doc, [...trail, it.key], "number")))];
  }

  function refEl(r) {
    if (!r || r.fileId === "0") return h("span", { class: "refchip" }, "None");
    if (!r.guid) {
      const target = ED.model.byId.get(r.fileId);
      const node = target && flat().find((n) => docsOf(n).includes(target));
      return h("button", { class: "refchip", type: "button", title: `fileID ${r.fileId}`, disabled: node ? null : true,
        onclick: () => { ED.sel = node; renderObjects(); renderInspector(); } },
        target ? `${(node ? node.label + " · " : "")}${target.type}` : `fileID ${r.fileId}`);
    }
    if (BUILTIN_GUIDS[r.guid]) return h("span", { class: "refchip", title: `fileID ${r.fileId}` }, `${BUILTIN_GUIDS[r.guid]} · ${r.fileId}`);
    const known = paths.get(r.guid);
    if (known === undefined) resolve(r.guid).then(() => renderInspector());
    return h("span", { class: "refchip", title: `guid ${r.guid}, fileID ${r.fileId}, type ${r.type}` },
      known ? fileName(known) : known === null ? `guid ${short(r.guid)}… (not in this workbench)` : `guid ${short(r.guid)}…`);
  }

  // Asset paths for referenced GUIDs, a few at a time, in the background.
  const resolving = new Map();
  function resolve(guid) {
    if (resolving.has(guid)) return resolving.get(guid);
    const p = getJson(`${App.revUrl(ED.wb, ED.rev)}/assets/${guid}`).then((a) => paths.set(guid, a.path ?? null), () => paths.set(guid, null));
    resolving.set(guid, p);
    return p;
  }

  // ── edits and saving ─────────────────────────────────────────────────────

  function setEdit(x, doc, trail, text, el) {
    const key = keyOf(x);
    if (text === x.raw) ED.edits.delete(key);
    else ED.edits.set(key, { line: x.line, start: x.start, end: x.end, text, old: x.raw, label: trail.join("."), doc: doc.fileId, docType: doc.type });
    el?.closest(".f")?.classList.toggle("changed", ED.edits.has(key));
    renderBar();
    renderObjects();
  }

  function renderBar() {
    const bar = $("edBar");
    if (!bar) return;
    const n = ED.edits.size;
    const op = ED.saveOp;
    bar.classList.toggle("dirty", n > 0 && !op?.running);
    const internal = h("label", { class: "switch", title: "Also show fields like m_ObjectHideFlags and serializedVersion" },
      h("input", { type: "checkbox", checked: ED.showInternal ? true : null, onchange: (e) => { ED.showInternal = e.target.checked; writePref("edit.internal", ED.showInternal); renderInspector(); } }), "Internal fields");
    if (op?.running) {
      put(bar, h("span", { class: "spin" }), h("b", {}, op.activeStep?.label ?? "Saving"), op.activeStep?.sub ? h("span", { class: "muted small" }, op.activeStep.sub) : null,
        h("span", { class: "grow" }), h("span", { class: "muted small num", "data-op": op.id }, secs(op.elapsed)));
      return;
    }
    const moved = ED.rev !== App.revision && App.wb === ED.wb && App.revision;
    if (!n) {
      put(bar, op?.state === "done" ? h("span", { class: "dot ok" }) : null,
        h("span", { class: "muted small" }, op?.state === "done" ? `Saved as revision ${ED.rev}. ` : "", "Edit a value, then save the file as a new workbench revision."),
        h("span", { class: "grow" }), internal);
      return;
    }
    const msg = h("input", { id: "edMessage", placeholder: `Edit ${fileName(ED.path)}: ${n} value${n === 1 ? "" : "s"}`, value: ED.message, oninput: (e) => (ED.message = e.target.value),
      onkeydown: (e) => e.key === "Enter" && save() });
    put(bar,
      h("b", {}, `${n} change${n === 1 ? "" : "s"}`),
      msg,
      h("button", { class: "btn small primary", onclick: save }, `Save as a new revision`),
      h("button", { class: "btn small", onclick: () => { ED.reviewing = !ED.reviewing; renderInspector(); } }, ED.reviewing ? "Hide changes" : "Review"),
      h("button", { class: "btn small ghost", onclick: revertAll }, "Revert all"),
      internal,
      op?.state === "failed" ? h("div", { class: "callout bad", style: "flex-basis:100%" }, h("span", { class: "dot bad" }), h("div", {}, h("p", {}, h("b", {}, "Not saved. "), op.error))) : null,
      moved ? h("div", { class: "callout warn", style: "flex-basis:100%" }, h("span", { class: "dot warn" }), h("div", {},
        h("p", {}, `The workbench is now at revision ${App.revision}; these edits are on revision ${ED.rev}. Saving writes this whole file, replacing any newer change to it.`))) : null);
  }

  function reviewSection() {
    const after = Y.apply(ED.parsed, [...ED.edits.values()]);
    const diff = lineDiff(ED.text, after);
    return h("div", { style: "padding:12px 16px", class: "section" },
      h("h2", {}, "Changes"),
      h("ul", { class: "list", style: "margin-bottom:10px" }, [...ED.edits.values()].map((e) => h("li", {},
        h("span", { class: "what" }, h("span", { class: "mono" }, e.label), h("small", {}, `${e.docType} &${e.doc} · line ${e.line + 1}`)),
        h("span", { class: "mono small" }, `${e.old.trim() || "''"} → ${e.text.trim() || "''"}`),
        h("button", { class: "btn small ghost", onclick: () => { ED.edits.delete(`${e.line}:${e.start}`); renderInspector(); renderObjects(); } }, "Revert")))),
      diff ? diffEl(diff, 2) : h("div", { class: "muted small" }, "The file is too big to show a diff."));
  }

  async function revertAll() {
    if (!(await ask({ title: `Revert ${ED.edits.size} change${ED.edits.size === 1 ? "" : "s"}?`, message: "The values go back to what the workbench has.", ok: "Revert", danger: true }))) return;
    ED.edits.clear();
    ED.reviewing = false;
    renderObjects();
    renderInspector();
  }

  async function save() {
    if (!ED.edits.size || ED.saveOp?.running) return;
    const path = ED.path, wb = ED.wb, base = ED.rev;
    const text = Y.apply(ED.parsed, [...ED.edits.values()]);
    const n = ED.edits.size;
    const message = ED.message.trim() || `Edit ${fileName(path)}: ${n} value${n === 1 ? "" : "s"}`;
    const op = ED.saveOp = Ops.start({ title: `Save ${fileName(path)} (${n} change${n === 1 ? "" : "s"})`, kind: "edit-save", asset: path,
      steps: [{ id: "upload", label: "Upload and commit" }, { id: "validating", label: "Validate the new revision" }, { id: "settled", label: "Ready to read" }, { id: "reload", label: "Reopen the file" }] });
    op.onShow = () => tree.select(path);
    renderBar();
    try {
      op.step("upload", "active", fmtBytes(text.length));
      const r = await postJson(`${App.revUrl(wb, base)}/save`, { files: [{ path, text }], message, branch: App.branchOf(wb) }, op);
      op.step("upload", "done", `revision ${r.revision}`);
      op.note(`Committed "${message}" as revision ${r.revision}. It validates before it can be read.`);
      const settled = await waitUntilSettled(wb, op, r.revision);
      op.step("reload", "active");
      ED.edits.clear();
      if (App.wb === wb) await openRevision(wb, settled);   // reopens this file at the new revision
      op.step("reload", "done");
      op.done(`Saved as revision ${settled}`);
    } catch (e) {
      op.fail(e);
    }
    renderBar();
  }
})();
