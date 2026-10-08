// Unity YAML (prefabs, scenes, ScriptableObjects, materials) for the "Edit a project" example.
// Not a general YAML parser: it reads the subset Unity writes, and remembers where every value sits
// in the text, so an edit replaces those characters and leaves the rest of the file byte for byte.
"use strict";

const UnityYaml = (() => {
  // Class IDs the hierarchy cares about (https://docs.unity3d.com/Manual/ClassIDReference.html).
  const CLASS = { GameObject: 1, Transform: 4, RectTransform: 224, MonoBehaviour: 114, PrefabInstance: 1001 };

  /**
   * Parse a Unity YAML file.
   * Returns { lines, eol, docs: [{ classId, fileId, stripped, type, line, root }] } where each node is
   * { key, raw, line, start, end, indent, kind: "scalar"|"flow"|"seq"|"map"|"block", children, items, readOnly }.
   * Scalars and flow-map items carry [start, end) offsets of their value in lines[line].
   */
  function parse(text) {
    const eol = text.includes("\r\n") ? "\r\n" : "\n";
    const lines = text.split(/\r?\n/);
    const docs = [];
    let doc = null, stack = null;
    // A value Unity wrapped onto the next lines (a long flow map or quoted string): those lines
    // belong to it, and it can't be edited in place.
    let wrapped = null;

    for (let n = 0; n < lines.length; n++) {
      const line = lines[n];
      if (wrapped) {
        wrapped.node.readOnly = true;
        if (wrapped.ends(line.trim())) wrapped = null;
        continue;
      }
      const header = /^--- !u!(\d+) &(-?\d+)( stripped)?/.exec(line);
      if (header) {
        doc = { classId: Number(header[1]), fileId: header[2], stripped: !!header[3], type: null, line: n, root: null };
        docs.push(doc);
        stack = null;
        continue;
      }
      if (!doc || line.startsWith("%") || !line.trim()) continue;
      const indent = line.length - line.trimStart().length;
      if (indent === 0) {
        // "TypeName:" opens the document's body.
        doc.type = line.replace(/:\s*$/, "");
        doc.root = node(doc.type, "", n, 0, 0, 0, "map");
        doc.root.ci = 2;
        stack = [doc.root];
        continue;
      }
      if (!stack) continue;
      const body = line.slice(indent);

      if (body === "-" || body.startsWith("- ")) {
        const parent = popUntil(stack, (c) => acceptsItem(c, indent));
        if (!parent) continue;
        parent.listIndent = indent;
        parent.kind = "seq";
        const item = node(`${parent.children.length}`, "", n, indent, indent + 2, indent + 2, "map");
        item.isItem = true;
        parent.children.push(item);
        const rest = body.slice(2);
        const kv = splitKey(rest);
        if (kv) {
          item.ci = indent + 2;
          stack.push(item);
          addKey(stack, item, line, n, indent + 2, kv);
        } else {
          // A bare item: a scalar or a flow map/list.
          setValue(item, line, n, indent + 2, rest);
        }
        wrapped = wrapsFrom(lastValue(item));
        continue;
      }

      const kv = splitKey(body);
      if (!kv) {
        // A continuation of a multi-line scalar: the value above can't be edited safely.
        const last = lastScalar(stack);
        if (last) last.readOnly = true;
        continue;
      }
      const parent = popUntil(stack, (c) => acceptsKey(c, indent));
      if (!parent) continue;
      addKey(stack, parent, line, n, indent, kv);
      wrapped = wrapsFrom(parent.children[parent.children.length - 1]);
    }
    return { lines, eol, docs: docs.filter((d) => d.root) };
  }

  const lastValue = (item) => (item.children.length ? item.children[item.children.length - 1] : item);

  // Does this value carry on to the next line? Then return how to find its last line.
  function wrapsFrom(nd) {
    if (!nd) return null;
    const v = (nd.raw ?? "").trim();
    if (nd.kind === "flow" || nd.kind === "flowseq") {
      let depth = 0;
      for (const ch of v) depth += ch === "{" || ch === "[" ? 1 : ch === "}" || ch === "]" ? -1 : 0;
      if (depth <= 0) return null;
      nd.readOnly = true;
      nd.items = null;
      return { node: nd, ends: (s) => { for (const ch of s) depth += ch === "{" || ch === "[" ? 1 : ch === "}" || ch === "]" ? -1 : 0; return depth <= 0; } };
    }
    if (nd.kind === "scalar" && (v[0] === "'" || v[0] === '"') && !closed(v, v[0])) {
      const q = v[0];
      nd.readOnly = true;
      return { node: nd, ends: (s) => s.endsWith(q) && (q !== "'" || /(^|[^'])('')*'$/.test(s)) };
    }
    return null;
  }

  function node(key, raw, line, start, end, indent, kind) {
    return { key, raw, line, start, end, indent, kind, children: [], items: null, readOnly: false };
  }

  // "key: value" (value may be empty). Keys are plain in Unity's files.
  function splitKey(s) {
    const m = /^([^\s'"{}\[\]#:][^:]*?):(?: (.*)|$)/.exec(s);
    if (!m) return null;
    return { key: m[1], value: m[2] ?? "", valueOffset: m[2] === undefined ? s.length : m[1].length + 2 };
  }

  function addKey(stack, parent, line, n, indent, kv) {
    if (parent.ci === undefined) parent.ci = indent;
    parent.kind = parent.kind === "block" ? "map" : parent.kind;
    const start = indent + kv.valueOffset;
    const child = node(kv.key, kv.value, n, start, line.length, indent, "scalar");
    parent.children.push(child);
    if (kv.value === "" || /^\s*$/.test(kv.value)) {
      // Empty: either an empty string, or a block map/list on the lines below. Decided by what follows.
      child.kind = "block";
      child.end = start + kv.value.length;
      stack.push(child);
    } else {
      setValue(child, line, n, start, kv.value);
    }
  }

  function setValue(nd, line, n, start, value) {
    nd.line = n;
    nd.start = start;
    nd.end = start + value.length;
    nd.raw = value;
    const v = value.trim();
    if (v.startsWith("{")) {
      nd.kind = "flow";
      nd.items = parseFlow(value, start);
      if (!nd.items) nd.readOnly = true;
      else for (const it of nd.items) it.line = n;
    } else if (v.startsWith("[")) {
      nd.kind = "flowseq";
      nd.readOnly = true;
    } else {
      nd.kind = "scalar";
      const q = v[0];
      if ((q === "'" || q === '"') && !closed(v, q)) nd.readOnly = true;   // continues on the next lines
      if (v.startsWith("|") || v.startsWith(">") || v.startsWith("&") || v.startsWith("*") || v.startsWith("!")) nd.readOnly = true;
    }
  }

  function closed(v, q) {
    if (v.length < 2 || v[v.length - 1] !== q) return false;
    if (q === "'") {
      // '' is an escaped quote: the closing quote is the last one when the count is even.
      const inner = v.slice(1, -1);
      return !/(^|[^'])('')*'$/.test(inner);
    }
    let i = v.length - 2, slashes = 0;
    while (i > 0 && v[i] === "\\") { slashes++; i--; }
    return slashes % 2 === 0;
  }

  // {a: 1, b: 2} → [{ key, raw, start, end }] with offsets into the line. Nested braces aren't editable.
  function parseFlow(value, base) {
    const open = value.indexOf("{"), close = value.lastIndexOf("}");
    if (close < open) return null;
    const inner = value.slice(open + 1, close);
    if (/[{}\[\]]/.test(inner)) return null;
    const items = [];
    let partStart = base + open + 1;
    for (const part of inner.split(",")) {
      const colon = part.indexOf(":");
      if (colon < 0) return null;
      let v = colon + 1;
      if (part[v] === " ") v++;
      const raw = part.slice(v).replace(/\s+$/, "");
      items.push({ key: part.slice(0, colon).trim(), raw, start: partStart + v, end: partStart + v + raw.length });
      partStart += part.length + 1;
    }
    return items;
  }

  function popUntil(stack, ok) {
    while (stack.length && !ok(stack[stack.length - 1])) stack.pop();
    return stack[stack.length - 1] ?? null;
  }

  function acceptsKey(c, indent) {
    if (c.isItem) return c.ci === indent;
    if (c.indent === 0 && c.ci === 2) return indent === 2;   // the document's root
    if (c.kind === "seq") return false;
    if (c.kind !== "block" && c.kind !== "map") return false;
    return indent > c.indent && (c.ci === undefined || c.ci === indent);
  }

  function acceptsItem(c, indent) {
    if (c.kind === "block" && !c.children.length) return indent >= c.indent;
    if (c.kind === "seq") return c.listIndent === indent;
    return false;
  }

  function lastScalar(stack) {
    for (let i = stack.length - 1; i >= 0; i--) {
      const last = stack[i].children[stack[i].children.length - 1];
      if (last && (last.kind === "scalar" || last.kind === "flow")) return last;
    }
    return null;
  }

  // ── reading values ──────────────────────────────────────────────────────

  const child = (nd, key) => nd?.children.find((c) => c.key === key) ?? null;
  const flowValue = (nd, key) => nd?.items?.find((i) => i.key === key)?.raw ?? null;

  /** Plain text of a scalar, unquoted. */
  function scalarText(raw) {
    const v = (raw ?? "").trim();
    if (v.length >= 2 && v[0] === "'" && v.endsWith("'")) return v.slice(1, -1).replace(/''/g, "'");
    if (v.length >= 2 && v[0] === '"' && v.endsWith('"')) {
      try { return JSON.parse(v); } catch { return v.slice(1, -1); }
    }
    return v;
  }

  /** A value written back as Unity would: plain when that's unambiguous, else single-quoted. */
  function formatString(text, previousRaw) {
    const wasQuoted = /^\s*['"]/.test(previousRaw ?? "");
    const needsQuotes = text !== text.trim() || /^[-?:,\[\]{}#&*!|>'"%@`]/.test(text) || /: |\s#/.test(text) || /^\s*$/.test(text) && text.length > 0;
    if (!wasQuoted && !needsQuotes) return text;
    return `'${text.replace(/'/g, "''")}'`;
  }

  /** A reference ({fileID, guid, type}) as an object. */
  function ref(nd) {
    if (nd?.kind !== "flow" || !nd.items?.some((i) => i.key === "fileID")) return null;
    return { fileId: flowValue(nd, "fileID"), guid: flowValue(nd, "guid"), type: flowValue(nd, "type") };
  }

  // ── the hierarchy: GameObjects with their components, prefab instances, other objects ──

  function model(parsed) {
    const byId = new Map(parsed.docs.map((d) => [d.fileId, d]));
    const name = (d) => {
      const n = scalarText(child(d.root, "m_Name")?.raw);
      return n || null;
    };
    const gos = parsed.docs.filter((d) => d.classId === CLASS.GameObject && !d.stripped);
    const transformOf = new Map();   // GameObject fileId → transform doc
    const goOfTransform = new Map(); // transform fileId → GameObject doc
    const componentsOf = new Map();
    for (const go of gos) {
      const comps = (child(go.root, "m_Component")?.children ?? [])
        .map((item) => ref(child(item, "component") ?? item)?.fileId)
        .map((id) => byId.get(id)).filter(Boolean);
      componentsOf.set(go.fileId, comps);
      const t = comps.find((c) => c.classId === CLASS.Transform || c.classId === CLASS.RectTransform);
      if (t) { transformOf.set(go.fileId, t); goOfTransform.set(t.fileId, go); }
    }

    // Prefab instances hang under a transform (m_TransformParent); their name comes from a m_Name modification.
    const instances = parsed.docs.filter((d) => d.classId === CLASS.PrefabInstance && !d.stripped);
    const instanceName = (d) => {
      const mods = child(child(d.root, "m_Modification"), "m_Modifications")?.children ?? [];
      const named = mods.find((m) => scalarText(child(m, "propertyPath")?.raw) === "m_Name");
      return named ? scalarText(child(named, "value")?.raw) : null;
    };
    const instanceParent = (d) => ref(child(child(d.root, "m_Modification"), "m_TransformParent"))?.fileId ?? "0";

    const nodeFor = (doc, kind, label) => ({ doc, kind, label, children: [] });
    const goNode = new Map(gos.map((go) => [go.fileId, nodeFor(go, "go", name(go) ?? "(unnamed)")]));
    const instNode = new Map(instances.map((d) => [d.fileId, nodeFor(d, "instance", instanceName(d) ?? "Prefab instance")]));
    // A stripped transform stands in for an object inside a prefab instance.
    const strippedOwner = (id) => {
      const d = byId.get(id);
      if (!d?.stripped) return null;
      const inst = ref(child(d.root, "m_PrefabInstance"))?.fileId;
      return instNode.get(inst) ?? null;
    };

    const roots = [];
    const attach = (parentTransformId, n) => {
      const owner = goOfTransform.get(parentTransformId);
      const parent = owner ? goNode.get(owner.fileId) : strippedOwner(parentTransformId);
      (parent ? parent.children : roots).push(n);
    };
    for (const go of gos) {
      const t = transformOf.get(go.fileId);
      const father = t ? ref(child(t.root, "m_Father"))?.fileId ?? "0" : "0";
      attach(father, goNode.get(go.fileId));
    }
    for (const d of instances) attach(instanceParent(d), instNode.get(d.fileId));

    // Keep each parent's children in its transform's m_Children order.
    const order = (n) => {
      const t = n.kind === "go" ? transformOf.get(n.doc.fileId) : null;
      const ids = (child(t?.root, "m_Children")?.children ?? []).map((c) => ref(c)?.fileId);
      const rank = (c) => {
        const tid = c.kind === "go" ? transformOf.get(c.doc.fileId)?.fileId : null;
        const i = tid ? ids.indexOf(tid) : -1;
        return i < 0 ? 1e9 : i;
      };
      n.children.sort((a, b) => rank(a) - rank(b));
      n.children.forEach(order);
    };
    roots.forEach(order);

    const used = new Set([...gos.map((g) => g.fileId), ...instances.map((d) => d.fileId)]);
    for (const comps of componentsOf.values()) for (const c of comps) used.add(c.fileId);
    const others = parsed.docs.filter((d) => !used.has(d.fileId) && !d.stripped).map((d) => nodeFor(d, "object", name(d) ?? d.type));

    return { byId, roots, others, componentsOf, name };
  }

  // ── editing ─────────────────────────────────────────────────────────────

  /** Apply edits ({line, start, end, text}) to the original lines; everything else is unchanged. */
  function apply(parsed, edits) {
    const lines = parsed.lines.slice();
    const byLine = new Map();
    for (const e of edits) (byLine.get(e.line) ?? byLine.set(e.line, []).get(e.line)).push(e);
    for (const [n, list] of byLine) {
      let s = lines[n];
      for (const e of list.sort((a, b) => b.start - a.start)) s = s.slice(0, e.start) + e.text + s.slice(e.end);
      lines[n] = s;
    }
    return lines.join(parsed.eol);
  }

  return { parse, model, apply, child, ref, scalarText, formatString, flowValue, CLASS };
})();

if (typeof module !== "undefined") module.exports = UnityYaml;
