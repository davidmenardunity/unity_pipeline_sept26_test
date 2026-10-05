// Pipeline Explorer page. Talks only to the local server (/api/...), which
// holds the token and calls the pipeline.
"use strict";

const $ = (id) => document.getElementById(id);
const ROOTS = ["Assets", "Packages", "ProjectSettings"];

const state = {
  config: null,
  branch: null,
  workbenches: [],
  wb: null,            // selected workbench id
  revision: null,      // settled revision every read is pinned to
  env: null,           // selected environment id
  selected: null,      // { path, guid, info }
  pollTimer: null,
  lastCall: 0,
  calls: [],
  selectedCall: null,
};

// ── helpers ─────────────────────────────────────────────────────────────────

function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === "class") el.className = v;
    else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else if (v !== undefined && v !== null && v !== false) el.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children.flat()) if (c !== null && c !== undefined && c !== false) el.append(c);
  return el;
}

class ApiError extends Error {
  constructor(status, body) {
    super(body?.error || `HTTP ${status}`);
    this.status = status;
    this.body = body || {};
  }
}

async function call(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: { "X-Pipeline-Explorer": "1", ...(body ? { "Content-Type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) {
    let parsed = null;
    try { parsed = await res.json(); } catch { /* not JSON */ }
    throw new ApiError(res.status, parsed);
  }
  return res;
}
const getJson = async (url) => (await call("GET", url)).json();
const postJson = async (url, body) => (await call("POST", url, body ?? {})).json();
const enc = encodeURIComponent;

function banner(text, kind = "error") {
  const b = $("banner");
  b.hidden = !text;
  b.className = "banner" + (kind === "info" ? " info" : "");
  b.textContent = text || "";
}

function describeError(e) {
  const b = e.body || {};
  const lines = [e.message];
  if (b.requestId && !e.message.includes(b.requestId)) lines.push(`requestId: ${b.requestId}`);
  if (b.code === "revision_not_validated") lines.push("The project service is still producing this (common right after a cold start). Retry in a minute.");
  if (b.vpn) lines.push("Connect to the corporate VPN and retry.");
  return lines.join("\n");
}

const short = (id) => (id ? id.slice(0, 8) : "");
const fmtBytes = (n) => n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`;

// In-page stand-in for confirm()/prompt(), which the app's browser pane doesn't support.
// Resolves to true/false (confirm) or the trimmed text / null (when `value` is given).
function ask({ title, message, value, placeholder, ok = "OK", danger = false }) {
  return new Promise((resolve) => {
    const input = value !== undefined ? h("input", { value, placeholder: placeholder ?? "", spellcheck: "false" }) : null;
    const cancel = () => done(input ? null : false);
    const done = (v) => { overlay.remove(); resolve(v); };
    const okBtn = h("button", { class: "primary" + (danger ? " danger" : ""), onclick: () => done(input ? input.value.trim() || null : true) }, ok);
    const overlay = h("div", { class: "overlay", onclick: (e) => e.target === overlay && cancel() },
      h("div", { class: "dialog", role: "dialog", "aria-modal": "true", "aria-label": title },
        h("h3", {}, title), message ? h("p", {}, message) : null, input,
        h("div", { class: "dialog-actions" }, h("button", { onclick: cancel }, "Cancel"), okBtn)));
    overlay.addEventListener("keydown", (e) => {
      if (e.key === "Escape") cancel();
      else if (e.key === "Enter" && input) okBtn.click();
    });
    document.body.append(overlay);
    (input ?? okBtn).focus();
    input?.select();
  });
}

// ── config, service ─────────────────────────────────────────────────────────

// Returns "token" when there's no usable token, "pick" when org/project are still to choose, "ok" otherwise.
async function loadConfig() {
  const c = state.config = await getJson("/api/config");
  const t = $("token");
  if (!c.token) { t.textContent = "no token"; t.className = "pill bad"; }
  else if (c.token.isExpired) { t.textContent = "token expired"; t.className = "pill bad"; }
  else {
    const hours = c.token.expiresAt ? Math.round((new Date(c.token.expiresAt) - Date.now()) / 36e5) : null;
    t.textContent = `${c.token.kind}${hours !== null ? `, ${hours} h left` : ""}`;
    t.className = "pill " + (hours !== null && hours < 4 ? "warn" : "ok");
  }
  if (c.missing.includes("UNITY_JWT") || c.token?.isExpired) {
    banner(`${c.token?.isExpired ? "The bearer token has expired." : `No bearer token in ${c.source ?? ".env"}.`}\n` +
      "Copy the Authorization header from the staging dashboard (see .env.example), put it in UNITY_JWT in .env, then press Reload config.");
    return "token";
  }
  if (c.missing.length) {
    banner("Enter your org ID and press Load, then pick a project.", "info");
    return "pick";
  }
  banner(null);
  return "ok";
}

// ── org and project picker ──────────────────────────────────────────────────

const SAVED = "pipeline-explorer.selection";
function readSaved() {
  try { return JSON.parse(localStorage.getItem(SAVED) || "null"); } catch { return null; }
}
function save(selection) {
  try { localStorage.setItem(SAVED, JSON.stringify(selection)); } catch { /* private window */ }
}

// Fill the project dropdown for an org; select `prefer` if it's there.
async function loadOrg(org, prefer) {
  const sel = $("projectSel");
  $("orgName").textContent = "…";
  $("orgName").title = "";
  sel.replaceChildren(h("option", { value: "" }, "loading…"));
  try {
    const o = await getJson(`/api/orgs/${enc(org)}`);
    $("orgName").textContent = o.name ?? "";
  } catch (e) {
    $("orgName").textContent = e.status === 404 ? "org not found" : "couldn't load org";
    $("orgName").title = describeError(e);
    sel.replaceChildren(h("option", { value: "" }, "(no projects)"), h("option", { value: "__enter" }, "Enter project ID…"));
    return;
  }
  let projects = [];
  try { projects = await getJson(`/api/orgs/${enc(org)}/projects`); }
  catch (e) { $("orgName").title = describeError(e); }
  const known = projects.some((p) => p.id === prefer);
  sel.replaceChildren(
    ...(prefer && known ? [] : [h("option", { value: "" }, projects.length ? "(pick a project)" : "(no projects)")]),
    ...(prefer && !known ? [h("option", { value: prefer }, `${short(prefer)}… (not in the list)`)] : []),
    ...projects.map((p) => h("option", { value: p.id, title: `${p.id}\ncreated ${new Date(p.createdAt).toLocaleString()}` },
      `${p.name ?? "(unnamed)"} · ${short(p.id)}`)),
    h("option", { value: "__enter" }, "Enter project ID…"));
  sel.value = prefer ?? "";
}

async function onLoadOrg() {
  const org = $("org").value.trim();
  if (!org) return;
  await loadOrg(org, org === state.config.organizationId ? state.config.projectId : null);
}

async function onPickProject() {
  const sel = $("projectSel");
  let project = sel.value;
  if (project === "__enter") {
    project = await ask({ title: "Enter a project ID", message: "The Unity Cloud project's UUID.", value: state.config.projectId || "", placeholder: "00000000-0000-0000-0000-000000000000", ok: "Use project" });
    if (!project) { sel.value = state.config.projectId || ""; return; }
    if (![...sel.options].some((o) => o.value === project)) sel.prepend(h("option", { value: project }, `${short(project)}…`));
    sel.value = project;
  }
  if (!project) return;
  await selectProject($("org").value.trim(), project);
}

async function selectProject(org, project) {
  await postJson("/api/select", { organizationId: org, projectId: project });
  save({ org, project });
  if ((await loadConfig()) === "ok") await openProject();
}

async function loadService() {
  const pill = $("service");
  try {
    const s = await getJson(`/api/service?branch=${enc(state.branch)}`);
    pill.textContent = `${s.summary} (${state.branch})`;
    pill.className = "pill " + (s.isReady ? "ok" : s.isStarting ? "warn" : "bad");
    $("startService").disabled = s.isReady;
  } catch (e) {
    pill.textContent = "project service: ?";
    pill.className = "pill bad";
    pill.title = describeError(e);
  }
}

// ── progress panel: what a long wait is doing ───────────────────────────────

const STEPS = [
  { id: "service", label: "Project service" },
  { id: "created", label: "Workbench" },
  { id: "validating", label: "Validation" },
  { id: "settled", label: "Ready to read" },
];

const progress = {
  owner: null,    // the workbench id (or "creating") the panel is about
  timer: null,
  lastLog: null,

  start(owner, title, since = Date.now(), steps = STEPS) {
    this.owner = owner;
    this.since = since;
    this.lastLog = null;
    $("progress").hidden = false;
    $("progress").className = "progress";
    $("progressSpin").hidden = false;
    $("progressTitle").textContent = title;
    $("progressHint").textContent = "";
    $("progressDetail").replaceChildren();
    $("progressLog").replaceChildren();
    $("progressSteps").replaceChildren(...steps.map((s) =>
      h("li", { class: "pending", "data-step": s.id }, h("span", { class: "icon" }, "○"), s.label, h("span", { class: "sub" }))));
    clearInterval(this.timer);
    const tick = () => {
      const s = Math.round((Date.now() - this.since) / 1000);
      $("progressElapsed").textContent = s < 60 ? `${s} s` : `${Math.floor(s / 60)} min ${s % 60} s`;
    };
    tick();
    this.timer = setInterval(tick, 1000);
  },

  step(id, status, sub = "") {
    const li = document.querySelector(`#progressSteps [data-step="${id}"]`);
    if (!li) return;
    li.className = status;
    li.querySelector(".icon").replaceChildren(
      status === "active" ? h("span", { class: "spinner" }) : status === "done" ? "✓" : status === "failed" ? "✕" : "○");
    li.querySelector(".sub").textContent = sub ? ` ${sub}` : "";
  },

  hint(text) { $("progressHint").textContent = text; },
  detail(...nodes) { $("progressDetail").replaceChildren(...nodes); },

  log(text) {
    if (text === this.lastLog) return;
    this.lastLog = text;
    const list = $("progressLog");
    list.append(h("li", {}, `${new Date().toLocaleTimeString()}  ${text}`));
    list.scrollTop = list.scrollHeight;
  },

  finish(ok, title) {
    clearInterval(this.timer);
    $("progressSpin").hidden = true;
    $("progress").className = "progress " + (ok ? "done" : "failed");
    $("progressTitle").textContent = title;
    if (ok) {
      const owner = this.owner;
      setTimeout(() => { if (this.owner === owner) this.hide(); }, 6000);
    }
  },

  hide() {
    clearInterval(this.timer);
    this.owner = null;
    $("progress").hidden = true;
  },
};

