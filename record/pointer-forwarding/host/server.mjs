/*
 * FSCPP host — the local process the cockpit panels connect to.
 *
 * Stands in for what would be the FS Copilot desktop app. Panels open a WebSocket
 * to it (see module/PackageSources/html_ui/FSCPP/link.js), identify themselves by
 * panel key, and send an interact message whenever the pilot presses something.
 *
 * On one machine it does three useful things:
 *
 *   - shows every capture as it happens, live, which is how you tell whether
 *     capture is losing presses without inferring it from a short recording;
 *   - records to NDJSON in recordings/;
 *   - replays a recording back into the panel.
 *
 * With a second machine it would forward between two hosts instead. That is
 * deliberately not built yet — there is no second machine to test it against.
 *
 *   npm run host
 *   npm run host -- --port 9020 --record fpln
 *
 * While it runs, type into it:
 *   r            start / stop recording
 *   p            replay the current recording into the panel
 *   p <file>     replay a specific one
 *   s            panel and message counters
 *   q            quit
 */

import { writeFileSync, readFileSync, mkdirSync, existsSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, join, isAbsolute } from "node:path"

import { wsServer } from "./ws.mjs"

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..")
const RECORDINGS = join(ROOT, "recordings")

const argv = process.argv.slice(2)
const argOf = (name, fallback) => {
  const i = argv.indexOf(`--${name}`)
  return i === -1 || !argv[i + 1] ? fallback : argv[i + 1]
}
const PORT = Number(argOf("port", 9020))

const stamp = () => new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")
const slug = (s) => String(s).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 40)

const panels = new Map()          // id -> socket
let recording = null              // { name, t0, events }
let lastFile = null
const counts = { interact: 0, replayed: 0 }

function panelsByKey(key) {
  return [...panels.values()].filter((p) => p.name === key)
}

/** One line per gesture. A drag carries a path rather than a point, so its
 *  interesting numbers are where it started, where it ended and how long it took. */
function describe(msg) {
  if (msg.k === "drag" && msg.path && msg.path.length) {
    const a = msg.path[0]
    const b = msg.path[msg.path.length - 1]
    return `drag   (${a[1]}, ${a[2]}) -> (${b[1]}, ${b[2]})  ` +
           `${msg.path.length} pts  ${b[0]}ms`
  }
  return `${msg.k}  (${msg.nx}, ${msg.ny})${msg.hold ? "  hold=" + msg.hold + "ms" : ""}`
}

function onInteract(from, msg) {
  counts.interact++
  const now = Date.now()

  if (recording) {
    if (recording.t0 === null) recording.t0 = now
    recording.events.push({ at: now - recording.t0, ...msg })
  }

  console.log(`  ${recording ? "REC " : "    "}${String(counts.interact).padStart(4)}  ${describe(msg)}  [${from.name}]`)

  // A second machine would forward here. On one machine there is nowhere to send
  // it — and echoing to the originating panel would replay the pilot's own press
  // back at them, which is the loop the agent exists to break, not a feature.
}

const server = wsServer({
  port: PORT,
  onConnection(sock) {
    panels.set(sock.id, sock)
    sock.on((m) => {
      if (m.t === "hello") {
        sock.name = m.name || "unknown"
        console.log(`+ panel #${sock.id}  ${sock.name}`)
        return
      }
      if (m.t === "interact" && m.msg) onInteract(sock, m.msg)
    })
    sock.onClose(() => {
      panels.delete(sock.id)
      console.log(`- panel #${sock.id}  ${sock.name || "(never identified)"}`)
    })
  },
  onHttp: () => JSON.stringify({ ok: true, panels: panels.size, counts })
})

/* ---- recording ----------------------------------------------------------- */

function toggleRecord(name) {
  if (recording) {
    const { events } = recording
    if (!events.length) {
      console.log("  nothing captured — nothing written")
    } else {
      mkdirSync(RECORDINGS, { recursive: true })
      lastFile = join(RECORDINGS, `${recording.name}-${stamp()}.ndjson`)
      writeFileSync(lastFile, events.map((e) => JSON.stringify(e)).join("\n") + "\n")
      console.log(`  ${events.length} events -> ${lastFile}`)
    }
    recording = null
    return
  }
  recording = { name: slug(name || argOf("record", "session")), t0: null, events: [] }
  console.log(`  recording (${recording.name}) — press r again to stop`)
}

async function replay(file) {
  const path = file
    ? (isAbsolute(file) ? file : (existsSync(file) ? file : join(RECORDINGS, file)))
    : lastFile
  if (!path || !existsSync(path)) { console.log("  no recording to replay"); return }

  const events = readFileSync(path, "utf8").trim().split("\n").filter(Boolean).map((l) => JSON.parse(l))
  console.log(`  replaying ${events.length} events from ${path}`)

  let prev = 0
  for (const ev of events) {
    const wait = Math.max(0, ev.at - prev)
    prev = ev.at
    if (wait) await new Promise((r) => setTimeout(r, Math.min(wait, 10000)))

    const targets = ev.key ? panelsByKey(ev.key) : [...panels.values()]
    if (!targets.length) { console.log(`    no panel for ${ev.key} — skipped`); continue }
    for (const t of targets) t.send({ t: "interact", msg: ev })
    counts.replayed++
    console.log(`    ${String(ev.at).padStart(6)}ms  ${describe(ev)}`)

    // A drag occupies the far side for as long as it took to make. Waiting for it
    // keeps the replay honest — otherwise the next gesture lands mid-drag.
    if (ev.k === "drag" && ev.path && ev.path.length) {
      await new Promise((r) => setTimeout(r, Math.min(ev.path[ev.path.length - 1][0], 10000)))
    }
  }
  console.log(`  replay complete (${counts.replayed} sent)`)
}

/* ---- console ------------------------------------------------------------- */

console.log(`FSCPP host on ws://127.0.0.1:${PORT}/`)
console.log(`waiting for panels — load the aircraft with the module installed\n`)
console.log(`  r  record on/off     p [file]  replay     s  stats     q  quit\n`)

process.stdin.setEncoding("utf8")
process.stdin.on("data", (raw) => {
  const line = raw.trim()
  const [cmd, ...rest] = line.split(/\s+/)
  if (cmd === "r") toggleRecord(rest.join(" "))
  else if (cmd === "p") replay(rest.join(" ") || null)
  else if (cmd === "s") {
    console.log(`  panels ${panels.size}: ${[...panels.values()].map((p) => p.name || "?").join(", ") || "none"}`)
    console.log(`  interact ${counts.interact}   replayed ${counts.replayed}` +
      (recording ? `   recording ${recording.events.length}` : ""))
  } else if (cmd === "q") { server.close(); process.exit(0) }
  else if (line) console.log("  r | p [file] | s | q")
})
