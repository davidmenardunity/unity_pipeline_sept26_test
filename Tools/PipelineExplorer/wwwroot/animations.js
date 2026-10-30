// Animations: every animation clip in the workbench (.anim files and the clips an .fbx imports), previewed in
// the animation viewer's WebGL runtime (AnimationViewer_WebGL). Humanoid clips play on its humanoid character
// (retargeted through the Avatar); an .fbx's other clips play on the model they came with; a generic .anim
// plays on a rig built from the transforms it animates, with boxes for parts until a model is generated for it
// with Unity AI (the local `unity ai` CLI; the open Editor packs it). Server side: AnimationEndpoints.cs.
"use strict";

(() => {
  const AV = {
    wb: null, rev: null,
    clips: [], generated: [], loading: null, error: null,
    selected: null, filter: "",
    playing: null,           // the runtime's last "playing" report
    paused: false, speed: 1,
    quote: null, prompt: "", gen: null,   // the model generation for the selected clip
    showOp: null,
  };
  let viewer = null;
  const keyOf = (c) => `${c.path}#${c.name}`;
  const modelFor = (c) => [...AV.generated].reverse().find((g) => g.clip === keyOf(c)) ?? null;

  App.register({ id: "animations", name: "Animations", desc: "Every animation in the workbench, previewed on a humanoid character, or on a model generated for it." });

  App.on("revision", ({ wb, revision }) => {
    $("avRev").textContent = revision ? `rev ${revision}` : "";
    if (wb && revision && (wb !== AV.wb || revision !== AV.rev)) load(wb, revision);
  });

  // ── the runtime ──────────────────────────────────────────────────────

  function runtime() {
    viewer ??= new Viewer($("avPlayer"), "animation viewer", "animplayer.html");
    viewer.ensure("animations");
    return viewer;
  }
  const rt = (method, arg = {}) => runtime().post({ type: "av", method, arg: JSON.stringify(arg) });

  addEventListener("message", (e) => {
    if (e.origin !== location.origin || !viewer || e.source !== viewer.iframe.contentWindow) return;
    const m = e.data ?? {};
    if (m.type === "anim-viewer") fromRuntime(m.data ?? {});
    else if (m.type === "player-ready" || m.type === "player-progress" || m.type === "player-error") renderStage();
  });

  function fromRuntime(r) {
    if (r.type === "playing") {
      AV.playing = r;
      AV.showOp?.step("play", "done", r.target).done(`${r.clip} on ${r.target}`);
    } else if (r.type === "error") {
      AV.showOp?.fail(r.error);
      AV.playing = null;
    }
    renderStage();
    renderDetails();
  }

  // ── the list ─────────────────────────────────────────────────────────

  async function load(wb, rev, refresh = false) {
    Object.assign(AV, { wb, rev, error: null });
    const op = AV.loading = Ops.start({ title: "List the animations", kind: "anim-list", steps: [{ id: "walk", label: "Read every .anim and .fbx (with its .meta)" }] });
    renderList();
    try {
      op.step("walk", "active", "the first time for a revision takes a few seconds");
      const r = await getJson(`${App.revUrl(wb, rev)}/animations${refresh ? "?refresh=true" : ""}`, op);
      if (AV.loading !== op) return;
      AV.clips = r.clips;
      AV.generated = r.generated ?? [];
      op.step("walk", "done", `${r.clips.length} clips`).done(`${r.clips.length} animation clips`);
      if (AV.selected) AV.selected = AV.clips.find((c) => keyOf(c) === keyOf(AV.selected)) ?? null;
    } catch (e) {
      op.fail(e);
      AV.error = describeError(e);
    }
    renderList();
    renderDetails();
  }

  const RIG = { Humanoid: "ok", Generic: "warn", Legacy: "warn" };
  function renderList() {
    const list = $("avList");
    const f = AV.filter.toLowerCase();
    const shown = AV.clips.filter((c) => !f || `${c.name} ${c.path}`.toLowerCase().includes(f));
    const group = (title, clips) => clips.length ? [h("li", { class: "grp" }, `${title} (${clips.length})`), clips.map(row)] : null;
    put(list,
      AV.loading?.running ? h("li", { class: "note" }, h("span", { class: "spin" }), " reading the workbench…") : null,
      AV.error ? h("li", { class: "note" }, AV.error) : null,
      !AV.loading?.running && !AV.error && AV.wb && !AV.clips.length ? h("li", { class: "note" }, "No animations in this revision.") : null,
      group("Humanoid", shown.filter((c) => c.humanoid)),
      group("Other rigs", shown.filter((c) => !c.humanoid)));
  }

  function row(c) {
    const len = c.length ? `${c.length.toFixed(2)} s` : c.frames ? `${c.frames} frames` : "";
    return h("li", {}, h("button", { class: "item anim-row", type: "button", "aria-current": AV.selected && keyOf(AV.selected) === keyOf(c) ? "true" : null,
      title: `${c.path}${c.source === "fbx" ? ` (clip ${c.name})` : ""}`, onclick: () => select(c) },
      h("span", { class: `dot ${RIG[c.rig] ?? ""}`, title: c.rig }),
      h("span", { class: "n" }, c.name, h("span", { class: "muted small" }, ` ${fileName(c.path)}`)),
      modelFor(c) ? h("span", { class: "badge", title: "A model was generated for it" }, "model") : null,
      h("span", { class: "muted small num" }, len)));
  }

  // ── previewing ───────────────────────────────────────────────────────

  async function select(c) {
    AV.selected = c;
    AV.quote = null;
    AV.gen = null;
    AV.prompt = "";
    AV.playing = null;
    renderList();
    renderDetails();
    await show(c);
  }

  // Have Pipeline build the clip's archive (an .anim, or the whole .fbx), then play it in the runtime.
  async function show(c) {
    if (!c.guid) { banner(`${fileName(c.path)} has no .meta GUID, so Pipeline can't build it.`); return; }
    const wb = AV.wb, rev = AV.rev, model = modelFor(c);
    AV.showOp?.stop("Another clip was picked.");
    const op = AV.showOp = Ops.start({ title: `Preview ${c.name}`, kind: "anim-show", asset: c.path,
      steps: [{ id: "archive", label: "Pipeline builds the clip's WebGL archive" }, { id: "play", label: "Load and play it" }] });
    renderStage();
    try {
      const r = await Archives.build(wb, rev, c.guid, op);
      if (AV.showOp !== op) return;
      op.step("play", "active");
      // humanoid: the character; own: the model the .fbx imports; rig: built from the clip's paths (+ a generated model).
      const mode = c.humanoid ? "humanoid" : c.source === "fbx" ? "own" : "rig";
      rt("Show", { key: c.guid, url: r.url, clip: c.name, mode, paths: c.paths,
        modelKey: model?.archive ?? "", modelUrl: model ? `/api/animations/archive/${enc(model.archive)}` : "" });
      rt("SetSpeed", { speed: AV.paused ? 0 : AV.speed });
    } catch (e) { if (AV.showOp === op) op.fail(e); }
    renderStage();
  }

  function setSpeed(s) {
    if (s !== null) AV.speed = s;
    rt("SetSpeed", { speed: AV.paused ? 0 : AV.speed });
    renderHead();
  }

  // ── generating a model for a clip that isn't humanoid ────────────────

  async function getQuote(c) {
    const op = Ops.start({ title: `Quote a model for ${c.name}`, kind: "anim-quote", asset: c.path, steps: [{ id: "quote", label: "unity ai quote (free)" }] });
    AV.gen = { state: "quoting" };
    renderDetails();
    try {
      op.step("quote", "active");
      AV.quote = await postJson("/api/animations/model-quote", { clip: keyOf(c), name: c.name, prompt: AV.prompt }, op);
      AV.prompt = AV.quote.prompt;
      op.step("quote", "done", `${AV.quote.pointsCost} points`).done(`${AV.quote.pointsCost} points (worst case ${AV.quote.worstCase}), ${AV.quote.orgName}`);
      AV.gen = null;
    } catch (e) { op.fail(e); AV.gen = { state: "failed", error: describeError(e) }; }
    renderDetails();
  }

  async function generate(c) {
    const q = AV.quote;
    if (!q) return;
    const ok = await ask({ title: `Spend Unity AI points on a model for ${c.name}?`,
      message: `${q.model}: ${q.pointsCost} points (up to ${q.worstCase} worst case), charged to ${q.orgName} (${q.org}). Prompt: "${q.prompt}". ` +
        "The open Unity Editor then imports it under Assets/AnimationViewer/Generated and packs it for the player.", ok: "Generate" });
    if (!ok) return;
    const op = Ops.start({ title: `Generate a model for ${c.name}`, kind: "anim-generate", asset: c.path,
      steps: [{ id: "gen", label: `unity ai generate (${q.model})` }, { id: "pack", label: "The Editor imports it and builds its WebGL archive" }] });
    AV.gen = { state: "running", op };
    renderDetails();
    try {
      op.step("gen", "active", "usually 1–3 minutes");
      const r = await postJson("/api/animations/model", { clip: keyOf(c), name: c.name, prompt: q.prompt, acceptedWorstCase: q.worstCase }, op);
      for (const s of r.steps ?? []) op.note(s);
      op.step("gen", "done").step("pack", "done", r.generated.archive);
      AV.generated.push(r.generated);
      op.done(`${r.generated.model}${r.generated.pointsCost != null ? `, ${r.generated.pointsCost} points` : ""}`);
      AV.gen = { state: "done", result: r.generated };
      AV.quote = null;
      renderList();
      if (AV.selected && keyOf(AV.selected) === keyOf(c)) show(c);
    } catch (e) {
      op.fail(e);
      AV.gen = { state: "failed", error: describeError(e) };
    }
    renderDetails();
  }

  // ── panels ───────────────────────────────────────────────────────────

  function renderStage() { renderHead(); renderOverlay(); }

  function renderHead() {
    const c = AV.selected;
    put($("avHead"),
      h("div", { class: "path" }, c ? [folderOf(c.path) + "/", h("b", {}, fileName(c.path)), c.source === "fbx" ? ` · ${c.name}` : ""] : "No clip selected"),
      h("span", { class: "grow" }),
      h("button", { class: "btn small", disabled: !AV.playing ? true : null, onclick: () => { AV.paused = !AV.paused; setSpeed(null); } }, AV.paused ? "Play" : "Pause"),
      h("select", { class: "btn small", "aria-label": "Speed", onchange: (e) => setSpeed(Number(e.target.value)) },
        [0.25, 0.5, 1, 2].map((s) => h("option", { value: s, selected: s === AV.speed ? true : null }, `${s}×`))),
      h("button", { class: "btn small", disabled: !AV.playing ? true : null, onclick: () => rt("Frame") }, "Frame"),
      h("span", { class: "muted small" }, "Drag: orbit · Wheel: zoom"));
  }

  function renderOverlay() {
    const ov = $("avOverlay");
    const run = AV.showOp?.running ? AV.showOp : null;
    let over = null, bottom = true;
    if (!AV.selected) { over = h("div", {}, h("b", {}, "Pick an animation."), h("div", {}, "Humanoid clips play on the character; others on their own model or a rig built for them.")); bottom = false; }
    else if (viewer?.failed) { over = h("div", {}, viewer.failed); bottom = false; }
    else if (AV.showOp?.state === "failed") over = h("div", {}, h("b", {}, "Couldn't preview it. "), AV.showOp.error);
    else {
      const starting = playerStartingEl(viewer);
      if (run || starting) over = h("div", {}, starting, run ? stepsEl(run) : null);
    }
    ov.hidden = !over;
    ov.className = "stage-overlay" + (bottom ? "" : " center");
    put(ov, over);
  }

  function renderDetails() {
    const box = $("avDetails");
    const c = AV.selected;
    if (!c) { put(box, h("div", { class: "muted small", style: "padding:12px 16px" }, "Select an animation.")); return; }
    const p = AV.playing && AV.playing.clip === c.name ? AV.playing : null;
    const len = p?.length ? `${p.length.toFixed(2)} s` : c.length ? `${c.length.toFixed(2)} s` : c.frames ? `${c.frames} frames` : "—";
    const model = modelFor(c);
    const on = c.humanoid ? "the humanoid character (retargeted through its Avatar)"
      : c.source === "fbx" ? `the model ${fileName(c.path)} imports`
      : model ? `a rig built from its ${c.paths.length} animated transform${c.paths.length === 1 ? "" : "s"}, with the generated model on each`
      : `a rig built from its ${c.paths.length} animated transform${c.paths.length === 1 ? "" : "s"} (boxes until a model is generated)`;
    put(box,
      h("div", { class: "section", style: "padding:12px 16px" },
        h("h2", {}, c.name),
        h("dl", { class: "facts" },
          h("dt", {}, "File"), h("dd", { class: "mono" }, c.path),
          h("dt", {}, "Rig"), h("dd", {}, c.rig, c.source === "fbx" ? h("span", { class: "muted small" }, " (ModelImporter animationType)") : null),
          h("dt", {}, "Length"), h("dd", {}, len, c.loop ? " · loops" : ""),
          h("dt", {}, "Plays on"), h("dd", {}, p ? `${p.target}` : on))),
      !c.humanoid && c.paths.length ? h("div", { class: "section", style: "padding:0 16px 12px" },
        h("h3", { class: "small muted" }, "Animated transforms"),
        h("ul", { class: "anim-paths mono small" }, c.paths.slice(0, 40).map((x) => h("li", {}, x)), c.paths.length > 40 ? h("li", {}, `+${c.paths.length - 40} more`) : null)) : null,
      c.humanoid ? null : generateEl(c, model));
    if (!c.humanoid) renderGenButtons(c);
  }

  function generateEl(c, model) {
    if (c.source === "fbx")
      return h("div", { class: "section anim-gen", style: "padding:0 16px 12px" },
        h("p", { class: "muted small" }, `This clip comes with its model (${fileName(c.path)}), so it plays on that.`));
    const g = AV.gen, q = AV.quote, busy = g?.state === "running" || g?.state === "quoting";
    return h("div", { class: "section anim-gen", style: "padding:0 16px 12px" },
      h("h3", {}, model ? "Generated model" : "Generate a model for this animation"),
      model ? h("p", { class: "small" }, h("span", { class: "mono" }, model.model), h("br"), h("span", { class: "muted" }, `“${model.prompt}”`,
        model.pointsCost != null ? ` · ${model.pointsCost} points` : "", model.org ? ` · ${model.org}` : "")) : null,
      h("p", { class: "muted small" }, "Unity AI makes a mesh from the prompt; the player puts it on a rig with this clip's transforms (same names and hierarchy), " +
        "so the clip moves it. Text-to-3D models come unrigged and AI rigging only makes biped or quadruped skeletons with their own bone names, so the rig is built from the clip, not by the generator."),
      h("textarea", { class: "anim-prompt", rows: 2, maxlength: 600, placeholder: `a simple low-poly 3D model of ${c.name.replace(/[_-]+/g, " ")}`, disabled: busy ? true : null,
        oninput: (e) => { AV.prompt = e.target.value; AV.quote = null; renderGenButtons(c); } }, AV.prompt),
      h("div", { class: "anim-gen-buttons", id: "avGenButtons" }),
      g?.state === "failed" ? h("div", { class: "callout bad" }, h("span", { class: "dot bad" }), h("div", {}, h("p", {}, g.error))) : null,
      g?.state === "running" ? h("p", { class: "small" }, h("span", { class: "spin" }), " generating, then packing in the Editor (a few minutes)…") : null);
  }

  // The quote, then the paid generation: two steps, so the cost and the org are always seen first.
  function renderGenButtons(c) {
    const el = $("avGenButtons");
    if (!el) return;
    const g = AV.gen, q = AV.quote, busy = g?.state === "running" || g?.state === "quoting";
    put(el,
      !q ? h("button", { class: "btn small", disabled: busy ? true : null, onclick: () => getQuote(c) }, g?.state === "quoting" ? "Getting a quote…" : modelFor(c) ? "Generate another (get a quote)" : "Get a quote (free)") : null,
      q ? h("div", { class: "small" }, h("b", {}, `${q.pointsCost} points`), ` (worst case ${q.worstCase}) · ${q.orgName} (${q.org}) · ${q.model}`) : null,
      q ? h("button", { class: "btn small primary", disabled: busy ? true : null, onclick: () => generate(c) }, `Generate a model for this animation (${q.pointsCost} points)`) : null,
      q ? h("button", { class: "btn small ghost", disabled: busy ? true : null, onclick: () => { AV.quote = null; renderDetails(); } }, "Cancel") : null);
  }

  // ── wiring ───────────────────────────────────────────────────────────

  $("avFilter").addEventListener("input", (e) => { AV.filter = e.target.value; renderList(); });
  $("avRefresh").addEventListener("click", () => AV.wb && load(AV.wb, AV.rev, true));
  App.on("ops", () => { if (AV.selected) renderOverlay(); });
  renderStage();
  renderDetails();
})();