function serviceSteps(s) {
  if (!s.steps?.length) return null;
  return h("div", { class: "svc-steps" }, ...s.steps.map((st) =>
    h("span", {}, `${st.name}: ${st.status}${st.progress != null && st.status !== "Succeeded" ? ` ${st.progress}%` : ""}`)));
}

// Start the branch's project service if needed and wait for "ready", narrating in the panel.
async function waitForService(owner) {
  progress.step("service", "active", "checking…");
  let s = await getJson(`/api/service?branch=${enc(state.branch)}`);
  if (!s.isReady && !s.isStarting) {
    progress.log("project service stopped: asking it to start");
    await postJson(`/api/service/start?branch=${enc(state.branch)}&wait=false`);
  }
  const deadline = Date.now() + 6 * 60_000;
  while (!s.isReady) {
    if (progress.owner !== owner) return false;
    if (Date.now() > deadline) throw new Error(`The project service isn't ready after 6 minutes (last: ${s.summary}).`);
    progress.step("service", "active", s.isStarting ? "starting" : s.status ?? "");
    progress.hint("A cold start takes 1–2 minutes.");
    progress.detail(h("div", {}, s.message ?? s.summary), serviceSteps(s));
    progress.log(`project service: ${s.status}${s.jobStatus ? ` (job ${s.jobStatus})` : ""}`);
    await new Promise((r) => setTimeout(r, 3000));
    s = await getJson(`/api/service?branch=${enc(state.branch)}`);
  }
  progress.step("service", "done", "ready");
  progress.log("project service: ready");
  loadService();
  return true;
}

