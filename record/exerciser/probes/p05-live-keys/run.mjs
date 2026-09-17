/*
 * p05: the panel keys each aircraft really helloes with, collected while a pilot switches
 * aircraft.
 *
 * p04 reads panel.cfg and resolves identifiers out of package JavaScript, which leaves some
 * unresolved and cannot see a livery variable's value. This runs one FS Copilot, watches its
 * panel channel, and records the keys and rects for every aircraft that loads, until stopped.
 *
 *   node probes/p05-live-keys/run.mjs [out.json]
 *
 * Load an aircraft, sit in the cockpit until the line for it prints, then switch. Nothing
 * needs restarting between aircraft. The FSC it starts joins no session and syncs nothing.
 */

import { writeFileSync } from "node:fs"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { fileURLToPath } from "node:url"
import { App } from "../../../pointer-forwarding/testbed/lib/app.mjs"

const HERE = fileURLToPath(new URL(".", import.meta.url))
const OUT = process.argv[2] || join(HERE, "live-keys.json")
const peerId = "FSCL" + Math.random().toString(36).slice(2, 6).toUpperCase()
const fsc = new App({
  name: "FSC",
  exe: join(HERE, "../../../../../ahead-pointer-forwarding/FsCopilot/bin/Debug/net9.0/win-x64/FsCopilot.exe"),
  dir: join(tmpdir(), "fsc-exerciser-p05", peerId), peerId, benchPort: 9480
})

const seen = new Map()   // aircraft -> {pointer: [...], panels: [...]}
let aircraft = "(none)"
let config = []

const flush = () => writeFileSync(OUT, JSON.stringify([...seen].map(([k, v]) => ({ aircraft: k, ...v })), null, 2) + "\n")

await fsc.launch()
console.log(`FSC on panel port ${fsc.port}. Switch aircraft freely; Ctrl-C when done.`)

// The app names each aircraft as SimConnect reports it, which is the profile's file name.
setInterval(() => {
  for (const line of fsc.lines.splice(0, fsc.lines.length)) {
    const m = String(line).match(/Loaded aircraft: (.+)$/)
    if (m) { aircraft = m[1].trim(); console.log(`\n--- ${aircraft}`) }
  }
}, 500)

const ws = new WebSocket(`ws://127.0.0.1:${fsc.port}/`)
ws.onopen = () => ws.send(JSON.stringify({ t: "watch" }))
ws.onmessage = (e) => {
  const msg = JSON.parse(String(e.data))
  if (msg.t === "config") config = msg.pointer || []
  if (msg.t !== "panels") return
  const panels = msg.panels.filter((p) => !p.key.startsWith("ExerciserTest"))
  if (panels.length === 0) return
  const before = seen.get(aircraft)
  const now = { pointer: config, panels }
  if (before && JSON.stringify(before) === JSON.stringify(now)) return
  seen.set(aircraft, now)
  flush()
  console.log(`  ${panels.length} panels, profile opts in ${config.length ? config.join(", ") : "nothing"}`)
  for (const p of panels) {
    const opted = config.indexOf(p.key) >= 0 ? "MATCH" : config.some((k) => k.split("|")[0] === p.key.split("|")[0]) ? "id   " : "     "
    console.log(`    ${opted} ${p.key.padEnd(55)} ${p.rect ? p.rect.join("x") : "null"}`)
  }
}

const stop = async () => { flush(); console.log(`\nwrote ${OUT}`); await fsc.quit().catch(() => {}); await fsc.dispose(); process.exit(0) }
process.on("SIGINT", stop)
process.on("SIGTERM", stop)
