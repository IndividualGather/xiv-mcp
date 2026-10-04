#!/usr/bin/env node
// XIV MCP bridge for Claude Desktop: speaks MCP over stdio (one JSON-RPC message per line) and forwards every message to the XIV MCP
// server running inside Final Fantasy XIV (HTTP on localhost). No dependencies: it runs on the Node.js that ships with Claude Desktop.
//
// While the game isn't running, the bridge still answers, with no tools, and checks every few seconds. When the server appears it
// connects and tells Claude the tool list changed, so the tools show up without restarting Claude.
"use strict";
const http = require("http");
const readline = require("readline");

const URL_ = new URL(process.env.XIVMCP_URL || "http://localhost:37521/mcp");
const TOKEN = process.env.XIVMCP_TOKEN || "";
const OFFLINE = "Final Fantasy XIV isn't running, or the XIV MCP server is switched off. Start the game with Dalamud and try again.";
const REFUSED = "XIV MCP refused the access token. It was probably regenerated: install the connector again from /xivmcp → Connect.";
const RETRY_MS = 5000;

let session = null;        // Mcp-Session-Id from the server
let online = false;
let initParams = null;     // the client's initialize request, replayed when the server (re)appears
let reconnecting = null;
let stream = null;         // the server -> client notification stream
let internalId = 0;
let reason = OFFLINE;      // what to tell the client while the server can't be used

const log = (...a) => process.stderr.write(`[xivmcp-bridge] ${a.join(" ")}\n`);
const send = (msg) => process.stdout.write(JSON.stringify(msg) + "\n");
const reply = (id, result) => send({ jsonrpc: "2.0", id, result });
const fail = (id, message, code = -32000) => send({ jsonrpc: "2.0", id, error: { code, message } });

function headers(extra) {
  const h = { "Content-Type": "application/json", Accept: "application/json, text/event-stream", ...extra };
  if (TOKEN) h.Authorization = `Bearer ${TOKEN}`;
  if (session) h["Mcp-Session-Id"] = session;
  return h;
}

/** POSTs one message. Resolves { status, messages } (the JSON-RPC messages in the reply); rejects when the server can't be reached. */
function post(msg) {
  return new Promise((resolve, reject) => {
    const body = JSON.stringify(msg);
    const req = http.request(URL_, { method: "POST", headers: headers({ "Content-Length": Buffer.byteLength(body) }) }, (res) => {
      const id = res.headers["mcp-session-id"];
      if (id) session = id;
      let text = "";
      res.setEncoding("utf8");
      res.on("data", (c) => (text += c));
      res.on("end", () => resolve({ status: res.statusCode, messages: parse(text, res.headers["content-type"] || "") }));
    });
    req.on("error", reject);
    req.end(body);
  });
}

function parse(text, type) {
  if (!text.trim() || !/json|event-stream/.test(type)) return []; // plain-text errors (e.g. a refused token) are handled by status
  try {
    if (type.includes("text/event-stream"))
      return text.split(/\r?\n/).filter((l) => l.startsWith("data:")).map((l) => JSON.parse(l.slice(5)));
    const json = JSON.parse(text);
    return Array.isArray(json) ? json : [json];
  } catch (e) {
    log("unreadable reply:", e.message);
    return [];
  }
}

/** Answers while the server can't be reached: an empty but working server, so Claude keeps the connection. */
function answerOffline(msg) {
  if (msg.id === undefined) return; // notifications: nothing to say
  switch (msg.method) {
    case "initialize":
      return reply(msg.id, {
        protocolVersion: msg.params?.protocolVersion || "2025-06-18",
        capabilities: { tools: { listChanged: true }, resources: { subscribe: true, listChanged: false } },
        serverInfo: { name: "xiv-mcp", title: "Final Fantasy XIV (waiting for the game)", version: "bridge" },
        instructions: reason,
      });
    case "ping": return reply(msg.id, {});
    case "tools/list": return reply(msg.id, { tools: [] });
    case "resources/list": return reply(msg.id, { resources: [] });
    case "resources/templates/list": return reply(msg.id, { resourceTemplates: [] });
    case "prompts/list": return reply(msg.id, { prompts: [] });
    case "tools/call": return reply(msg.id, { content: [{ type: "text", text: reason }], isError: true });
    default: return fail(msg.id, reason);
  }
}

