/*
 * The testbed: record a real cockpit interaction, replay it into a live panel.
 *
 *   npm run rec  -- <page> [name]     record until Ctrl-C
 *   npm run play -- <page> <file>     replay a recording
 *   npm run play -- <page> <file> --speed 2 --once
 *   npm run agent -- <page> stats|stop|rect
 *
 * <page> is an id or a case-insensitive title substring.
 *
 * Nothing is installed in the simulator. testbed/agent.js is injected through
 * Runtime.evaluate, and its captures come back as Console.messageAdded events.
 * The agent knows none of that — the console wiring is injected separately, below,
 * precisely so the same agent file can be handed a different transport later.
 *
 * Recordings are NDJSON in recordings/, one message per line, and are committed:
 * getting MSFS into a flight and reaching a control is minutes of manual work, and
 * a recording turns that into a fixture that can be re-run after every change to
 * the agent.
 */

import { readFileSync, writeFileSync, mkdirSync, existsSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, join, isAbsolute } from "node:path"

import { listPages, selectPage, Inspector } from "../probes/lib/inspector.mjs"

const HERE = dirname(fileURLToPath(import.meta.url))
const ROOT = join(HERE, "..")
const RECORDINGS = join(ROOT, "recordings")

/** The prefix that separates our captures from the simulator's own console noise —
 *  a fresh connection replays the panel's whole init log before anything of ours. */
const MARK = "FSCPP§"

const argv = process.argv.slice(2)
const mode = argv[0]
const rest = argv.slice(1)

function flag(name, fallback) {
  const i = rest.indexOf(`--${name}`)
  if (i === -1) return fallback
  const v = rest[i + 1]
  rest.splice(i, v === undefined || v.startsWith("--") ? 1 : 2)
  return v === undefined || v.startsWith("--") ? true : v
}

const slug = (s) => String(s).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 40)
const stamp = () => new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")

/** Inject the agent, then wire its captures to the console channel.
 *  Two separate evaluations on purpose: the first is the file that ships, the
 *  second is testbed plumbing that never will. */
async function install(insp) {
  const source = readFileSync(join(HERE, "agent.js"), "utf8")
  await insp.evaluate(`(function(){ ${source} })()`)
  const wired = await insp.evaluate(
    `(function(){
       if (!window.FSCPP) return "agent missing"
       window.__FSCPP_SEQ = 0
       window.FSCPP.onCapture(function (m) {
         // The sequence number is not decoration. Coherent's console collapses a
         // message identical to the one before it — verified, and across any gap,
         // not just within a burst. Two presses on the same control with the same
         // hold produce identical JSON, and the second would vanish silently.
         // A monotonic prefix makes every message unique on the wire.
         window.__FSCPP_SEQ = window.__FSCPP_SEQ + 1
         console.log(${JSON.stringify(MARK)} + window.__FSCPP_SEQ + " " + JSON.stringify(m))
       })
       return window.FSCPP.key + " | " + JSON.stringify(window.FSCPP.rect())
     })()`)
  return wired
}

/** Console.messageAdded carries the text; pull out anything wearing our mark.
 *  Payload is "<seq> <json>" — the seq exists to defeat console dedup, and is
 *  also how a gap in delivery becomes visible rather than silent. */
function readMarks(params, onMsg) {
  const m = params && params.message
  if (!m || typeof m.text !== "string") return
  const at = m.text.indexOf(MARK)
  if (at === -1) return
  const body = m.text.slice(at + MARK.length)
  const sp = body.indexOf(" ")
  if (sp === -1) return
  const seq = Number(body.slice(0, sp))
  try { onMsg(JSON.parse(body.slice(sp + 1)), seq) } catch { /* truncated or not ours */ }
}

