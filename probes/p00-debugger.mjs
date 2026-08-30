/*
 * P00 — Is the Coherent GT debugger scriptable from outside the sim?
 *
 * Settles Q00. If it is, every console probe in this project becomes a script
 * Claude can run and re-run; if it is not, they get pasted by hand and we stop
 * wondering.
 *
 * Run with MSFS in a flight AND the Coherent GT Debugger window open — the
 * listener may belong to the debugger rather than to the sim, which is itself
 * one of the things this tells us.
 *
 *   npm run probe:00
 *   npm run probe:00 -- --port 19999
 *
 * Writes a transcript to results/. Nothing here mutates the sim.
 */

import { createConnection } from "node:net"
import { execFileSync } from "node:child_process"
import { mkdirSync, writeFileSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, join } from "node:path"

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..")
const HOST = "127.0.0.1"

const portArg = process.argv.indexOf("--port")
const PORT = portArg >= 0 ? Number(process.argv[portArg + 1]) : 19999

const out = []
const say = (s = "") => { out.push(String(s)); console.log(s) }

/** Paths worth trying. The first block is Chrome DevTools Protocol, the second
 *  is WebKit Web Inspector, which is what Coherent GT is built on. */
const HTTP_PATHS = [
  "/", "/json", "/json/list", "/json/version", "/list", "/list.json",
  "/index.html", "/inspector", "/views", "/pages", "/debug"
]
const WS_PATHS = ["/", "/devtools/page/1", "/inspector", "/socket", "/ws", "/debug"]

// ---------------------------------------------------------------- who is there

function listeners() {
  try {
    const ps = execFileSync("powershell", [
      "-NoProfile", "-Command",
      "Get-NetTCPConnection -State Listen | Where-Object { $_.LocalPort -ge 19000 -and $_.LocalPort -le 21000 } | " +
      "ForEach-Object { $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue; " +
      "'{0}:{1} pid={2} {3}' -f $_.LocalAddress, $_.LocalPort, $_.OwningProcess, $p.ProcessName }"
    ], { encoding: "utf8", timeout: 15000 })
    return ps.trim() || "(nothing listening in 19000-21000)"
  } catch (e) {
    return `(port scan failed: ${e.message})`
  }
}

// ------------------------------------------------------------------ raw socket

/** Connect, optionally send something, and report whatever comes back.
 *  A server that speaks first is a strong hint about its protocol. */
function raw(send, label, ms = 2500) {
  return new Promise((resolve) => {
    const chunks = []
    let settled = false
    const done = (verdict) => {
      if (settled) return
      settled = true
      sock.destroy()
      const body = Buffer.concat(chunks).toString("latin1")
      resolve({ label, verdict, bytes: body.length, body: body.slice(0, 400) })
    }
    const sock = createConnection({ host: HOST, port: PORT })
    sock.setTimeout(ms)
    sock.on("connect", () => { if (send) sock.write(send) })
    sock.on("data", (d) => { chunks.push(d); if (Buffer.concat(chunks).length > 4096) done("data") })
    sock.on("timeout", () => done(chunks.length ? "data then idle" : "silence"))
    sock.on("error", (e) => done(`error: ${e.code || e.message}`))
    sock.on("close", () => done(chunks.length ? "data then close" : "closed with no data"))
  })
}

function httpGet(path) {
  return raw(
    `GET ${path} HTTP/1.1\r\nHost: ${HOST}:${PORT}\r\nAccept: */*\r\nConnection: close\r\n\r\n`,
    `GET ${path}`
  )
}

function wsUpgrade(path) {
  const key = Buffer.from(String(Math.random()).slice(2, 18)).toString("base64")
  return raw(
    `GET ${path} HTTP/1.1\r\nHost: ${HOST}:${PORT}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
    `Sec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\nOrigin: http://${HOST}:${PORT}\r\n\r\n`,
    `WS ${path}`
  )
}

// ------------------------------------------------------------------------ main

say("=".repeat(72))
say("P00  Coherent GT debugger reachability")
say(`     ${HOST}:${PORT}   ${new Date().toISOString()}`)
say("=".repeat(72))
say()
say("--- listening in 19000-21000 ---")
say(listeners())

say()
say("--- does it speak first? ---")
const first = await raw(null, "connect and wait")
say(`  ${first.verdict}  (${first.bytes} bytes)`)
if (first.body) say(`  ${JSON.stringify(first.body)}`)

say()
say("--- HTTP ---")
for (const p of HTTP_PATHS) {
  const r = await httpGet(p)
  const head = r.body.split("\r\n")[0] || ""
  say(`  ${r.verdict.padEnd(20)} ${String(r.bytes).padStart(6)}B  ${p}${head ? "   " + head : ""}`)
  if (r.bytes && !/^HTTP\//.test(r.body)) say(`      raw: ${JSON.stringify(r.body.slice(0, 200))}`)
}

say()
say("--- WebSocket upgrade ---")
for (const p of WS_PATHS) {
  const r = await wsUpgrade(p)
  const head = r.body.split("\r\n")[0] || ""
  say(`  ${r.verdict.padEnd(20)} ${String(r.bytes).padStart(6)}B  ${p}${head ? "   " + head : ""}`)
}

say()
say("--- a few non-HTTP openers ---")
// Coherent's own debugger may expect a length-prefixed or newline-delimited
// JSON-RPC rather than an HTTP handshake.
for (const [send, label] of [
  ["\r\n", "bare CRLF"],
  ['{"id":1,"method":"Runtime.evaluate","params":{"expression":"1+1"}}\n', "JSON-RPC line"],
  ["COHERENT\r\n", "COHERENT token"]
]) {
  const r = await raw(send, label)
  say(`  ${r.verdict.padEnd(20)} ${String(r.bytes).padStart(6)}B  ${label}`)
  if (r.bytes) say(`      raw: ${JSON.stringify(r.body.slice(0, 200))}`)
}

say()
say("--- read this as ---")
say("  an HTTP response listing views  -> inspector backend; drive it and automate everything")
say("  a 101 Switching Protocols       -> WebSocket inspector; same conclusion")
say("  silence on everything           -> 19999 is the debugger UI's own port, not a backend.")
say("                                     Next: find the port the SIM listens on (the debugger")
say("                                     connects to it), or accept the manual console loop.")

mkdirSync(join(ROOT, "results"), { recursive: true })
const file = join(ROOT, "results", `p00-debugger-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")}.txt`)
writeFileSync(file, out.join("\n") + "\n")
say()
say(`written: ${file}`)