/** Connects (or reconnects) to the server with the client's initialize parameters. Returns the server's initialize reply or null. */
async function handshake() {
  session = null;
  const res = await post({ jsonrpc: "2.0", id: `bridge-${++internalId}`, method: "initialize", params: initParams });
  if (res.status === 401) throw new Error("unauthorized");
  const init = res.messages.find((m) => m.result || m.error);
  if (!init?.result) return null;
  await post({ jsonrpc: "2.0", method: "notifications/initialized" });
  return init;
}

function goOnline() {
  if (online) return;
  online = true;
  log("connected to", URL_.href);
  openStream();
}

function goOffline(why) {
  if (!online && reconnecting) return;
  if (online) log("lost the server:", why);
  online = false;
  session = null;
  if (stream) { stream.destroy(); stream = null; }
  if (!reconnecting && initParams) reconnecting = setTimeout(retry, RETRY_MS);
}

async function retry() {
  reconnecting = null;
  try {
    if (await handshake()) {
      goOnline();
      send({ jsonrpc: "2.0", method: "notifications/tools/list_changed" }); // the game's tools are here now
      return;
    }
  } catch { /* still away */ }
  reconnecting = setTimeout(retry, RETRY_MS);
}

/** Listens for server notifications (tool list changes, resource updates) and passes them on. */
function openStream() {
  if (stream || !session) return;
  const req = http.request(URL_, { method: "GET", headers: headers({ Accept: "text/event-stream" }) }, (res) => {
    if (res.statusCode !== 200) { res.resume(); stream = null; return; }
    let buffer = "";
    res.setEncoding("utf8");
    res.on("data", (chunk) => {
      buffer += chunk;
      let end;
      while ((end = buffer.indexOf("\n\n")) >= 0) {
        const frame = buffer.slice(0, end);
        buffer = buffer.slice(end + 2);
        const data = frame.split(/\r?\n/).filter((l) => l.startsWith("data:")).map((l) => l.slice(5)).join("\n");
        if (data) try { send(JSON.parse(data)); } catch { /* not JSON: ignore */ }
      }
    });
    res.on("end", () => { stream = null; if (online) setTimeout(openStream, 3000); });
  });
  req.on("error", () => { stream = null; });
  req.end();
  stream = req;
}

async function handle(msg) {
  if (msg.method === "initialize") initParams = msg.params;
  if (!online && msg.method !== "initialize") return answerOffline(msg);
  try {
    let res = await post(msg);
    if (res.status === 404 && session && msg.method !== "initialize") {
      // The server restarted (game or plugin reload) and forgot the session: connect again and resend.
      if (await handshake()) res = await post(msg);
    }
    if (res.status === 401) {
      reason = REFUSED;
      if (msg.id !== undefined) fail(msg.id, REFUSED);
      return;
    }
    for (const m of res.messages) send(m);
    if (msg.method === "initialize" && res.messages.some((m) => m.result)) { reason = OFFLINE; goOnline(); }
    else if (msg.id !== undefined && res.messages.length === 0 && res.status >= 400) fail(msg.id, `XIV MCP answered with HTTP ${res.status}.`);
  } catch (e) {
    goOffline(e.code || e.message);
    answerOffline(msg);
  }
}

// Messages are handled in order, one at a time, except tool calls, which may run long and must not hold up pings or cancellations.
let queue = Promise.resolve();
readline.createInterface({ input: process.stdin }).on("line", (line) => {
  if (!line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch { return fail(null, "Parse error", -32700); }
  if (msg.method === "tools/call") handle(msg);
  else queue = queue.then(() => handle(msg));
}).on("close", () => process.exit(0));

log("started, server", URL_.href);
