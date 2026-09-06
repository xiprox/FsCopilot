/*
 * A driver for MSFS's Coherent GT remote inspector.
 *
 * Answers Q00 in the affirmative: the simulator hosts a WebKit Web Inspector
 * backend on 127.0.0.1:19999 and it will evaluate arbitrary JavaScript in any
 * panel document. That makes every console probe in this project scriptable.
 *
 *   GET /pagelist.json                     the inspectable documents
 *   ws://127.0.0.1:19999/devtools/page/N   WebKit inspector protocol for one
 *
 * The page ids are assigned by the sim and change between sessions and aircraft,
 * so select by title rather than hardcoding a number. Panel titles come from
 * VCockpit.js and carry the instrument identifier — "VCockpit17 - WasmInstrument".
 *
 * Requires nothing. Node 22 has a global WebSocket.
 */

import { createConnection } from "node:net"

const HOST = "127.0.0.1"
const PORT = 19999

/** Minimal HTTP GET. The backend speaks HTTP/1.1 and closes on request. */
function httpGet(path, ms = 5000) {
  return new Promise((resolve, reject) => {
    const chunks = []
    const sock = createConnection({ host: HOST, port: PORT })
    sock.setTimeout(ms)
    sock.on("connect", () =>
      sock.write(`GET ${path} HTTP/1.1\r\nHost: ${HOST}:${PORT}\r\nConnection: close\r\n\r\n`))
    sock.on("data", (d) => chunks.push(d))
    sock.on("timeout", () => { sock.destroy(); finish() })
    sock.on("error", reject)
    sock.on("close", finish)
    function finish() {
      const raw = Buffer.concat(chunks).toString("utf8")
      const at = raw.indexOf("\r\n\r\n")
      if (at === -1) return reject(new Error(`no response body from ${path}`))
      resolve({ head: raw.slice(0, at), body: raw.slice(at + 4) })
    }
  })
}

/** Every document the sim will let us into, newest layout first.
 *  Panels are `VCockpitNN - <instrumentIdentifier>`; the rest are sim UI. */
export async function listPages() {
  const { body } = await httpGet("/pagelist.json")
  return JSON.parse(body)
}

/** Resolve a page by id, exact title, or case-insensitive title substring.
 *  Throws on ambiguity rather than picking one — the wrong panel produces a
 *  meaningless result rather than a weaker one. */
export function selectPage(pages, sel) {
  if (typeof sel === "number" || /^\d+$/.test(sel)) {
    const id = Number(sel)
    const hit = pages.find((p) => p.id === id)
    if (!hit) throw new Error(`no page with id ${id}`)
    return hit
  }
  const needle = String(sel).toLowerCase()
  const hits = pages.filter((p) => String(p.title || "").toLowerCase().includes(needle))
  if (!hits.length) throw new Error(`no page whose title contains ${JSON.stringify(sel)}`)
  if (hits.length > 1) {
    throw new Error(
      `${JSON.stringify(sel)} matches ${hits.length} pages — be more specific:\n` +
      hits.map((p) => `  ${String(p.id).padStart(3)}  ${p.title}`).join("\n"))
  }
  return hits[0]
}

export class Inspector {
  constructor(pageId) {
    this.pageId = pageId
    this.ws = null
    this.seq = 0
    this.pending = new Map()
    this.handlers = new Map()
  }

  /** Subscribe to a protocol event, e.g. "Console.messageAdded".
   *  The domain has to be enabled first — Console.enable, Page.enable — or the
   *  simulator sends nothing and the silence looks like a bug in the handler. */
  on(method, fn) {
    if (!this.handlers.has(method)) this.handlers.set(method, [])
    this.handlers.get(method).push(fn)
    return this
  }

