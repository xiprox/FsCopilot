/*
 * p01: can a separate process join a running FS Copilot as a peer, on one machine, and
 * trade PointerEvents with it in both directions?
 *
 * Starts one real FSC from ahead-pointer-forwarding (with --bench, only to set the pointer
 * key list without an aircraft profile), opens a fake panel on it from the testbed, and runs
 * PeerProbe.exe against it. When the panel receives the probe's press it answers with a
 * capture of its own, which the probe should receive.
 *
 *   dotnet build probes/p01-peer-join -c Debug
 *   node probes/p01-peer-join/run.mjs [relay-host]      omit for the build's default
 *
 * Do not run it with the simulator in a flight: the FSC instance opens SimConnect, and an
 * aircraft profile loading replaces the key list (see the result file).
 */

import { spawn } from "node:child_process"
import { mkdirSync } from "node:fs"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"
import { tmpdir } from "node:os"
import { App } from "../../../pointer-forwarding/testbed/lib/app.mjs"
import { Panel } from "../../../pointer-forwarding/testbed/lib/panel.mjs"

const HERE = dirname(fileURLToPath(import.meta.url))
const FSC_EXE = join(HERE, "../../../../../ahead-pointer-forwarding/FsCopilot/bin/Debug/net9.0/win-x64/FsCopilot.exe")
const PROBE_EXE = join(HERE, "bin/Debug/net9.0/win-x64/PeerProbe.exe")
const KEY = "DisplayUnits|config=Default"
const host = process.argv[2] || null
const peerId = "FSCP" + Math.random().toString(36).slice(2, 6).toUpperCase()

const dir = join(tmpdir(), "fsc-exerciser-p01", peerId)
mkdirSync(dir, { recursive: true })
const fsc = new App({ name: "FSC", exe: FSC_EXE, dir, peerId, relay: host, benchPort: 9477 })
let code = 1
try {
  await fsc.launch()
  console.log(`FSC ${peerId} panel port ${fsc.port}`)
  await new Promise((r) => setTimeout(r, 4000))
  await fsc.configure([KEY])
  const panel = await Panel.open({ key: KEY, port: fsc.port, rect: [7410, 1110] })
  panel.on("message", (m) => {
    if (m.t === "state") return
    console.log("PANEL <-", JSON.stringify(m).slice(0, 200))
    if (m.t === "pointer") {
      setTimeout(() => { console.log("PANEL -> capture press"); panel.press({ x: 0.75, y: 0.2, hold: 60 }) }, 500)
    }
    // FSC re-applies its aircraft profile, which empties the list; put the key back.
    if (m.t === "config" && m.pointer.length === 0) { console.log("RUNNER re-configure"); fsc.configure([KEY]) }
  })
  const args = [peerId, String(fsc.port), KEY]
  if (host) args.push(host)
  const probe = spawn(PROBE_EXE, args, { stdio: "inherit" })
  code = await new Promise((r) => probe.on("exit", r))
  console.log("probe exit", code, "- FSC log in", dir)
  panel.close()
} finally {
  await fsc.quit().catch(() => {})
  await fsc.dispose()
}
process.exit(code)