async function startService() {
  const btn = $("startService");
  btn.disabled = true;
  progress.start("service", `Starting the project service for ${state.branch}`);
  try {
    if (await waitForService("service")) progress.finish(true, `Project service for ${state.branch} is ready`);
  } catch (e) {
    progress.step("service", "failed");
    progress.detail(describeError(e));
    progress.finish(false, "The project service didn't start");
  }
  await loadService();
}

// ── branches and workbenches ────────────────────────────────────────────────

async function loadBranches() {
  const r = await getJson("/api/branches");
  const sel = $("branch");
  sel.replaceChildren(...r.branches.map((b) => h("option", { value: b }, b)), h("option", { value: "__other" }, "Other…"));
  state.branch ??= state.config.branch;
  if (!r.branches.includes(state.branch)) sel.prepend(h("option", { value: state.branch }, state.branch));
  sel.value = state.branch;
  state.repository = r.repository;
  state.heads = r.heads ?? {};
  sel.title = r.gitError ? `Couldn't list the repo's branches: ${r.gitError}` : `Branches of ${r.repository ?? "the repo"}`;
}

async function loadWorkbenches(preferId) {
  try {
    state.workbenches = await getJson("/api/workbenches");
  } catch (e) {
    banner(describeError(e));
    state.workbenches = [];
  }
  renderWorkbenchPicker(preferId);
}

function renderWorkbenchPicker(preferId) {
  const onBranch = state.workbenches.filter((w) => (w.branchName || state.config.branch) === state.branch);
  const sel = $("workbench");
  // "behind" when the branch has moved on since the workbench was made: it won't have those commits.
  const head = state.heads?.[state.branch];
  const behind = (w) => head && w.upstreamRevision && head !== w.upstreamRevision;
  const label = (w) => `${short(w.workbenchId)} · ${w.upstreamRevision ? "@" + w.upstreamRevision.slice(0, 7) + " · " : ""}` +
    (w.createdAt ? new Date(w.createdAt).toLocaleString() : "") +
    (behind(w) ? ` · behind (${state.branch} @${head.slice(0, 7)})` : "");
  sel.replaceChildren(
    ...(onBranch.length ? [] : [h("option", { value: "" }, "(no workbench on this branch)")]),
    ...onBranch.map((w) => h("option", { value: w.workbenchId, title: `${w.upstreamRepository ?? ""} ${w.branchName ?? ""}` }, label(w))));
  const pick = onBranch.find((w) => w.workbenchId === preferId)
    ?? onBranch.find((w) => w.workbenchId === state.wb)
    ?? onBranch.find((w) => w.workbenchId === state.config.workbenchId)
    ?? onBranch[0];
  sel.value = pick?.workbenchId ?? "";
  $("deleteWorkbench").disabled = !pick;
  selectWorkbench(pick?.workbenchId ?? null);
}

async function changeBranch() {
  let b = $("branch").value;
  if (b === "__other") {
    b = await ask({ title: "Another branch", message: "The branch must exist in the git repo.", value: state.branch, ok: "Switch" });
    if (!b) { $("branch").value = state.branch; return; }
    if (![...$("branch").options].some((o) => o.value === b)) $("branch").prepend(h("option", { value: b }, b));
    $("branch").value = b;
  }
  state.branch = b;
  loadService();
  renderWorkbenchPicker();
}

async function newWorkbench() {
  const repository = await ask({
    title: `New workbench on ${state.branch}`,
    message: "The public git repo it tracks. The workbench starts from the branch's latest commit and doesn't follow later pushes.",
    value: state.repository ?? "", placeholder: "https://github.com/owner/repo", ok: "Create",
  });
  if (repository) await createWorkbench(repository);
}