  open(ms = 8000) {
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(`ws://${HOST}:${PORT}/devtools/page/${this.pageId}`)
      this.ws = ws
      const timer = setTimeout(() => reject(new Error("inspector connect timed out")), ms)
      ws.onopen = () => { clearTimeout(timer); resolve(this) }
      ws.onerror = () => { clearTimeout(timer); reject(new Error("inspector connection failed")) }
      ws.onclose = () => {
        // Fail anything still in flight rather than hanging the caller.
        for (const [, p] of this.pending) p.reject(new Error("inspector connection closed"))
        this.pending.clear()
      }
      ws.onmessage = (e) => {
        let msg
        try { msg = JSON.parse(String(e.data)) } catch { return }
        if (msg.id == null) {
          const fns = this.handlers.get(msg.method)
          if (fns) for (const fn of fns) {
            try { fn(msg.params || {}) } catch { /* a bad handler must not kill the socket */ }
          }
          return
        }
        const p = this.pending.get(msg.id)
        if (!p) return
        this.pending.delete(msg.id)
        p.resolve(msg)
      }
    })
  }

  send(method, params, ms = 20000) {
    const id = ++this.seq
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        reject(new Error(`${method} timed out after ${ms}ms`))
      }, ms)
      this.pending.set(id, {
        resolve: (m) => { clearTimeout(timer); resolve(m) },
        reject: (e) => { clearTimeout(timer); reject(e) }
      })
      this.ws.send(JSON.stringify({ id, method, params: params || {} }))
    })
  }

  /** Evaluate in the page and return the value. Throws on a page-side throw,
   *  which is the behaviour a caller almost always wants — a probe that blew up
   *  should not look like a probe that returned undefined. */
  async evaluate(expression, { returnByValue = true } = {}) {
    const msg = await this.send("Runtime.evaluate", {
      expression,
      returnByValue,
      includeCommandLineAPI: true,
      doNotPauseOnExceptionsAndMuteConsoleAPI: false
    })
    if (msg.error) throw new Error(`inspector error: ${JSON.stringify(msg.error)}`)
    const r = msg.result || {}
    if (r.wasThrown) {
      const d = (r.result && (r.result.description || r.result.value)) || "unknown"
      throw new Error(`page threw: ${d}`)
    }
    return r.result ? r.result.value : undefined
  }

  close() { try { this.ws && this.ws.close() } catch { /* already gone */ } }
}

/** console.* in a panel goes to the sim's log, not to us. Install a buffer so a
 *  probe written for a human console can be read back over the wire unchanged. */
export const CAPTURE_SHIM = `
(function () {
  if (window.__PPLOG) { window.__PPLOG.length = 0; return "reused" }
  var buf = window.__PPLOG = []
  var native = { log: console.log, warn: console.warn, error: console.error }
  window.__PPLOG_NATIVE = native
  function cap(kind, fn) {
    return function () {
      try {
        var parts = []
        for (var i = 0; i < arguments.length; i++) {
          var a = arguments[i]
          if (typeof a === "string") parts.push(a)
          else if (a instanceof Error) parts.push(String(a))
          else { try { parts.push(JSON.stringify(a)) } catch (e) { parts.push(String(a)) } }
        }
        buf.push((kind === "log" ? "" : "[" + kind + "] ") + parts.join(" "))
      } catch (e) { /* never break the page */ }
      try { fn.apply(console, arguments) } catch (e) {}
    }
  }
  console.log = cap("log", native.log)
  console.warn = cap("warn", native.warn)
  console.error = cap("error", native.error)
  return "installed"
})()`

/** Run a probe's source in a page and return whatever it printed.
 *
 *  Probes are async by nature — p01 fetches files, p03 waits on a
 *  MutationObserver — so this drains the buffer after `settle` ms rather than
 *  assuming the source finished when evaluate() returned. */
export async function runProbe(insp, source, { settle = 2500 } = {}) {
  await insp.evaluate(CAPTURE_SHIM)
  await insp.evaluate(`(function(){ try { ${JSON.stringify(source)} && eval(${JSON.stringify(source)}); return "ok" } catch (e) { console.error("probe threw: " + e); return "threw" } })()`)
  await new Promise((r) => setTimeout(r, settle))
  return drain(insp)
}

/** Read and clear the capture buffer. */
export async function drain(insp) {
  const out = await insp.evaluate(`(function(){ var b = window.__PPLOG || []; var s = b.join("\\n"); b.length = 0; return s })()`)
  return out || ""
}
