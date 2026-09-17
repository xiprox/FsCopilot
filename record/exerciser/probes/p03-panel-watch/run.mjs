/*
 * p03: does {t:"watch"} on FSC's panel channel deliver the panel list with rects?
 *
 * Starts one FSC from ahead-pointer-forwarding, connects a watcher and two fake panels, and
 * checks the protocol. Then waits for the simulator's own panels to connect and prints the
 * rect each one reported. With no sim running that last list is empty and the checks still
 * run.
 *
 *   node probes/p03-panel-watch/run.mjs
 */

import { App } from "../../../pointer-forwarding/testbed/lib/app.mjs"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { fileURLToPath } from "node:url"
const HERE = fileURLToPath(new URL(".", import.meta.url))
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
let failures = 0
const check = (ok, what) => { console.log(`${ok ? "PASS" : "FAIL"}  ${what}`); if (!ok) failures++ }

function client(port, label) {
  const ws = new WebSocket(`ws://127.0.0.1:${port}/`)
  const c = { ws, label, msgs: [] }
  ws.onmessage = (e) => { const m = JSON.parse(String(e.data)); m._at = Date.now(); c.msgs.push(m) }
  c.open = new Promise((r) => (ws.onopen = r))
  c.send = (o) => ws.send(JSON.stringify(o))
  c.panels = () => c.msgs.filter((m) => m.t === "panels")
  c.lastPanels = () => { const p = c.panels(); return p.length ? p[p.length - 1].panels : null }
  return c
}
const entry = (list, key) => list && list.find((p) => p.key === key)

const peerId = "FSCW" + Math.random().toString(36).slice(2, 6).toUpperCase()
const fsc = new App({ name: "FSC", exe: join(HERE, "../../../../../ahead-pointer-forwarding/FsCopilot/bin/Debug/net9.0/win-x64/FsCopilot.exe"),
  dir: join(tmpdir(), "fsc-exerciser-p03", peerId), peerId, relay: "localhost", benchPort: 9478 })
try {
  await fsc.launch()
  console.log("FSC panel port", fsc.port)
  const W = client(fsc.port, "watcher"); await W.open
  W.send({ t: "watch" })
  await sleep(500)
  const kinds = W.msgs.map((m) => m.t)
  check(kinds.includes("config") && kinds.includes("state") && kinds.includes("panels"), `watch replies config, state, panels (got ${kinds.join(",")})`)

  const P1 = client(fsc.port, "p1"); await P1.open
  P1.send({ t: "hello", name: "ExerciserTest|a", url: "test://a", rect: [800, 600] })
  await sleep(500)
  check(JSON.stringify(entry(W.lastPanels(), "ExerciserTest|a")) === JSON.stringify({ key: "ExerciserTest|a", rect: [800, 600] }), "a hello with a rect appears with that rect")

  const before = W.panels().length
  P1.send({ t: "hello", name: "ExerciserTest|a", url: "test://a", rect: [800, 600] })
  await sleep(500)
  const extra = W.panels().slice(before).filter((m) => entry(m.panels, "ExerciserTest|a"))
  check(W.panels().length === before, `an identical re-hello sends nothing (${W.panels().length - before} panels messages after it)`)

  P1.send({ t: "hello", name: "ExerciserTest|a", url: "test://a", rect: [1024, 768] })
  await sleep(500)
  check(JSON.stringify(entry(W.lastPanels(), "ExerciserTest|a").rect) === "[1024,768]", "a changed rect is re-sent")

  const P2 = client(fsc.port, "p2"); await P2.open
  P2.send({ t: "hello", name: "ExerciserTest|b", url: "test://b" })
  await sleep(500)
  const b = entry(W.lastPanels(), "ExerciserTest|b")
  check(b && b.rect === null, "a hello without a rect appears with rect null")

  P1.ws.close()
  await sleep(800)
  check(!entry(W.lastPanels(), "ExerciserTest|a") && !!entry(W.lastPanels(), "ExerciserTest|b"), "a closed panel drops out, the other stays")

  check(P1.panels().length === 0 && P2.panels().length === 0, "panels that never watch never receive panels")

  const cockpit = () => (W.lastPanels() || []).filter((p) => !p.key.startsWith("ExerciserTest"))
  for (let i = 0; i < 40 && cockpit().length < 10; i++) await sleep(1000)
  await sleep(3000)
  console.log("\nCockpit panels seen by the watcher (real sim):")
  for (const p of W.lastPanels() || []) if (!p.key.startsWith("ExerciserTest")) console.log(`  ${p.key.padEnd(55)} ${p.rect ? p.rect.join("x") : "null"}`)
  P2.ws.close(); W.ws.close()
} finally {
  await fsc.quit().catch(() => {})
  await fsc.dispose()
}
console.log(failures ? `\n${failures} FAILED` : "\nall passed")
process.exit(failures ? 1 : 0)