// Create a workbench (deleting `replacing` first, if given) and follow it until it settles.
async function createWorkbench(repository, replacing = null) {
  const btn = $("newWorkbench");
  btn.disabled = true;
  selectWorkbench(null);
  progress.start("creating", `Starting a workbench on ${state.branch}`);
  try {
    if (!(await waitForService("creating"))) return;
    const before = new Set(state.workbenches.map((w) => w.workbenchId));
    if (replacing) {
      progress.step("created", "active", `deleting ${short(replacing)}…`);
      await call("DELETE", `/api/workbenches/${replacing}`);
      before.delete(replacing);
      progress.log(`deleted workbench ${replacing}`);
    }
    progress.step("created", "active", "creating…");
    progress.log(`POST workbenches (${state.branch}, ${repository})`);
    const wb = await postJson("/api/workbenches", { branch: state.branch, repository });
    if (before.has(wb.workbenchId)) {
      // The pipeline answered with a workbench we already had: nothing new was made.
      const head = state.heads?.[state.branch];
      const from = wb.upstreamRevision?.slice(0, 7);
      progress.step("created", "failed", `got ${short(wb.workbenchId)} back`);
      progress.log(`the pipeline returned the existing workbench ${wb.workbenchId}`);
      progress.detail(
        h("div", {}, `The pipeline handed back the existing workbench ${short(wb.workbenchId)}` +
          `${from ? `, made from commit ${from}` : ""}, instead of creating a new one` +
          `${head && from && !head.startsWith(from) ? `. ${state.branch} is now at ${head.slice(0, 7)}` : ""}.`),
        h("div", { class: "dialog-actions left" },
          h("button", { class: "danger", onclick: () => createWorkbench(repository, wb.workbenchId) },
            `Delete ${short(wb.workbenchId)} and create a new one`)));
      progress.finish(false, "No new workbench: the existing one came back");
      await loadWorkbenches(wb.workbenchId);
      return;
    }
    progress.owner = wb.workbenchId;   // pollWorkbench carries on in the same panel
    progress.step("created", "done", short(wb.workbenchId));
    progress.log(`workbench ${wb.workbenchId} created from ${wb.upstreamRevision?.slice(0, 7) ?? "?"}`);
    await loadWorkbenches(wb.workbenchId);
  } catch (e) {
    progress.step(document.querySelector('#progressSteps li[data-step="service"].done') ? "created" : "service", "failed");
    progress.detail(describeError(e));
    progress.finish(false, "Couldn't start the workbench");
  } finally {
    btn.disabled = false;
  }
}

async function deleteWorkbench() {
  const id = state.wb;
  if (!id) return;
  const ok = await ask({
    title: `Delete workbench ${short(id)}?`,
    message: "Its environments go with it, and so do any changes made through the pipeline that weren't published to git.",
    ok: "Delete", danger: true,
  });
  if (!ok) return;
  const btn = $("deleteWorkbench");
  btn.disabled = true;
  banner(`Deleting workbench ${short(id)}…`, "info");
  try {
    await call("DELETE", `/api/workbenches/${id}`);
    banner(null);
    if (progress.owner === id) progress.hide();
    state.wb = null;
    await loadWorkbenches();
  } catch (e) {
    banner(describeError(e));
    btn.disabled = false;
  }
}

function selectWorkbench(id) {
  clearTimeout(state.pollTimer);
  if (id === state.wb && state.revision) return;
  state.wb = id;
  state.revision = null;
  state.env = null;
  $("tree").replaceChildren();
  $("revLabel").textContent = "";
  $("environment").replaceChildren();
  clearSelection();
  if (progress.owner && progress.owner !== id && progress.owner !== "creating" && progress.owner !== "service") progress.hide();
  $("deleteWorkbench").disabled = !id;
  $("readiness").textContent = id ? "checking…" : "";
  if (id) pollWorkbench(id);
}

// Wait until the workbench has settled (validated), then read at that revision.
// While it settles, the progress panel says where it is.
async function pollWorkbench(id, first = true) {
  if (state.wb !== id) return;
  let again = true;
  try {
    const { workbench, readiness, readinessError } = await getJson(`/api/workbenches/${id}`);
    if (state.wb !== id) return;
    const v = workbench.validation;
    const r = readiness?.readiness;
    const parts = [];
    if (readiness) parts.push(r, `head ${readiness.head ?? "?"}`);
    if (v?.status) parts.push(`validation ${v.status}`);
    $("readiness").textContent = parts.join(" · ");
    $("readiness").title = readinessError ?? "";

    const settled = r === "settled" && readiness.settledRevision;
    // Already settled when picked: read straight away, no panel.
    if (!(first && settled && progress.owner !== id)) watchSettling(id, workbench, readiness, readinessError);

    if (v?.status === "failed") {
      again = false;
      progress.step("validating", "failed", v.status);
      progress.detail(h("div", {}, `${v.error?.category ?? "validation failed"}: ${v.error?.message ?? ""}`),
        v.error?.message?.includes("Broken pipe") ? h("div", {}, "Known cause: Unity 6.6+ projects. Use 6.5 or older.") : null);
      progress.finish(false, `Validation failed on workbench ${short(id)}`);
    } else if (settled) {
      again = false;
      if (progress.owner === id) {
        progress.step("validating", "done", v?.status ?? "");
        progress.step("settled", "done", `revision ${readiness.settledRevision}`);
        progress.log(`settled at revision ${readiness.settledRevision}`);
        progress.finish(true, `Workbench ${short(id)} is ready at revision ${readiness.settledRevision}`);
      }
      await openRevision(id, readiness.settledRevision);
    } else if (r === "gone") {
      again = false;
      progress.step("validating", "failed", "gone");
      progress.detail("The project service no longer has this workbench. Start the service, or create a new workbench.");
      progress.finish(false, `Workbench ${short(id)} is gone from the project service`);
    }
  } catch (e) {
    $("readiness").textContent = "error";
    $("readiness").title = describeError(e);
    if (progress.owner === id) { progress.detail(describeError(e)); progress.log(`error: ${e.body?.code ?? e.message}`); }
    if (e.body?.code === "status_authority_unavailable") await loadService();
  }
  if (again && state.wb === id) state.pollTimer = setTimeout(() => pollWorkbench(id, false), 5000);
}