async function main() {
  if (!mode || !rest[0]) {
    console.error("usage: npm run rec|play|agent -- <page> [args]")
    process.exit(1)
  }

  const page = selectPage(await listPages(), rest[0])
  const insp = new Inspector(page.id)
  await insp.open()

  if (mode === "agent") {
    const what = rest[1] || "stats"
    if (what === "stop") {
      console.log(await insp.evaluate(`window.FSCPP ? (window.FSCPP.stop(), "stopped") : "not installed"`))
    } else if (what === "rect") {
      console.log(await insp.evaluate(`window.FSCPP ? JSON.stringify(window.FSCPP.rect()) : "not installed"`))
    } else {
      console.log(await insp.evaluate(
        `window.FSCPP ? ("v" + window.FSCPP.version + "  " + window.FSCPP.key + "  " + JSON.stringify(window.FSCPP.stats())) : "not installed"`))
    }
    insp.close()
    return
  }

  if (mode === "rec") {
    const name = rest[1] && !rest[1].startsWith("--") ? slug(rest[1]) : slug(page.title)
    mkdirSync(RECORDINGS, { recursive: true })
    const file = join(RECORDINGS, `${name}-${stamp()}.ndjson`)

    await insp.send("Console.enable", {})
    const info = await install(insp)

    const lines = []
    const seqs = []
    let t0 = null
    insp.on("Console.messageAdded", (params) => readMarks(params, (msg, seq) => {
      const now = Date.now()
      if (t0 === null) t0 = now
      // Timing is stamped here rather than in the agent: the agent stays
      // transport-agnostic, and this is when the message actually arrived.
      const rec = { at: now - t0, ...msg }
      lines.push(rec)
      seqs.push(seq)
      const gap = seqs.length > 1 && seq !== seqs[seqs.length - 2] + 1
      console.log(`  ${String(rec.at).padStart(6)}ms  #${seq}  ${rec.k}  ` +
                  `(${rec.nx}, ${rec.ny})${rec.hold ? "  hold=" + rec.hold + "ms" : ""}` +
                  (gap ? `   <-- GAP, expected #${seqs[seqs.length - 2] + 1}` : ""))
    }))

    console.log(`page ${page.id}  ${page.title}`)
    console.log(`agent ${info}`)
    console.log(`recording to ${file}`)
    console.log("\nInteract with the panel in the cockpit. Ctrl-C to stop.\n")

    const finish = () => {
      if (!lines.length) console.log("\nnothing captured — nothing written")
      else {
        writeFileSync(file, lines.map((l) => JSON.stringify(l)).join("\n") + "\n")
        console.log(`\n${lines.length} events written to ${file}`)
      }
      insp.close()
      process.exit(0)
    }
    process.on("SIGINT", finish)
    return
  }

  if (mode === "play") {
    const speed = Number(flag("speed", 1)) || 1
    const once = flag("once", false) === true
    const target = rest[1]
    if (!target) throw new Error("give a recording file")
    const file = isAbsolute(target) ? target : (existsSync(target) ? target : join(RECORDINGS, target))
    const events = readFileSync(file, "utf8").trim().split("\n").filter(Boolean).map((l) => JSON.parse(l))

    const info = await install(insp)
    console.log(`page ${page.id}  ${page.title}`)
    console.log(`agent ${info}`)
    console.log(`replaying ${events.length} events from ${file}${speed !== 1 ? `  at ${speed}x` : ""}${once ? "  (first only)" : ""}\n`)

    const list = once ? events.slice(0, 1) : events
    let prev = 0
    for (const ev of list) {
      const wait = Math.max(0, (ev.at - prev)) / speed
      prev = ev.at
      if (wait > 0) await new Promise((r) => setTimeout(r, Math.min(wait, 10000)))
      const ok = await insp.evaluate(`window.FSCPP ? window.FSCPP.replay(${JSON.stringify(ev)}) : "no agent"`)
      console.log(`  ${String(ev.at).padStart(6)}ms  ${ev.k}  (${ev.nx}, ${ev.ny})  -> ${ok}`)
    }

    // A held press finishes inside a timeout, so reading counters immediately
    // undercounts the last event and makes a clean run look like a lost one.
    const tail = list.length ? Math.max(...list.map((e) => e.hold || 0)) : 0
    await new Promise((r) => setTimeout(r, tail + 250))

    const stats = JSON.parse(await insp.evaluate(`JSON.stringify(window.FSCPP.stats())`))
    const expect = list.length
    console.log(`\nagent stats ${JSON.stringify(stats)}`)
    console.log(`  replayed ${stats.replayed}/${expect}` +
      (stats.missed ? `   MISSED ${stats.missed} — elementFromPoint found nothing there` : "") +
      (stats.captured ? `   ECHO ${stats.captured} — loop breaking leaked` : "   no echo"))
    console.log("Watch the display: only a human can confirm what actually happened.")
    insp.close()
    return
  }

  throw new Error(`unknown mode ${JSON.stringify(mode)} — use rec, play or agent`)
}

main().catch((e) => { console.error("\n" + e.message + "\n"); process.exit(1) })
