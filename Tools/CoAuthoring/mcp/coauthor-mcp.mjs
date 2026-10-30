#!/usr/bin/env node
// MCP server (stdio) for agents such as Claude Code: join a co-authoring session of the Pipeline Explorer as one
// more participant. Agents send the same Unity CLI commands they would run locally ("create_gameobject --name Crate
// --primitive Cube"); they go through the session's queue, in order with everyone else's, so people watching the
// Co-authoring tab see them land.
//
//   COAUTHOR_URL=http://127.0.0.1:5280 COAUTHOR_WORKBENCH=<workbench id> COAUTHOR_SCENE=Assets/Scenes/X.unity \
//   COAUTHOR_ACTOR=claude-code node coauthor-mcp.mjs
//
// Claude Code: claude mcp add coauthor -e COAUTHOR_WORKBENCH=<id> -e COAUTHOR_SCENE=<scene> -- node <path>/coauthor-mcp.mjs
import { createInterface } from "node:readline";

const url = (process.env.COAUTHOR_URL ?? "http://127.0.0.1:5280").replace(/\/$/, "");
const workbench = process.env.COAUTHOR_WORKBENCH ?? "";
const actor = process.env.COAUTHOR_ACTOR ?? "claude-code";
let scene = process.env.COAUTHOR_SCENE ?? null;
const base = `${url}/api/coauthor/v1/workbenches/${workbench}`;

async function api(method, path, body) {
  if (!workbench) throw new Error("Set COAUTHOR_WORKBENCH to the workbench ID (the Explorer shows it).");
  const res = await fetch(base + path, {
    method,
    headers: { "X-Pipeline-Explorer": "1", "X-Actor": actor, "X-Actor-Kind": "agent", "Content-Type": "application/json" },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  const json = text ? JSON.parse(text) : null;
  if (!res.ok) throw new Error(json?.error ?? `HTTP ${res.status}`);
  return json;
}

/** Tool name → [description, input schema, handler]. */
const tools = {
  unity_command: [
    "Runs Unity CLI commands (the pipeline package's: create_gameobject, set_transform, instantiate_prefab, add_component, " +
      "set_component_properties, get_scene_hierarchy, …) on the shared scene, as `unity command` would. Each item is a command " +
      "line or {command, params}. \"$<index>.<path>\" uses an earlier result (e.g. \"$0.globalId\"). With transaction: true they " +
      "apply all or nothing. Others edit at the same time: a failed command changed nothing; look again and resend.",
    {
      type: "object",
      properties: {
        commands: { type: "array", items: { anyOf: [{ type: "string" }, { type: "object" }] }, minItems: 1 },
        transaction: { type: "boolean" },
      },
      required: ["commands"],
    },
    async ({ commands, transaction }) => {
      const r = await api("POST", "/commands", { commands, transaction: !!transaction, scene });
      // open_scene changes the scene later commands edit.
      commands.forEach((c, i) => { if (r.results[i]?.status === "ok" && r.results[i].command === "open_scene") scene = r.results[i].result.path; });
      return r;
    },
  ],
  list_commands: [
    "Lists the commands unity_command accepts, with their parameters.",
    { type: "object", properties: {} },
    async () => api("GET", "/commands?detail=full"),
  ],
  find_assets: [
    "Lists the project's asset paths containing query (e.g. \"Barrel\", \".prefab\", \"Toon Gas Station/Prefabs\"), to use with instantiate_prefab.",
    { type: "object", properties: { query: { type: "string" } }, required: ["query"] },
    async ({ query }) => api("GET", `/assets?q=${encodeURIComponent(query ?? "")}`),
  ],
  who_is_here: [
    "Lists who is in the session (people and agents), where they are, and the changes not saved yet.",
    { type: "object", properties: {} },
    async () => api("GET", "/draft"),
  ],
};

// ── MCP over stdio (newline-delimited JSON-RPC) ─────────────────────────

const send = (msg) => process.stdout.write(JSON.stringify({ jsonrpc: "2.0", ...msg }) + "\n");

async function handle(msg) {
  const { id, method, params } = msg;
  if (id === undefined) return;   // notifications
  try {
    switch (method) {
      case "initialize":
        return send({ id, result: {
          protocolVersion: params?.protocolVersion ?? "2025-06-18",
          capabilities: { tools: {} },
          serverInfo: { name: "coauthor", version: "0.1.0" },
          instructions: `Edits a Unity scene people are watching live${scene ? ` (${scene})` : ""}, with Unity CLI commands. Start with get_scene_hierarchy; open another scene with open_scene --path.`,
        } });
      case "ping":
        return send({ id, result: {} });
      case "tools/list":
        return send({ id, result: { tools: Object.entries(tools).map(([name, [description, inputSchema]]) => ({ name, description, inputSchema })) } });
      case "tools/call": {
        const tool = tools[params?.name];
        if (!tool) return send({ id, error: { code: -32602, message: `Unknown tool ${params?.name}` } });
        try {
          const result = await tool[2](params.arguments ?? {});
          return send({ id, result: { content: [{ type: "text", text: JSON.stringify(result, null, 1) }] } });
        } catch (e) {
          return send({ id, result: { content: [{ type: "text", text: String(e.message ?? e) }], isError: true } });
        }
      }
      default:
        return send({ id, error: { code: -32601, message: `Method not found: ${method}` } });
    }
  } catch (e) {
    send({ id, error: { code: -32603, message: String(e.message ?? e) } });
  }
}

createInterface({ input: process.stdin }).on("line", (line) => {
  if (!line.trim()) return;
  try { handle(JSON.parse(line)); } catch { /* not JSON */ }
});