// Narrate one readiness poll in the progress panel.
function watchSettling(id, workbench, readiness, readinessError) {
  if (progress.owner !== id) {
    // Picked while settling: time it from creation when that's recent, else from now.
    const created = workbench.createdAt ? new Date(workbench.createdAt).getTime() : 0;
    progress.start(id, `Workbench ${short(id)} is settling`, Date.now() - created < 30 * 60_000 ? created : Date.now());
    progress.step("service", "done");
    progress.step("created", "done", short(id));
  }
  const v = workbench.validation;
  const r = readiness?.readiness ?? "unknown";
  const head = readiness?.head;
  const firstRun = !readiness?.settledRevision;
  progress.step("validating", "active", [v?.status, head ? `revision ${head}` : null].filter(Boolean).join(", "));
  progress.hint(firstRun
    ? "The first validation of a workbench takes 2–3 minutes."
    : "A change validates in about 40 seconds (longer right after a cold start).");
  const lines = [
    h("div", {}, `readiness: ${r}${head ? ` · head ${head}` : ""}${readiness?.settledRevision ? ` · last settled ${readiness.settledRevision}` : ""}` +
      ` · validation: ${v?.status ?? "?"}`),
  ];
  if (r === "unknown" || readinessError) {
    lines.push(h("div", {}, readinessError
      ? `Readiness isn't answering: ${readinessError}`
      : "Readiness is unknown: the project service may still be warming up, or it stopped. Check the project service pill."));
  }
  if (v?.previousError) lines.push(h("div", {}, `previous validation error: ${v.previousError.category}: ${v.previousError.message}`));
  progress.detail(...lines);
  progress.log(`readiness ${r}, head ${head ?? "?"}, validation ${v?.status ?? "?"}`);
}

async function openRevision(id, revision, keepOpen = []) {
  state.revision = revision;
  $("revLabel").textContent = `@ revision ${revision}`;
  $("tree").replaceChildren(...ROOTS.map((r) => treeNode(r, true)));
  for (const folder of keepOpen) await reveal(folder);
  await loadEnvironments(state.env);
}

async function loadEnvironments(preferId) {
  const sel = $("environment");
  try {
    const envs = await getJson(`/api/workbenches/${state.wb}/environments`);
    sel.replaceChildren(
      ...(envs.length ? [] : [h("option", { value: "" }, "(none: add one)")]),
      ...envs.map((e) => h("option", { value: e.environmentId }, `${e.platform ?? "?"} · ${short(e.environmentId)}`)));
    const pick = envs.find((e) => e.environmentId === preferId)
      ?? envs.find((e) => e.environmentId === state.config.environmentId)
      ?? envs.find((e) => e.platform === state.config.platform)
      ?? envs[0];
    sel.value = pick?.environmentId ?? "";
    state.env = pick?.environmentId ?? null;
  } catch (e) {
    sel.replaceChildren(h("option", { value: "" }, "(couldn't list)"));
    sel.title = describeError(e);
  }
}

async function addEnvironment() {
  if (!state.wb) return;
  try {
    const env = await postJson(`/api/workbenches/${state.wb}/environments`, { platform: $("platform").value });
    await loadEnvironments(env.environmentId);
  } catch (e) { banner(describeError(e)); }
}

// ── tree ────────────────────────────────────────────────────────────────────

function treeNode(path, isFolder) {
  const name = path.split("/").pop();
  const isMeta = name.endsWith(".meta");
  const row = h("div", { class: isMeta ? "meta" : "", title: isFolder ? `${path}\nDrop files here to add them` : path },
    h("span", { class: "twisty" }, isFolder ? "▸" : ""), (isFolder ? "📁 " : ""), name);
  const li = h("li", { "data-path": path, "data-meta": isMeta ? "1" : null, "data-folder": isFolder ? "1" : null }, row);
  if (isMeta && !$("showMeta").checked) li.hidden = true;
  let loaded = false;

  li.expand = async () => {
    if (loaded) return;
    loaded = true;
    row.querySelector(".twisty").textContent = "▾";
    const ul = h("ul", {}, h("li", { class: "dim" }, "loading…"));
    li.append(ul);
    try {
      const tree = await getJson(`/api/workbenches/${state.wb}/revisions/${enc(state.revision)}/tree?path=${enc(path)}`);
      const entries = tree.entries
        .map((e) => ({ path: e.path.replace(/^\/+/, ""), isFolder: e.isFolder }))
        .filter((e) => e.path !== path)
        .sort((a, b) => (a.isFolder !== b.isFolder ? (a.isFolder ? -1 : 1) : a.path.localeCompare(b.path)));
      ul.replaceChildren(...(entries.length ? entries.map((e) => treeNode(e.path, e.isFolder)) : [h("li", { class: "dim" }, "(empty)")]));
    } catch (e) {
      ul.replaceChildren(h("li", { class: "dim", title: describeError(e) }, `error: ${e.body?.code ?? e.status}`));
      loaded = false;
    }
  };
  li.collapse = () => {
    row.querySelector(".twisty").textContent = "▸";
    li.querySelector("ul")?.remove();
    loaded = false;
  };

  row.addEventListener("click", () => {
    if (!isFolder) return selectFile(path, row);
    loaded ? li.collapse() : li.expand();
  });

  // Drop files on a folder (or on a file: its folder) to add them to the project.
  const target = isFolder ? path : path.split("/").slice(0, -1).join("/");
  row.addEventListener("dragover", (e) => {
    if (!e.dataTransfer.types.includes("Files") || !state.revision) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "copy";
    row.classList.add("drop");
  });
  row.addEventListener("dragleave", () => row.classList.remove("drop"));
  row.addEventListener("drop", (e) => {
    e.preventDefault();
    e.stopPropagation();
    row.classList.remove("drop");
    const items = [...(e.dataTransfer.items ?? [])];
    if (items.some((i) => i.webkitGetAsEntry?.()?.isDirectory)) {
      banner("Folders can't be dropped yet: drop the files themselves.", "info");
      return;
    }
    const files = [...e.dataTransfer.files];
    if (files.length) uploadFiles(target, files);
  });
  return li;
}

