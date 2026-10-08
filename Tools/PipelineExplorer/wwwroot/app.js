// Pipeline Explorer: the shared frame. The top configuration (org, project, token, project service,
// branch, workbench, environment), the example tabs, operations with the status bar and activity drawer,
// and pieces the examples share (API calls, dialogs, the file tree). Talks only to the local server
// (/api/...), which holds the token and calls the pipeline.
"use strict";

const $ = (id) => document.getElementById(id);
const ROOTS = ["Assets", "Packages", "ProjectSettings"];

// ── helpers ─────────────────────────────────────────────────────────────────

function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props ?? {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === "class") el.className = v;
    else if (k.startsWith("on") && typeof v === "function") el.addEventListener(k.slice(2), v);
    else if (k === "value") el.value = v;
    else el.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children.flat(Infinity)) if (c !== null && c !== undefined && c !== false) el.append(c);
  return el;
}
// replaceChildren would print null/false as text: drop them.
const put = (el, ...kids) => el.replaceChildren(...kids.flat(Infinity).filter((k) => k !== null && k !== undefined && k !== false));

class ApiError extends Error {
  constructor(status, body) {
    super(body?.error || `HTTP ${status}`);
    this.status = status;
    this.body = body || {};
  }
}

// Every request names the operation it belongs to (X-Op), so the server can group the pipeline calls.
async function call(method, url, body, op) {
  const res = await fetch(url, {
    method,
    headers: { "X-Pipeline-Explorer": "1", ...(op ? { "X-Op": op.id } : {}), ...(body ? { "Content-Type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) {
    let parsed = null;
    try { parsed = await res.json(); } catch { /* not JSON */ }
    throw new ApiError(res.status, parsed);
  }
  return res;
}
const getJson = async (url, op) => (await call("GET", url, null, op)).json();
const postJson = async (url, body, op) => (await call("POST", url, body ?? {}, op)).json();
const enc = encodeURIComponent;
const short = (id) => (id ? String(id).slice(0, 8) : "");
const fmtBytes = (n) => n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`;
const secs = (ms) => ms < 10_000 ? `${(ms / 1000).toFixed(1)} s` : ms < 120_000 ? `${Math.round(ms / 1000)} s` : `${Math.floor(ms / 60000)} min ${Math.round(ms / 1000) % 60} s`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const fileName = (path) => path.split("/").pop();
const folderOf = (path) => path.split("/").slice(0, -1).join("/");
const ext = (path) => (/\.([^./]+)$/.exec(path)?.[1] ?? "").toLowerCase();
const KINDS = { prefab: "PRE", unity: "SCN", asset: "AST", mat: "MAT", fbx: "FBX", obj: "OBJ", png: "PNG", psd: "PSD", tga: "TGA", jpg: "JPG",
  cs: "CS", shader: "SHD", shadergraph: "SG", asmdef: "ASM", json: "JSN", txt: "TXT", meta: "META", anim: "ANI", controller: "CTL", xml: "XML" };
const kindOf = (path, isFolder) => isFolder ? "DIR" : KINDS[ext(path)] ?? (ext(path).slice(0, 3).toUpperCase() || "FILE");

function describeError(e) {
  const b = e?.body || {};
  const lines = [e?.message ?? String(e)];
  if (b.requestId && !lines[0].includes(b.requestId)) lines.push(`requestId: ${b.requestId}`);
  if (b.code === "revision_not_validated") lines.push("The project service is still producing this (common right after a cold start). Retry in a minute.");
  if (b.vpn) lines.push("Connect to the corporate VPN and retry.");
  return lines.join("\n");
}

// In-page stand-in for confirm()/prompt(), which the app's browser pane doesn't support.
// Resolves to true/false (confirm), or (when `value` is given) the trimmed text, "" for an empty OK, null when cancelled.
function ask({ title, message, value, placeholder, ok = "OK", danger = false, secret = false }) {
  return new Promise((resolve) => {
    const input = value !== undefined
      ? h("input", { value, placeholder: placeholder ?? "", spellcheck: "false",
          ...(secret ? { type: "password", autocomplete: "off", "data-1p-ignore": true, "data-lpignore": "true" } : {}) })
      : null;
    const cancel = () => done(input ? null : false);
    const done = (v) => { overlay.remove(); resolve(v); };
    const okBtn = h("button", { class: "btn primary" + (danger ? " danger" : ""), onclick: () => done(input ? input.value.trim() : true) }, ok);
    const overlay = h("div", { class: "overlay", onclick: (e) => e.target === overlay && cancel() },
      h("div", { class: "dialog", role: "dialog", "aria-modal": "true", "aria-label": title },
        h("h3", {}, title), message ? h("p", {}, message) : null, input,
        h("div", { class: "row-end" }, h("button", { class: "btn", onclick: cancel }, "Cancel"), okBtn)));
    overlay.addEventListener("keydown", (e) => {
      if (e.key === "Escape") cancel();
      else if (e.key === "Enter" && input) okBtn.click();
    });
    document.body.append(overlay);
    (input ?? okBtn).focus();
    input?.select();
  });
}

function banner(text, kind = "error", action = null) {
  const b = $("banner");
  b.hidden = !text;
  b.className = "banner" + (kind === "info" ? " info" : "");
  put(b, h("span", {}, text || ""), text && action ? h("button", { class: "btn small", onclick: action.onclick }, action.label) : null);
}

// Line diff (LCS) for small texts: [[" "|"+"|"-", line]].
function lineDiff(a, b, maxLines = 4000) {
  const x = a.split(/\r?\n/), y = b.split(/\r?\n/);
  if (x.length > maxLines || y.length > maxLines || x.length * y.length > 4e6) return null;
  const n = x.length, m = y.length, L = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) L[i][j] = x[i] === y[j] ? L[i + 1][j + 1] + 1 : Math.max(L[i + 1][j], L[i][j + 1]);
  const out = [];
  let i = 0, j = 0;
  while (i < n || j < m) {
    if (i < n && j < m && x[i] === y[j]) { out.push([" ", x[i]]); i++; j++; }
    else if (i < n && (j >= m || L[i + 1][j] >= L[i][j + 1])) out.push(["-", x[i++]]);
    else out.push(["+", y[j++]]);
  }
  return out;
}
// Changed lines with a few lines of context around each.
function diffEl(lines, context = 3) {
  const keep = new Set();
  lines.forEach(([t], i) => { if (t !== " ") for (let k = i - context; k <= i + context; k++) keep.add(k); });
  const rows = [];
  let gap = false;
  lines.forEach(([t, s], i) => {
    if (!keep.has(i)) { gap = true; return; }
    if (gap && rows.length) rows.push(h("div", { class: "hunk" }, "…"));
    gap = false;
    rows.push(h("div", { class: t === "+" ? "add" : t === "-" ? "del" : "" }, `${t} ${s}`));
  });
  return h("pre", { class: "diff" }, rows.length ? rows : h("div", { class: "hunk" }, "No differences."));
}

// ── operations: every action is one, with steps, notes and the pipeline calls it made ─────

const Ops = (() => {
  let seq = 0;
  const list = [];
  const listeners = new Set();
  let scheduled = false;
  const changed = () => {
    if (scheduled) return;
    scheduled = true;
    // A timer, not requestAnimationFrame: that one stalls while the tab isn't painting.
    setTimeout(() => { scheduled = false; listeners.forEach((f) => f()); }, 30);
  };

  class Op {
    constructor({ title, example, asset = null, kind = "", steps = [], background = false }) {
      this.id = `op${Date.now().toString(36)}${++seq}`;
      this.title = title;
      this.example = example ?? App.view;
      this.asset = asset;
      this.kind = kind;
      this.background = background;
      this.state = "running";
      this.started = Date.now();
      this.ended = null;
      this.notes = [];
      this.error = null;
      this.steps = steps.map((s) => ({ id: s.id, label: s.label, state: "pending", sub: "" }));
      list.unshift(this);
      if (list.length > 200) list.length = 200;
      changed();
    }
    get running() { return this.state === "running"; }
    step(id, state, sub) {
      let s = this.steps.find((x) => x.id === id);
      if (!s) { s = { id, label: id, state: "pending", sub: "" }; this.steps.push(s); }
      if (state === "active" && s.state !== "active") s.t0 = Date.now();
      if ((state === "done" || state === "failed") && !s.t1) { s.t0 ??= Date.now(); s.t1 = Date.now(); }
      s.state = state;
      if (sub !== undefined) s.sub = sub;
      changed();
      return this;
    }
    note(text) {
      if (this.notes[this.notes.length - 1] !== text) this.notes.push(text);
      if (this.notes.length > 30) this.notes.shift();
      changed();
      return this;
    }
    done(text) {
      if (!this.running) return this;
      this.state = "done";
      this.ended = Date.now();
      this.steps.forEach((s) => { if (s.state === "active") { s.state = "done"; s.t1 = Date.now(); } });
      if (text) this.result = text;
      changed();
      return this;
    }
    fail(e) {
      if (!this.running) return this;
      this.state = "failed";
      this.ended = Date.now();
      this.error = typeof e === "string" ? e : describeError(e);
      this.steps.forEach((s) => { if (s.state === "active") { s.state = "failed"; s.t1 = Date.now(); } });
      changed();
      return this;
    }
    stop(why) {
      if (!this.running) return this;
      this.state = "stopped";
      this.ended = Date.now();
      this.result = why;
      this.steps.forEach((s) => { if (s.state === "active") s.state = "pending"; });
      changed();
      return this;
    }
    get activeStep() { return this.steps.find((s) => s.state === "active") ?? this.steps.find((s) => s.state === "pending") ?? null; }
    get progress() {
      if (!this.steps.length) return null;
      const done = this.steps.filter((s) => s.state === "done").length + (this.steps.some((s) => s.state === "active") ? 0.5 : 0);
      return done / this.steps.length;
    }
    get elapsed() { return (this.ended ?? Date.now()) - this.started; }
    get calls() { return App.calls.filter((c) => c.op === this.id); }
    // Run fn(op) and finish the op with its result: done, or failed with the error (re-thrown).
    async run(fn) {
      try {
        const r = await fn(this);
        if (this.running) this.done();
        return r;
      } catch (e) {
        this.fail(e);
        throw e;
      }
    }
  }

  return {
    start: (opts) => new Op(opts),
    list,
    get: (id) => list.find((o) => o.id === id),
    running: () => list.filter((o) => o.running),
    last: () => list.find((o) => !o.running && !o.quiet),
    onChange: (f) => listeners.add(f),
    changed,
  };
})();

// Steps rendered in a viewer overlay or a panel.
function stepsEl(op) {
  return h("ol", { class: "steps" }, op.steps.map((s) => h("li", { class: s.state },
    h("span", { class: "ic" }, s.state === "done" ? "✓" : s.state === "failed" ? "✕" : s.state === "active" ? h("span", { class: "spin", style: "width:10px;height:10px" }) : "·"),
    h("span", {}, s.label, s.sub ? h("span", { class: "sub" }, ` · ${s.sub}`) : null),
    h("span", { class: "t" }, s.t1 && s.t0 ? secs(s.t1 - s.t0) : s.state === "active" && s.t0 ? h("span", { "data-since": s.t0 }, secs(Date.now() - s.t0)) : ""))));
}

// ── the app ─────────────────────────────────────────────────────────────────

const App = {
  view: "explorer",
  config: null,
  branch: null,
  branches: [],
  heads: {},
  repository: null,
  workbenches: [],
  wb: null,           // selected workbench id
  revision: null,     // settled revision every read is pinned to
  env: null,          // selected environment id
  calls: [],          // pipeline calls the server made (from /api/activity)
  lastCall: 0,
  selectedCall: null,
  examples: [],
  pollTimer: null,
  wbOp: null,         // the operation following the selected workbench
  listeners: {},

  on(event, fn) { (this.listeners[event] ??= []).push(fn); },
  emit(event, data) { for (const fn of this.listeners[event] ?? []) { try { fn(data); } catch (e) { console.error(e); } } },

  register(example) { this.examples.push(example); },

  revUrl(wb = this.wb, rev = this.revision) { return `/api/workbenches/${wb}/revisions/${enc(rev)}`; },
  branchOf(wb) { return this.workbenches.find((w) => w.workbenchId === wb)?.branchName || this.branch; },

  async boot() {
    wireConfig();
    wireStatus();
    renderExamples();
    showView(readPref("view") ?? "explorer");
    pollActivity();
    setInterval(tick, 500);
    await start();
  },
};

// Saved per browser: the org/project pick, the open example, custom examples.
function readPref(key) { try { return JSON.parse(localStorage.getItem(`pipeline-explorer.${key}`) || "null"); } catch { return null; } }
function writePref(key, value) { try { localStorage.setItem(`pipeline-explorer.${key}`, JSON.stringify(value)); } catch { /* private window */ } }

// ── config, token, org and project ──────────────────────────────────────────

// "token" when there's no usable token, "pick" when org/project are still to choose, "ok" otherwise.
async function loadConfig() {
  const c = App.config = await getJson("/api/config");
  const t = $("token");
  const from = c.tokenSource === "pasted" ? "saved in the app" : "from .env";
  if (!c.token) { t.textContent = "no token"; t.className = "pill bad"; }
  else if (c.token.isExpired) { t.textContent = `token expired (${from})`; t.className = "pill bad"; }
  else {
    const hours = c.token.expiresAt ? Math.round((new Date(c.token.expiresAt) - Date.now()) / 36e5) : null;
    t.textContent = `${c.token.kind}${hours !== null ? `, ${hours} h left` : ""} · ${from}`;
    t.className = "pill " + (hours !== null && hours < 4 ? "warn" : "ok");
  }
  if (c.missing.includes("UNITY_JWT") || c.token?.isExpired) {
    banner(c.token?.isExpired ? "The bearer token has expired. Paste a fresh one to continue." : "No bearer token yet. Paste one to start.", "error",
      { label: "Paste a token", onclick: pasteToken });
    return "token";
  }
  if (c.missing.length) {
    banner("Enter your org ID and press Load, then pick a project.", "info");
    return "pick";
  }
  banner(null);
  return "ok";
}

async function loadOrg(org, prefer) {
  const sel = $("projectSel");
  $("orgName").textContent = "…";
  put(sel, h("option", { value: "" }, "loading…"));
  try {
    const o = await getJson(`/api/orgs/${enc(org)}`);
    $("orgName").textContent = o.name ?? "";
  } catch (e) {
    $("orgName").textContent = e.status === 404 ? "org not found" : "couldn't load org";
    $("orgName").title = describeError(e);
    put(sel, h("option", { value: "" }, "(no projects)"), h("option", { value: "__enter" }, "Enter project ID…"));
    return;
  }
  let projects = [];
  try { projects = await getJson(`/api/orgs/${enc(org)}/projects`); } catch (e) { $("orgName").title = describeError(e); }
  const known = projects.some((p) => p.id === prefer);
  put(sel,
    prefer && known ? null : h("option", { value: "" }, projects.length ? "(pick a project)" : "(no projects)"),
    prefer && !known ? h("option", { value: prefer }, `${short(prefer)}… (not in the list)`) : null,
    projects.map((p) => h("option", { value: p.id, title: `${p.id}\ncreated ${new Date(p.createdAt).toLocaleString()}` }, `${p.name ?? "(unnamed)"} · ${short(p.id)}`)),
    h("option", { value: "__enter" }, "Enter project ID…"));
  sel.value = prefer ?? "";
}

async function onPickProject() {
  const sel = $("projectSel");
  let project = sel.value;
  if (project === "__enter") {
    project = await ask({ title: "Enter a project ID", message: "The Unity Cloud project's UUID.", value: App.config.projectId || "", placeholder: "00000000-0000-0000-0000-000000000000", ok: "Use project" });
    if (!project) { sel.value = App.config.projectId || ""; return; }
    if (![...sel.options].some((o) => o.value === project)) sel.prepend(h("option", { value: project }, `${short(project)}…`));
    sel.value = project;
  }
  if (!project) return;
  const org = $("org").value.trim();
  await postJson("/api/select", { organizationId: org, projectId: project });
  writePref("selection", { org, project });
  if ((await loadConfig()) === "ok") await openProject();
}

async function start() {
  if ((await loadConfig()) === "token") return;
  const saved = readPref("selection");
  const org = saved?.org || App.config.organizationId;
  const project = saved?.org ? saved.project : App.config.projectId;
  $("org").value = org ?? "";
  if (!org) return;
  await loadOrg(org, project);
  if (project && (org !== App.config.organizationId || project !== App.config.projectId)) {
    await postJson("/api/select", { organizationId: org, projectId: project });
    await loadConfig();
  }
  if (App.config.organizationId && App.config.projectId) await openProject();
}

async function openProject() {
  App.branch = App.config.branch;
  App.wb = null;
  App.workbenches = [];
  selectWorkbench(null);
  await loadBranches().catch((e) => banner(describeError(e)));
  loadService();
  await loadWorkbenches();
}

// The token goes to this app's server, which uses it for every pipeline call and saves it encrypted
// for this Windows user (outside the repo). The page never gets it back.
async function pasteToken() {
  const pasted = App.config?.tokenSource === "pasted";
  const token = await ask({
    title: "Bearer token",
    message: "Open the staging Unity Cloud dashboard, then devtools > Network, pick a request to " +
      "staging.services.api.unity.com and copy its Authorization header (\"Bearer \" is stripped). " +
      "The app saves it encrypted for your Windows account, outside the repo." +
      (pasted ? " Leave the field empty to forget the saved token and use the one in .env." : ""),
    value: "", placeholder: "eyJ…", ok: "Use token", secret: true,
  });
  if (token === null || (token === "" && !pasted)) return;
  try {
    if (token === "") await call("DELETE", "/api/token");
    else await postJson("/api/token", { token });
  } catch (e) {
    banner(describeError(e), "error", { label: "Try again", onclick: pasteToken });
    return;
  }
  App.wb = null;
  start();
}

// ── project service ─────────────────────────────────────────────────────────

async function loadService() {
  const pill = $("service");
  try {
    const s = await getJson(`/api/service?branch=${enc(App.branch)}`);
    pill.textContent = `project service: ${s.summary}`;
    pill.className = "pill " + (s.isReady ? "ok" : s.isStarting ? "warn" : "bad");
    pill.title = s.message ?? "";
    $("startService").hidden = s.isReady;
    return s;
  } catch (e) {
    pill.textContent = "project service: ?";
    pill.className = "pill bad";
    pill.title = describeError(e);
    return null;
  }
}

// Start the project service if needed and wait until it's ready, narrated on `op`'s "service" step.
async function waitForService(op) {
  op.step("service", "active", "checking");
  let s = await getJson(`/api/service?branch=${enc(App.branch)}`, op);
  if (!s.isReady && !s.isStarting) {
    op.note("The project service was stopped: asking it to start.");
    await postJson(`/api/service/start?branch=${enc(App.branch)}&wait=false`, {}, op);
  }
  const deadline = Date.now() + 6 * 60_000;
  while (!s.isReady) {
    if (!op.running) return false;
    if (Date.now() > deadline) throw new Error(`The project service isn't ready after 6 minutes (last: ${s.summary}).`);
    const steps = (s.steps ?? []).map((st) => `${st.name} ${st.status}`).join(", ");
    op.step("service", "active", s.isStarting ? "starting (a cold start takes 1–2 min)" : s.status ?? "");
    op.note(`${s.summary}${steps ? `: ${steps}` : ""}`);
    await sleep(3000);
    s = await getJson(`/api/service?branch=${enc(App.branch)}`, op);
  }
  op.step("service", "done", "ready");
  loadService();
  return true;
}

async function startService() {
  const op = Ops.start({ title: "Start the project service", kind: "service", steps: [{ id: "service", label: "Project service" }] });
  $("startService").disabled = true;
  try { await op.run(waitForService); } catch { /* shown on the op */ }
  $("startService").disabled = false;
  loadService();
}

// ── branches and workbenches ────────────────────────────────────────────────

async function loadBranches() {
  const r = await getJson("/api/branches");
  App.branches = r.branches;
  App.heads = r.heads ?? {};
  App.repository = r.repository;
  App.branch ??= App.config.branch;
  const sel = $("branch");
  put(sel, r.branches.map((b) => h("option", { value: b }, b)), h("option", { value: "__other" }, "Other…"));
  if (!r.branches.includes(App.branch)) sel.prepend(h("option", { value: App.branch }, App.branch));
  sel.value = App.branch;
  sel.title = r.gitError ? `Couldn't list the repo's branches: ${r.gitError}` : `Branches of ${r.repository ?? "the repo"}`;
  App.emit("branches");
}

async function loadWorkbenches(preferId) {
  try {
    App.workbenches = await getJson("/api/workbenches");
  } catch (e) {
    App.workbenches = [];
    // The project service answers for the workbenches: when it's down, follow its start, then list again.
    const s = e.body?.code === "status_authority_unavailable" || e.status === 503 ? await loadService() : null;
    if (s && !s.isReady) {
      if (s.isStarting) followServiceThenReload(preferId);
      else banner("The project service is stopped, so the workbenches can't be listed.", "error", { label: "Start the project service", onclick: () => { banner(null); followServiceThenReload(preferId); } });
    } else banner(describeError(e));
  }
  App.emit("workbenches");
  renderWorkbenchPicker(preferId);
}

let serviceOp = null;
async function followServiceThenReload(preferId) {
  if (serviceOp?.running) return;
  banner(null);
  readinessPill("waiting for the project service", "busy");
  const op = serviceOp = Ops.start({ title: "Start the project service", kind: "service",
    steps: [{ id: "service", label: "Project service" }, { id: "list", label: "List the workbenches" }] });
  try {
    await op.run(async () => {
      if (!(await waitForService(op))) return;
      op.step("list", "active");
      await loadWorkbenches(preferId);
      op.step("list", "done", `${App.workbenches.length} workbench${App.workbenches.length === 1 ? "" : "es"}`);
    });
  } catch { /* on the op */ }
}

const behindHead = (w, branch) => {
  const head = App.heads?.[branch ?? w.branchName];
  return head && w.upstreamRevision && head !== w.upstreamRevision ? head : null;
};
const workbenchLabel = (w) => `${short(w.workbenchId)} · ${w.upstreamRevision ? "@" + w.upstreamRevision.slice(0, 7) : ""}` +
  (w.createdAt ? ` · ${new Date(w.createdAt).toLocaleDateString()}` : "") + (behindHead(w) ? ` · behind ${App.heads[w.branchName].slice(0, 7)}` : "");
const workbenchesOn = (branch) => App.workbenches.filter((w) => (w.branchName || App.config.branch) === branch)
  .sort((a, b) => new Date(b.createdAt ?? 0) - new Date(a.createdAt ?? 0));

function renderWorkbenchPicker(preferId) {
  const onBranch = workbenchesOn(App.branch);
  const sel = $("workbench");
  put(sel,
    onBranch.length ? null : h("option", { value: "" }, "(no workbench on this branch)"),
    onBranch.map((w) => h("option", { value: w.workbenchId, title: `${w.upstreamRepository ?? ""} ${w.branchName ?? ""}` }, workbenchLabel(w))));
  const pick = onBranch.find((w) => w.workbenchId === preferId)
    ?? onBranch.find((w) => w.workbenchId === App.wb)
    ?? onBranch.find((w) => w.workbenchId === App.config.workbenchId)
    ?? onBranch[0];
  sel.value = pick?.workbenchId ?? "";
  selectWorkbench(pick?.workbenchId ?? null);
}

async function changeBranch() {
  let b = $("branch").value;
  if (b === "__other") {
    b = await ask({ title: "Another branch", message: "The branch must exist in the git repo.", value: App.branch, ok: "Switch" });
    if (!b) { $("branch").value = App.branch; return; }
    if (![...$("branch").options].some((o) => o.value === b)) $("branch").prepend(h("option", { value: b }, b));
    $("branch").value = b;
  }
  App.branch = b;
  loadService();
  renderWorkbenchPicker();
}

async function newWorkbench() {
  const repository = await ask({
    title: `New workbench on ${App.branch}`,
    message: "The public git repo it tracks. The workbench starts from the branch's latest commit and doesn't follow later pushes.",
    value: App.repository ?? "", placeholder: "https://github.com/owner/repo", ok: "Create",
  });
  if (repository) await createWorkbench(App.branch, repository);
}

/**
 * Create a workbench on `branch` and follow it until it's ready to read. Returns its id.
 * select: make it the selected workbench (the Compare example creates one for its other side without).
 */
async function createWorkbench(branch, repository = App.repository, { select = true, replacing = null, example } = {}) {
  const op = Ops.start({ title: `Create a workbench on ${branch}`, kind: "workbench", example,
    steps: [{ id: "service", label: "Project service" }, { id: "created", label: "Create the workbench" }, { id: "validating", label: "Import and validate" }, { id: "settled", label: "Ready to read" }] });
  if (select) { $("newWorkbench").disabled = true; selectWorkbench(null); }
  try {
    if (!(await waitForService(op))) return null;
    const before = new Set(App.workbenches.map((w) => w.workbenchId));
    if (replacing) {
      op.step("created", "active", `deleting ${short(replacing)}`);
      await call("DELETE", `/api/workbenches/${replacing}`, null, op);
      before.delete(replacing);
    }
    op.step("created", "active", "creating");
    const wb = await postJson("/api/workbenches", { branch, repository }, op);
    if (before.has(wb.workbenchId)) {
      // The pipeline handed back a workbench we already had: nothing new was made.
      const from = wb.upstreamRevision?.slice(0, 7), head = App.heads?.[branch];
      op.step("created", "failed", `got ${short(wb.workbenchId)} back`);
      op.retry = { label: `Delete ${short(wb.workbenchId)} and create a new one`, run: () => createWorkbench(branch, repository, { select, replacing: wb.workbenchId, example }) };
      op.fail(`Pipeline handed back the existing workbench ${short(wb.workbenchId)}${from ? `, made from ${from}` : ""}, instead of creating one` +
        `${head && from && !head.startsWith(from) ? `. ${branch} is now at ${head.slice(0, 7)}` : ""}.`);
      await loadWorkbenches(select ? wb.workbenchId : App.wb);
      return wb.workbenchId;
    }
    op.step("created", "done", short(wb.workbenchId));
    op.note(`Workbench ${wb.workbenchId} created from ${wb.upstreamRevision?.slice(0, 7) ?? "?"}.`);
    if (select) {
      App.wbOp = op;   // the selected workbench's poll carries on in this op
      await loadWorkbenches(wb.workbenchId);
    } else {
      await loadWorkbenches(App.wb);
      await waitUntilSettled(wb.workbenchId, op);
      op.done();
    }
    return wb.workbenchId;
  } catch (e) {
    op.fail(e);
    return null;
  } finally {
    $("newWorkbench").disabled = false;
  }
}

async function deleteWorkbench() {
  const id = App.wb;
  if (!id) return;
  const ok = await ask({
    title: `Delete workbench ${short(id)}?`,
    message: "Its environments go with it, and so do any changes made through Pipeline that weren't published to git.",
    ok: "Delete", danger: true,
  });
  if (!ok) return;
  const op = Ops.start({ title: `Delete workbench ${short(id)}`, kind: "workbench" });
  try {
    await op.run((o) => call("DELETE", `/api/workbenches/${id}`, null, o));
    App.wb = null;
    await loadWorkbenches();
  } catch { /* on the op */ }
}

function selectWorkbench(id) {
  clearTimeout(App.pollTimer);
  if (id === App.wb && App.revision) return;
  if (App.wbOp && App.wbOp.running && App.wbOp.wb && App.wbOp.wb !== id) App.wbOp.stop("You picked another workbench.");
  App.wb = id;
  App.revision = null;
  App.env = null;
  put($("environment"));
  $("deleteWorkbench").disabled = !id;
  readinessPill(id ? "checking…" : "no workbench", id ? "busy" : "");
  App.emit("revision", { wb: id, revision: null });
  if (id) pollWorkbench(id, true);
}

function readinessPill(text, cls, title = "") {
  const p = $("readiness");
  p.textContent = text;
  p.className = "pill " + cls;
  p.title = title;
}

// Follow the selected workbench until it settles (validated), then read at that revision.
async function pollWorkbench(id, first) {
  if (App.wb !== id) return;
  let again = true;
  let op = App.wbOp?.running && (App.wbOp.wb === id || !App.wbOp.wb) ? App.wbOp : null;
  try {
    const { workbench, readiness, readinessError } = await getJson(`/api/workbenches/${id}`, op);
    if (App.wb !== id) return;
    const v = workbench.validation, r = readiness?.readiness;
    const settled = r === "settled" && readiness.settledRevision;
    if (!settled && !op && v?.status !== "failed") {
      // Picked while settling: follow it in an operation.
      op = App.wbOp = Ops.start({ title: `Workbench ${short(id)} is settling`, kind: "workbench",
        steps: [{ id: "validating", label: "Import and validate" }, { id: "settled", label: "Ready to read" }] });
    }
    if (op) op.wb = id;
    readinessPill(settled ? `ready · revision ${readiness.settledRevision}` : v?.status === "failed" ? "validation failed" : `${r ?? "unknown"}${v?.status ? ` · ${v.status}` : ""}`,
      settled ? "ok" : v?.status === "failed" ? "bad" : "warn", readinessError ?? "");
    if (op) {
      op.step("validating", settled ? "done" : "active", [v?.status, readiness?.head ? `revision ${readiness.head}` : null].filter(Boolean).join(", "));
      if (!settled) op.note(!readiness?.settledRevision ? "The first validation of a workbench takes 2–3 minutes." : "A change validates in about 40 seconds.");
      if (readinessError) op.note(`Readiness isn't answering: ${readinessError}`);
    }
    if (v?.status === "failed") {
      again = false;
      const msg = `${v.error?.category ?? "validation failed"}: ${v.error?.message ?? ""}` +
        (v.error?.message?.includes("Broken pipe") ? "\nKnown cause: Unity 6.6+ projects. Use 6.5 or older." : "");
      if (op) op.fail(msg); else banner(`Workbench ${short(id)} failed validation. ${msg}`);
    } else if (settled) {
      again = false;
      if (op) { op.step("settled", "done", `revision ${readiness.settledRevision}`); op.done(`Ready at revision ${readiness.settledRevision}`); }
      await openRevision(id, readiness.settledRevision);
    } else if (r === "gone") {
      again = false;
      op?.fail("The project service no longer has this workbench. Start the service, or create a new workbench.");
    }
  } catch (e) {
    readinessPill("error", "bad", describeError(e));
    op?.note(`error: ${e.body?.code ?? e.message}`);
    if (e.body?.code === "status_authority_unavailable") await loadService();
  }
  if (again && App.wb === id) App.pollTimer = setTimeout(() => pollWorkbench(id, false), 5000);
}

/** Wait until `wb` has settled (at or after `atLeast`, if given), narrating on `op`. Returns the settled revision. */
async function waitUntilSettled(wb, op, atLeast = null) {
  const newer = (a, b) => (/^\d+$/.test(a) && /^\d+$/.test(b) ? Number(a) >= Number(b) : a === b);
  const deadline = Date.now() + 20 * 60_000;
  while (op.running) {
    const { workbench, readiness, readinessError } = await getJson(`/api/workbenches/${wb}`, op);
    const v = workbench.validation, r = readiness?.readiness;
    if (v?.status === "failed") throw new Error(`Validation failed: ${v.error?.category ?? ""} ${v.error?.message ?? ""}`.trim());
    if (r === "settled" && readiness.settledRevision && (!atLeast || newer(readiness.settledRevision, atLeast))) {
      op.step("validating", "done", v?.status ?? "");
      op.step("settled", "done", `revision ${readiness.settledRevision}`);
      return readiness.settledRevision;
    }
    if (r === "gone") throw new Error("The project service no longer has this workbench.");
    op.step("validating", "active", [v?.status, readiness?.head ? `revision ${readiness.head}` : null].filter(Boolean).join(", "));
    if (readinessError) op.note(`Readiness isn't answering: ${readinessError}`);
    if (Date.now() > deadline) throw new Error("The workbench didn't settle within 20 minutes.");
    await sleep(4000);
  }
  return null;
}

async function openRevision(wb, revision) {
  App.revision = revision;
  App.emit("revision", { wb, revision });
  await loadEnvironments(App.env);
}

async function loadEnvironments(preferId) {
  const sel = $("environment");
  try {
    const envs = await getJson(`/api/workbenches/${App.wb}/environments`);
    App.environments = envs;
    put(sel,
      envs.length ? null : h("option", { value: "" }, "(none yet)"),
      envs.map((e) => h("option", { value: e.environmentId }, `${e.platform ?? "?"} · ${short(e.environmentId)}`)));
    const pick = envs.find((e) => e.environmentId === preferId)
      ?? envs.find((e) => e.environmentId === App.config.environmentId)
      ?? envs.find((e) => e.platform === App.config.platform)
      ?? envs[0];
    sel.value = pick?.environmentId ?? "";
    App.env = pick?.environmentId ?? null;
  } catch (e) {
    put(sel, h("option", { value: "" }, "(couldn't list)"));
    sel.title = describeError(e);
  }
}

async function addEnvironment() {
  if (!App.wb) return;
  const platform = $("platform").value;
  const op = Ops.start({ title: `Add a ${platform} environment`, kind: "environment" });
  try {
    const env = await op.run((o) => postJson(`/api/workbenches/${App.wb}/environments`, { platform }, o));
    await loadEnvironments(env.environmentId);
  } catch { /* on the op */ }
}

function wireConfig() {
  $("loadOrg").addEventListener("click", () => { const org = $("org").value.trim(); if (org) loadOrg(org, org === App.config?.organizationId ? App.config.projectId : null); });
  $("org").addEventListener("keydown", (e) => e.key === "Enter" && $("loadOrg").click());
  $("projectSel").addEventListener("change", onPickProject);
  $("branch").addEventListener("change", changeBranch);
  $("workbench").addEventListener("change", (e) => selectWorkbench(e.target.value || null));
  $("environment").addEventListener("change", (e) => (App.env = e.target.value || null));
  $("newWorkbench").addEventListener("click", newWorkbench);
  $("deleteWorkbench").addEventListener("click", deleteWorkbench);
  $("addEnvironment").addEventListener("click", addEnvironment);
  $("startService").addEventListener("click", startService);
  $("service").addEventListener("click", loadService);
  $("token").addEventListener("click", pasteToken);
  $("changeToken").addEventListener("click", pasteToken);
  $("reload").addEventListener("click", async () => { await call("POST", "/api/config/reload"); App.wb = null; start(); });
}

// ── the file tree (shared by the examples) ──────────────────────────────────

/**
 * A lazily loaded tree of the selected workbench's revision.
 * opts: { show(entry) → bool, onFile(path, item), onFolder(path, item), onDrop(folder, files), dimFile(path) → bool }
 */
function fileTree(ul, opts = {}) {
  let rev = null, wb = null, current = null;
  const show = opts.show ?? (() => true);

  function node(path, isFolder) {
    const name = fileName(path);
    const isMeta = name.endsWith(".meta");
    const item = h("button", { class: "item" + (isMeta ? " meta" : "") + (!isFolder && opts.dimFile?.(path) ? " dim" : ""), type: "button", title: path },
      h("span", { class: "tw" }, isFolder ? "▸" : ""), name);
    const li = h("li", { "data-path": path, "data-folder": isFolder ? "1" : null }, item);
    let loaded = false;
    li.expand = async () => {
      if (loaded) return;
      loaded = true;
      item.querySelector(".tw").textContent = "▾";
      const sub = h("ul", {}, h("li", { class: "note" }, h("span", { class: "spin" }), " loading…"));
      li.append(sub);
      try {
        const entries = await children(path);
        put(sub, entries.length ? entries.map((e) => node(e.path, e.isFolder)) : h("li", { class: "note" }, "(empty)"));
      } catch (e) {
        put(sub, h("li", { class: "note", title: describeError(e) }, `couldn't list: ${e.body?.code ?? e.status ?? e.message}`));
        loaded = false;
      }
    };
    li.collapse = () => { item.querySelector(".tw").textContent = "▸"; li.querySelector("ul")?.remove(); loaded = false; };
    item.addEventListener("click", () => {
      if (isFolder) {
        if (opts.onFolder) { select(path); opts.onFolder(path, item); if (!loaded) li.expand(); }
        else loaded ? li.collapse() : li.expand();
      } else { select(path); opts.onFile?.(path, item); }
    });
    item.querySelector(".tw").addEventListener("click", (e) => { if (isFolder && loaded && opts.onFolder) { e.stopPropagation(); li.collapse(); } });
    if (opts.onDrop) dropTarget(item, isFolder ? path : folderOf(path), opts.onDrop);
    return li;
  }

  async function children(path) {
    const tree = await getJson(`${App.revUrl(wb, rev)}/tree?path=${enc(path)}`);
    return tree.entries
      .map((e) => ({ path: e.path.replace(/^\/+/, ""), isFolder: e.isFolder }))
      .filter((e) => e.path !== path && show(e))
      .sort((a, b) => (a.isFolder !== b.isFolder ? (a.isFolder ? -1 : 1) : a.path.localeCompare(b.path)));
  }

  function select(path) {
    current = path;
    for (const el of ul.querySelectorAll(".item[aria-current]")) el.removeAttribute("aria-current");
    ul.querySelector(`li[data-path="${CSS.escape(path)}"] > .item`)?.setAttribute("aria-current", "true");
  }

  // Open each folder on the way to `path`.
  async function reveal(path) {
    const parts = path.split("/");
    for (let i = 1; i <= parts.length; i++) {
      const li = ul.querySelector(`li[data-path="${CSS.escape(parts.slice(0, i).join("/"))}"]`);
      if (!li) return null;
      if (li.dataset.folder && i < parts.length) await li.expand();
      if (i === parts.length) return li;
    }
    return null;
  }
  const openFolders = () => [...ul.querySelectorAll("li[data-folder] > ul")].map((u) => u.parentElement.dataset.path);

  return {
    async open(workbench, revision) {
      const keep = wb === workbench ? openFolders() : [];
      const sel = wb === workbench ? current : null;
      wb = workbench; rev = revision;
      if (!wb || !rev) { put(ul, h("li", { class: "note" }, wb ? "Waiting for the workbench to be ready to read…" : "Pick a workbench at the top.")); return; }
      put(ul, ROOTS.map((r) => node(r, true)));
      for (const f of keep) await reveal(f).then((li) => li?.expand?.());
      if (sel) { await reveal(sel); select(sel); }
    },
    reveal, select, children,
    get current() { return current; },
  };
}

// Files dropped on `el` are added to `folder`.
function dropTarget(el, folder, onDrop) {
  el.addEventListener("dragover", (e) => {
    if (!e.dataTransfer.types.includes("Files") || !App.revision) return;
    e.preventDefault();
    e.stopPropagation();
    e.dataTransfer.dropEffect = "copy";
    el.classList.add("drop");
  });
  el.addEventListener("dragleave", () => el.classList.remove("drop"));
  el.addEventListener("drop", (e) => {
    e.preventDefault();
    e.stopPropagation();
    el.classList.remove("drop");
    if ([...(e.dataTransfer.items ?? [])].some((i) => i.webkitGetAsEntry?.()?.isDirectory)) {
      banner("Folders can't be dropped yet: drop the files themselves.", "info");
      return;
    }
    const files = [...e.dataTransfer.files];
    if (files.length) onDrop(typeof folder === "function" ? folder() : folder, files);
  });
}

// Upload files into `folder` as one new revision, then wait until it's readable. Returns the revision.
async function uploadFiles(folder, files, example) {
  const wb = App.wb, base = App.revision;
  if (!wb || !base) return null;
  const total = files.reduce((n, f) => n + f.size, 0);
  const op = Ops.start({ title: `Add ${files.length === 1 ? files[0].name : `${files.length} files`} to ${folder}`, kind: "upload", example,
    steps: [{ id: "upload", label: "Upload and commit" }, { id: "validating", label: "Validate the new revision" }, { id: "settled", label: "Ready to read" }] });
  try {
    return await op.run(async () => {
      op.step("upload", "active", fmtBytes(total));
      const form = new FormData();
      for (const f of files) form.append("files", f, f.name);
      const res = await fetch(`${App.revUrl(wb, base)}/files?folder=${enc(folder)}&branch=${enc(App.branchOf(wb))}`,
        { method: "POST", headers: { "X-Pipeline-Explorer": "1", "X-Op": op.id }, body: form });
      const body = await res.json().catch(() => null);
      if (!res.ok) throw new ApiError(res.status, body);
      op.step("upload", "done", `revision ${body.revision}`);
      const settled = await waitUntilSettled(wb, op, body.revision);
      if (App.wb === wb) await openRevision(wb, settled);
      op.done(`Added at revision ${settled}`);
      return { revision: settled, paths: body.paths };
    });
  } catch { return null; }
}

// ── example tabs ────────────────────────────────────────────────────────────

function customExamples() { return readPref("examples") ?? []; }

function renderExamples() {
  const all = [...App.examples, ...customExamples().map((c) => ({ ...c, custom: true }))];
  const busy = (id) => Ops.running().some((o) => o.example === id);
  const cur = all.find((e) => e.id === App.view) ?? all[0];
  put($("examples"),
    all.map((e) => h("div", { class: "ex", role: "tab", tabindex: "0", "aria-selected": e.id === App.view ? "true" : "false", title: e.desc || e.name,
      onclick: (ev) => { if (!ev.target.closest(".x")) showView(e.id); },
      onkeydown: (ev) => { if (ev.key === "Enter" || ev.key === " ") { ev.preventDefault(); showView(e.id); } } },
      e.name, busy(e.id) ? h("span", { class: "spin", title: "Something is running in this example" }) : null,
      e.custom ? h("button", { class: "x", type: "button", "aria-label": `Remove ${e.name}`, title: "Remove this example",
        onclick: () => { writePref("examples", customExamples().filter((c) => c.id !== e.id)); if (App.view === e.id) showView("explorer"); else renderExamples(); } }, "×") : null)),
    h("button", { class: "ex ex-add", type: "button", onclick: openNewExample }, "+ New example"),
    h("span", { class: "ex-desc" }, cur?.desc ?? ""));
}

function showView(id) {
  const builtin = App.examples.find((e) => e.id === id);
  const custom = customExamples().find((c) => c.id === id);
  if (!builtin && !custom) id = "explorer";
  App.view = id;
  writePref("view", id);
  for (const e of App.examples) $(`view-${e.id}`).hidden = e.id !== id;
  $("view-custom").hidden = !custom;
  if (custom) renderCustom(custom);
  App.examples.find((e) => e.id === id)?.shown?.();
  renderExamples();
  renderStatus();
}

function openNewExample() {
  $("newex").hidden = false;
  $("exNewErr").hidden = true;
  $("exNewName").value = "";
  $("exNewDesc").value = "";
  $("exNewName").focus();
}
function createExample() {
  const name = $("exNewName").value.trim();
  if (!name) { $("exNewErr").hidden = false; $("exNewName").focus(); return; }
  const ex = { id: `c-${Date.now().toString(36)}`, name, desc: $("exNewDesc").value.trim() };
  writePref("examples", [...customExamples(), ex]);
  $("newex").hidden = true;
  showView(ex.id);
}
function renderCustom(ex) {
  const blk = (b, s, shared) => h("div", { class: "block" + (shared ? " shared" : "") }, h("b", {}, b), h("span", {}, s));
  put($("view-custom"), h("div", { class: "blank-inner" },
    h("div", {}, h("div", { class: "muted small", style: "text-transform:uppercase;letter-spacing:.07em" }, "New example"),
      h("h2", {}, ex.name), ex.desc ? h("p", { style: "margin:6px 0 0;max-width:65ch" }, ex.desc) : null),
    h("div", { class: "callout" }, h("span", { class: "dot accent" }), h("div", {},
      h("p", {}, "This tab is a placeholder for a new way to use Pipeline. It already has the shared top configuration and the status bar."),
      h("p", { class: "muted" }, "To build it: add a script next to explorer.js that calls App.register({ id, name, desc, shown }) with a <main id=\"view-{id}\"> in index.html. Use getJson/postJson with an operation (Ops.start) so the status bar and Activity follow its work."))),
    h("div", { class: "section" }, h("h2", {}, "Shared by every example"), h("div", { class: "blocks" },
      blk("Top configuration", "Org, project, token, project service, branch, workbench, environment.", true),
      blk("Status bar and Activity", "Every operation, its steps and elapsed time, and each Pipeline call it made.", true))),
    h("div", { class: "section" }, h("h2", {}, "Pieces to build with"), h("div", { class: "blocks" },
      blk("File tree", "fileTree(): a workbench revision's folders, loaded as you open them."),
      blk("Viewer", "Viewer: the Scene Preview player, loading archives Pipeline builds."),
      blk("Save as a revision", "POST …/save: edited text files committed as one new revision."),
      blk("Two workbenches", "createWorkbench() and waitUntilSettled() for another branch."))),
    h("div", {}, h("button", { class: "btn small", type: "button", onclick: () => { writePref("examples", customExamples().filter((c) => c.id !== ex.id)); showView("explorer"); } }, "Remove this example"))));
}

// ── status bar and activity ─────────────────────────────────────────────────

function renderStatus() {
  const running = Ops.running();
  const status = $("status"), now = $("now");
  const last = Ops.last();
  status.classList.toggle("failed", !running.length && last?.state === "failed");
  if (running.length) {
    const op = running.find((o) => o.example === App.view) ?? running[0];
    const st = op.activeStep;
    const pct = op.progress;
    put(now, h("span", { class: "spin" }),
      h("span", { class: "txt" }, h("b", {}, op.title), st ? ` · ${st.label}${st.sub ? ` (${st.sub})` : ""}` : op.notes.length ? ` · ${op.notes[op.notes.length - 1]}` : "",
        running.length > 1 ? h("span", { class: "muted" }, ` · +${running.length - 1} more`) : null),
      pct !== null ? h("span", { class: "meter", title: `${Math.round(pct * 100)}%` }, h("i", { style: `width:${Math.round(pct * 100)}%` })) : null,
      h("span", { class: "muted num", "data-op": op.id }, secs(op.elapsed)));
  } else if (last) {
    const ok = last.state === "done";
    put(now, h("span", { class: "dot " + (ok ? "ok" : last.state === "failed" ? "bad" : "") }),
      h("span", { class: "txt", title: last.error ?? last.result ?? "" },
        ok ? "Idle · " : last.state === "failed" ? "Failed · " : "Stopped · ", h("b", {}, last.title),
        ok ? ` · ${last.result ? `${last.result} · ` : ""}${secs(last.elapsed)}` : ` · ${(last.error ?? last.result ?? "").split("\n")[0]}`),
      last.state === "failed" ? h("button", { class: "btn small", type: "button", onclick: () => { showView(last.example ?? "explorer"); last.onShow?.(); openDrawer(true, last); } }, "Show") : null,
      last.state === "failed" && last.retry ? h("button", { class: "btn small", type: "button", onclick: last.retry.run }, last.retry.label ?? "Retry") : null);
  } else {
    put(now, h("span", { class: "dot ok" }), h("span", { class: "txt" }, App.wb ? `Idle · workbench ${short(App.wb)}${App.revision ? ` ready at revision ${App.revision}` : ""}` : "Idle"));
  }
  const calls = App.calls.filter((c) => !c.isPoll).length;
  const errs = App.calls.filter((c) => c.status >= 400 || c.status === 0).length;
  put($("counts"), h("span", { class: "num" }, `${running.length} running`), h("span", { class: "num" }, `${calls} calls`),
    h("span", { class: "num" + (errs ? " s5" : "") }, `${errs} error${errs === 1 ? "" : "s"}`));
}

function callRow(c) {
  return h("li", { class: c.isPoll ? "poll" : "", "aria-current": App.selectedCall === c ? "true" : null, title: c.errorDetail ?? "",
    onclick: () => showExchange(c) },
    h("span", {}, c.method), h("span", { class: "s" + String(c.status)[0] }, String(c.status || "ERR")), h("span", { class: "p" }, c.path),
    h("span", { class: "muted" }, `${c.ms} ms`));
}

function showExchange(c) {
  App.selectedCall = c;
  $("exchange").textContent = c.exchange ?? "(no details)";
  $("copyCurl").disabled = !c.curl;
  if (!$("drawer").hidden) renderOps();
  App.emit("call-selected", c);
}

function renderOps() {
  const showPolls = $("showPolls").checked;
  const visible = (c) => showPolls || !c.isPoll;
  const items = Ops.list.map((op) => {
    const calls = op.calls.filter(visible);
    return h("li", { class: `op ${op.state}`, id: `drawer-${op.id}` },
      h("div", { class: "op-head" },
        op.running ? h("span", { class: "spin" }) : h("span", { class: "dot " + (op.state === "done" ? "ok" : op.state === "failed" ? "bad" : "") }),
        h("span", { class: "t", title: op.title }, op.title), h("span", { class: "d num", "data-op": op.running ? op.id : null }, secs(op.elapsed))),
      op.error ? h("div", { class: "note bad" }, op.error) : op.result ? h("div", { class: "note" }, op.result) : null,
      op.running && op.notes.length ? h("div", { class: "note" }, op.notes[op.notes.length - 1]) : null,
      calls.length ? h("ol", { class: "calls" }, calls.map(callRow)) : null);
  });
  const loose = App.calls.filter((c) => !c.op || !Ops.get(c.op)).filter(visible).slice(-60);
  if (loose.length) items.push(h("li", { class: "op" }, h("div", { class: "op-head" }, h("span", { class: "dot" }), h("span", { class: "t muted" }, "Other calls (page loads, lists)")),
    h("ol", { class: "calls" }, loose.reverse().map(callRow))));
  put($("ops"), items.length ? items : h("li", { class: "op muted" }, "Nothing yet."));
}

function openDrawer(open, focusOp) {
  $("drawer").hidden = !open;
  $("activityBtn").setAttribute("aria-expanded", String(open));
  if (open) {
    renderOps();
    if (focusOp) $(`drawer-${focusOp.id}`)?.scrollIntoView({ block: "nearest" });
  }
}

function wireStatus() {
  $("activityBtn").addEventListener("click", () => openDrawer($("drawer").hidden));
  $("showPolls").addEventListener("change", renderOps);
  $("copyCurl").addEventListener("click", () => {
    const curl = App.selectedCall?.curl;
    if (curl) navigator.clipboard?.writeText(curl).catch(() => getSelection().selectAllChildren($("exchange")));
  });
  $("exNewCreate").addEventListener("click", createExample);
  $("exNewCancel").addEventListener("click", () => ($("newex").hidden = true));
  $("exNewName").addEventListener("keydown", (e) => { if (e.key === "Enter") createExample(); if (e.key === "Escape") $("newex").hidden = true; });
  Ops.onChange(() => { renderStatus(); renderExamples(); if (!$("drawer").hidden) renderOps(); App.emit("ops"); });
}

async function pollActivity() {
  try {
    const r = await getJson(`/api/activity?after=${App.lastCall}`);
    if (r.items.length) {
      App.calls.push(...r.items);
      if (App.calls.length > 600) App.calls = App.calls.slice(-600);
      App.lastCall = r.last;
      Ops.changed();
      App.emit("calls");
    }
  } catch { /* the server is restarting; try again */ }
  setTimeout(pollActivity, 1200);
}

// Elapsed times tick without re-rendering everything.
function tick() {
  for (const el of document.querySelectorAll("[data-op]")) {
    const op = Ops.get(el.dataset.op);
    if (op?.running) el.textContent = secs(op.elapsed);
  }
  for (const el of document.querySelectorAll("[data-since]")) el.textContent = secs(Date.now() - Number(el.dataset.since));
}