// The folders open in the tree right now, outermost first.
function openFolders() {
  return [...document.querySelectorAll('#tree li[data-folder] > ul')].map((ul) => ul.parentElement.dataset.path);
}

// Open each folder on the way to `path` (and `path` itself if it's a folder).
async function reveal(path) {
  const parts = path.split("/");
  for (let i = 1; i <= parts.length; i++) {
    const li = document.querySelector(`#tree li[data-path="${CSS.escape(parts.slice(0, i).join("/"))}"]`);
    if (!li) return null;
    if (li.dataset.folder) await li.expand();
    if (i === parts.length) return li;
  }
  return null;
}

function toggleMeta() {
  for (const li of document.querySelectorAll("#tree li[data-meta]")) li.hidden = !$("showMeta").checked;
}

// ── selection and artifacts ─────────────────────────────────────────────────

function clearSelection() {
  state.selected = null;
  $("selection").hidden = true;
  $("empty").hidden = false;
  $("results").replaceChildren();
}

async function selectFile(path, row) {
  document.querySelectorAll("#tree .selected").forEach((el) => el.classList.remove("selected"));
  row.classList.add("selected");
  state.selected = { path, guid: null, info: null };
  $("empty").hidden = true;
  $("selection").hidden = false;
  $("selPath").textContent = path;
  $("results").replaceChildren();
  renderFacts({ revision: state.revision, guid: "resolving…" });
  try {
    const r = await getJson(`/api/workbenches/${state.wb}/revisions/${enc(state.revision)}/asset?path=${enc(path)}`);
    if (state.selected?.path !== path) return;
    Object.assign(state.selected, { guid: r.guid, info: r.info });
    renderFacts({
      revision: state.revision,
      guid: r.guid ?? "(none: not an asset?)",
      size: r.info?.size != null ? fmtBytes(r.info.size) : null,
      "file hash": r.info?.fileHash,
      "meta hash": r.info?.metafileHash,
    });
  } catch (e) {
    renderFacts({ revision: state.revision, guid: `error: ${describeError(e)}` });
  }
}

function renderFacts(facts) {
  $("selFacts").replaceChildren(...Object.entries(facts).filter(([, v]) => v != null)
    .flatMap(([k, v]) => [h("dt", {}, k), h("dd", {}, String(v))]));
}

// A result card with a spinner and elapsed time while it runs, and Retry on failure.
function card(title, run) {
  const status = h("span", { class: "dim small" });
  const spin = h("span", { class: "spinner" });
  const body = h("div", { class: "card-body" });
  const close = h("button", { class: "small", onclick: () => el.remove() }, "×");
  const el = h("div", { class: "card" }, h("div", { class: "card-head" }, spin, h("span", { class: "title" }, title), status, h("span", { class: "grow" }), close), body);
  $("results").prepend(el);
  const started = Date.now();
  const tick = setInterval(() => (status.textContent = `${Math.round((Date.now() - started) / 1000)} s`), 1000);
  const finish = () => { clearInterval(tick); spin.remove(); status.textContent = `${((Date.now() - started) / 1000).toFixed(1)} s`; };
  run(body).then(finish, (e) => {
    finish();
    el.classList.add("error");
    body.replaceChildren(describeError(e), h("div", {}, h("button", { onclick: () => { el.remove(); card(title, run); } }, "Retry")));
  });
}

async function renderBytes(res, name, into) {
  const type = res.headers.get("Content-Type") || "";
  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  const save = h("a", { href: url, download: name }, `Save ${name} (${fmtBytes(blob.size)})`);
  if (type.startsWith("image/")) {
    into.replaceChildren(h("img", { src: url, alt: name }), h("div", { class: "small" }, save));
  } else if (type.startsWith("text/")) {
    let text = await blob.text();
    if (text.length > 200_000) text = text.slice(0, 200_000) + "\n… (trimmed; save to see all)";
    into.replaceChildren(h("pre", {}, text), h("div", { class: "small" }, save));
  } else {
    const head = new Uint8Array(await blob.slice(0, 16).arrayBuffer());
    const ascii = [...head].map((b) => (b >= 32 && b < 127 ? String.fromCharCode(b) : ".")).join("");
    into.replaceChildren(h("div", {}, `Binary, ${fmtBytes(blob.size)}. Starts with `, h("code", {}, ascii)), h("div", {}, save));
  }
}

function needGuid() {
  if (!state.selected?.guid) throw new Error("This file has no asset GUID (yet). Wait for it to resolve, or pick an asset file.");
  return state.selected.guid;
}

function needEnv() {
  if (!state.env) throw new Error("Previews and imports render in an environment. Add one (top right) first.");
  return state.env;
}

const artifacts = {
  meta: () => card(".meta", async (body) => {
    const guid = needGuid();
    const res = await call("GET", `/api/workbenches/${state.wb}/revisions/${enc(state.revision)}/meta?guid=${guid}`);
    await renderBytes(res, state.selected.path.split("/").pop() + ".meta", body);
  }),

  source: () => card("Source", async (body) => {
    const path = state.selected.path;
    const res = await call("GET", `/api/workbenches/${state.wb}/revisions/${enc(state.revision)}/file?path=${enc(path)}`);
    await renderBytes(res, path.split("/").pop(), body);
  }),

  preview: () => card("Preview", async (body) => {
    const guid = needGuid(), env = needEnv();
    body.textContent = "Rendering in the cloud. A cold render takes about 2 minutes.";
    const res = await call("GET", `/api/workbenches/${state.wb}/environments/${env}/revisions/${enc(state.revision)}/preview?guid=${guid}`);
    if (res.status === 204) body.textContent = "No preview for this asset type (scenes, scripts… don't render).";
    else await renderBytes(res, `${guid}.png`, body);
  }),

  imports: () => {
    const type = $("importerType").value.trim() || $("importerType").placeholder;
    const address = $("importMode").value === "T" ? `T:${state.selected?.guid}+${type}` : `G:${state.selected?.guid}`;
    card(`Content files · ${address}`, async (body) => {
      needGuid();
      const env = needEnv();
      body.textContent = "Importing…";
      const base = `/api/workbenches/${state.wb}/environments/${env}/revisions/${enc(state.revision)}/imports`;
      const r = await postJson(base, { address });
      if (r.error) {
        body.replaceChildren(h("div", {}, `Slot error: ${r.error.code}: ${r.error.message ?? ""}`),
          r.error.code === "import_not_found" ? h("div", { class: "dim" }, "This project has no importer of that type for this asset.") : null);
        return;
      }
      const list = h("ul", { class: "artifact-list" }, ...r.artifacts.map((name) => {
        const out = h("div");
        const li = h("li", {}, h("button", { onclick: () => fetchArtifact(base, address, name, out) }, "Fetch"), name);
        return h("div", {}, li, out);
      }));
      body.replaceChildren(
        h("div", {}, r.artifacts.length ? `${r.artifacts.length} artifact(s):` : "The manifest lists no artifacts."),
        list,
        h("details", {}, h("summary", { class: "dim small" }, "Import manifest (raw)"), h("pre", {}, JSON.stringify(r.manifest, null, 2))));
    });
  },
};

async function fetchArtifact(base, address, name, out) {
  out.replaceChildren(h("span", { class: "dim small" }, "fetching…"));
  try {
    const res = await call("GET", `${base}/artifact?address=${enc(address)}&name=${enc(name)}`);
    await renderBytes(res, name.split("/").pop(), out);
  } catch (e) {
    out.replaceChildren(h("pre", { class: "small" }, describeError(e)));
  }
}

// ── adding files (drag and drop) ────────────────────────────────────────────

const UPLOAD_STEPS = [
  { id: "upload", label: "Upload and commit" },
  { id: "validating", label: "Validation" },
  { id: "settled", label: "In the tree" },
];

const isNewer = (a, b) => (/^\d+$/.test(a) && /^\d+$/.test(b) ? Number(a) >= Number(b) : a === b);

// Upload files into `folder`: one change = one new revision, which validates before
// it can be read. The tree keeps showing the old revision until then.
async function uploadFiles(folder, files) {
  const wb = state.wb, base = state.revision;
  if (!wb || !base) return;
  const total = files.reduce((n, f) => n + f.size, 0);
  const owner = `upload:${wb}:${Date.now()}`;
  const names = files.map((f) => f.name);
  progress.start(owner, `Adding ${files.length === 1 ? names[0] : `${files.length} files`} to ${folder}`, Date.now(), UPLOAD_STEPS);
  progress.detail(h("div", {}, names.map((n) => `${folder}/${n}`).join(", ")));
  progress.step("upload", "active", fmtBytes(total));
  progress.log(`uploading ${files.length} file(s), ${fmtBytes(total)}, on top of revision ${base}`);

  const form = new FormData();
  for (const f of files) form.append("files", f, f.name);
  const branch = state.workbenches.find((w) => w.workbenchId === wb)?.branchName || state.branch;
  let revision;
  try {
    const res = await fetch(`/api/workbenches/${wb}/revisions/${enc(base)}/files?folder=${enc(folder)}&branch=${enc(branch)}`,
      { method: "POST", headers: { "X-Pipeline-Explorer": "1" }, body: form });
    const body = await res.json().catch(() => null);
    if (!res.ok) throw new ApiError(res.status, body);
    revision = body.revision;
  } catch (e) {
    progress.step("upload", "failed");
    progress.detail(describeError(e));
    progress.finish(false, "The files weren't added");
    return;
  }
  progress.step("upload", "done", `revision ${revision}`);
  progress.step("validating", "active", `revision ${revision}`);
  progress.hint("A change validates in about 40 seconds (longer right after a cold start).");
  progress.log(`committed: revision ${revision}`);

  while (progress.owner === owner) {
    await new Promise((r) => setTimeout(r, 4000));
    if (progress.owner !== owner) return;
    try {
      const { workbench, readiness } = await getJson(`/api/workbenches/${wb}`);
      const v = workbench.validation;
      const r = readiness?.readiness ?? "unknown";
      progress.detail(h("div", {}, `readiness: ${r} · head ${readiness?.head ?? "?"} · last settled ${readiness?.settledRevision ?? "?"} · validation: ${v?.status ?? "?"}`));
      progress.log(`readiness ${r}, head ${readiness?.head ?? "?"}, validation ${v?.status ?? "?"}`);
      if (v?.status === "failed") {
        progress.step("validating", "failed", v.status);
        progress.detail(h("div", {}, `${v.error?.category}: ${v.error?.message}`),
          h("div", {}, "The revision with these files didn't validate. The tree still shows the previous one."));
        progress.finish(false, `Revision ${revision} failed validation`);
        return;
      }
      if (r === "settled" && readiness.settledRevision && isNewer(readiness.settledRevision, revision)) {
        progress.step("validating", "done");
        progress.step("settled", "active");
        if (state.wb === wb) {
          await openRevision(wb, readiness.settledRevision, [...new Set([...openFolders(), folder])]);
          const li = await reveal(`${folder}/${names[0]}`);
          li?.firstElementChild?.click();
          li?.scrollIntoView({ block: "nearest" });
        }
        progress.step("settled", "done", `revision ${readiness.settledRevision}`);
        progress.finish(true, `Added to ${folder} at revision ${readiness.settledRevision}`);
        return;
      }
    } catch (e) {
      progress.log(`error: ${e.body?.code ?? e.message}`);
    }
  }
}

// ── activity ────────────────────────────────────────────────────────────────

async function pollActivity() {
  try {
    const r = await getJson(`/api/activity?after=${state.lastCall}`);
    if (r.items.length) {
      state.calls.push(...r.items);
      state.calls = state.calls.slice(-300);
      state.lastCall = r.last;
      renderCalls();
    }
  } catch { /* the server is restarting; try again */ }
  setTimeout(pollActivity, 1500);
}

function renderCalls() {
  const list = $("calls");
  const atBottom = list.scrollTop + list.clientHeight >= list.scrollHeight - 4;
  const showPolls = $("showPolls").checked;
  list.replaceChildren(...state.calls.filter((c) => showPolls || !c.isPoll).map((c) => {
    const li = h("li", { class: (c.isPoll ? "poll " : "") + (c === state.selectedCall ? "selected" : ""), title: c.errorDetail ?? "" },
      new Date(c.at).toLocaleTimeString(), " ",
      h("span", { class: "s" + String(c.status)[0] }, String(c.status || "ERR")), ` ${c.method} ${c.path} · ${c.ms} ms`,
      c.errorCode ? ` · ${c.errorCode}` : "");
    li.addEventListener("click", () => {
      state.selectedCall = c;
      $("exchange").textContent = c.exchange ?? "(no details)";
      $("exchange").classList.remove("dim");
      $("copyCurl").disabled = !c.curl;
      renderCalls();
    });
    return li;
  }));
  if (atBottom) list.scrollTop = list.scrollHeight;
}

// ── wiring ──────────────────────────────────────────────────────────────────

async function boot() {
  if ((await loadConfig()) === "token") return;
  // The last pick in this browser wins over .env (.env itself isn't changed).
  const saved = readSaved();
  const org = saved?.org || state.config.organizationId;
  const project = saved?.org ? saved.project : state.config.projectId;
  $("org").value = org ?? "";
  if (!org) return;
  await loadOrg(org, project);
  if (project && (org !== state.config.organizationId || project !== state.config.projectId)) {
    await postJson("/api/select", { organizationId: org, projectId: project });
    await loadConfig();
  }
  if (state.config.organizationId && state.config.projectId) await openProject();
}

// Everything below the header, for the selected org/project.
async function openProject() {
  progress.hide();
  state.branch = state.config.branch;
  state.wb = null;
  state.workbenches = [];
  selectWorkbench(null);
  await loadBranches().catch((e) => banner(describeError(e)));
  loadService();
  await loadWorkbenches();
}

$("loadOrg").addEventListener("click", onLoadOrg);
$("org").addEventListener("keydown", (e) => e.key === "Enter" && onLoadOrg());
$("projectSel").addEventListener("change", onPickProject);
$("branch").addEventListener("change", changeBranch);
$("workbench").addEventListener("change", (e) => selectWorkbench(e.target.value || null));
$("environment").addEventListener("change", (e) => (state.env = e.target.value || null));
$("newWorkbench").addEventListener("click", newWorkbench);
$("deleteWorkbench").addEventListener("click", deleteWorkbench);
$("addEnvironment").addEventListener("click", addEnvironment);
$("startService").addEventListener("click", startService);
$("reload").addEventListener("click", async () => {
  await call("POST", "/api/config/reload");
  state.wb = null;
  boot();
});
$("showMeta").addEventListener("change", toggleMeta);
$("showPolls").addEventListener("change", renderCalls);
$("importMode").addEventListener("change", () => ($("importerType").hidden = $("importMode").value !== "T"));
$("copyCurl").addEventListener("click", () => navigator.clipboard.writeText(state.selectedCall?.curl ?? ""));
for (const btn of document.querySelectorAll("[data-artifact]"))
  btn.addEventListener("click", () => state.selected && artifacts[btn.dataset.artifact]());

boot();
pollActivity();
