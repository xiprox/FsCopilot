/*
 * The bench: two FS Copilot instances on one machine, fake panels on both, and a
 * local rendezvous, driven through outages and back.
 *
 *   node testbed/bench.mjs                    every fast scenario
 *   node testbed/bench.mjs press-across       one, by name
 *   node testbed/bench.mjs --slow             including the ones that wait out a timeout
 *   node testbed/bench.mjs --list             what there is
 *
 *   --exe <path>      the FS Copilot build to run. It must be one with BenchControl,
 *                     i.e. built from a branch that has --bench.
 *   --relay <host>    use an existing rendezvous instead of starting one. "none"
 *                     leaves the app on its built-in default.
 *   --keep            leave the processes up after a failure, to look at them.
 *
 * Every scenario gets its own pair of instances and its own directory under
 * testbed/runs/, holding each instance's log and the run's transcript. That tree is
 * not committed; a run worth keeping is promoted into results/ by hand.
 *
 * Do not run the simulator while the bench runs - a loaded aircraft's profile
 * replaces the bench's pointer keys through Coordinator.Load, and the two instances
 * would sync variables into your live flight. See docs/14-test-bench.md.
 */

import { readdirSync, mkdirSync, writeFileSync, existsSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, join, resolve } from "node:path"

import { Relay } from "./lib/relay.mjs"
import { World } from "./lib/world.mjs"
import { Failed } from "./lib/expect.mjs"

const HERE = dirname(fileURLToPath(import.meta.url))
const ROOT = join(HERE, "..")            // record/pointer-forwarding
const FSC = join(ROOT, "..", "..", "..") // the directory every checkout sits under

const DEFAULT_EXE = join(FSC, "ahead-pointer-forwarding", "FsCopilot", "bin", "Debug", "net9.0", "win-x64", "FsCopilot.exe")
const DEFAULT_DISCOVERY = join(FSC, "ahead-pointer-forwarding", "FsCopilot.Discovery")

const argv = process.argv.slice(2)
const flag = (name, fallback) => {
  const i = argv.indexOf(`--${name}`)
  if (i === -1) return fallback
  const v = argv[i + 1]
  argv.splice(i, v === undefined || v.startsWith("--") ? 1 : 2)
  return v === undefined || v.startsWith("--") ? true : v
}

const wantSlow = !!flag("slow", false)
const wantList = !!flag("list", false)
const keep = !!flag("keep", false)
const exe = resolve(String(flag("exe", DEFAULT_EXE)))
const relayFlag = flag("relay", null)
const named = argv.filter((a) => !a.startsWith("--"))

const scenarios = readdirSync(join(HERE, "scenarios"))
  .filter((f) => f.endsWith(".mjs"))
  .sort()
  .map((f) => ({ name: f.replace(/\.mjs$/, ""), file: join(HERE, "scenarios", f) }))

for (const s of scenarios) {
  const mod = await import(`file://${s.file}`)
  s.run = mod.default
  s.about = mod.about || ""
  s.slow = !!mod.slow
}

if (wantList) {
  for (const s of scenarios) console.log(`${s.slow ? "slow " : "     "}${s.name.padEnd(28)} ${s.about}`)
  process.exit(0)
}

const selected = scenarios.filter((s) =>
  (named.length ? named.includes(s.name) : true) && (wantSlow || !s.slow || named.includes(s.name)))

if (!selected.length) {
  console.error(named.length ? `No scenario named ${named.join(", ")}. --list shows them.` : "No scenarios.")
  process.exit(2)
}

if (!existsSync(exe)) {
  console.error(`No build at ${exe}\nBuild one with --bench in it, or point --exe at one.`)
  process.exit(2)
}

const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")
const runDir = join(HERE, "runs", stamp)
mkdirSync(runDir, { recursive: true })

let relay = null
let relayHost = null
if (relayFlag === "none") {
  console.log("Rendezvous: the app's own default")
} else if (typeof relayFlag === "string") {
  relayHost = relayFlag
  console.log(`Rendezvous: ${relayHost}`)
} else {
  process.stdout.write("Rendezvous: starting locally ... ")
  relay = await Relay.start({ project: DEFAULT_DISCOVERY })
  relayHost = "127.0.0.1"
  console.log("up on 127.0.0.1")
}

console.log(`Build:      ${exe}`)
console.log(`Run:        ${runDir}\n`)

const results = []
for (const s of selected) {
  const worlds = []
  const ctx = {
    exe,
    relayHost,
    runDir: join(runDir, s.name),
    notes: [],
    log: (m) => ctx.notes.push({ at: Date.now(), m }),
    // Registered before it is set up, so a world that throws halfway is still torn
    // down. It is the processes it already started that matter.
    world: async (opts) => {
      const w = new World(ctx)
      worlds.push(w)
      return w.setup(opts)
    }
  }
  mkdirSync(ctx.runDir, { recursive: true })

  process.stdout.write(`${s.name.padEnd(30)}`)
  const started = Date.now()

  try {
    await s.run(ctx)
    const took = ((Date.now() - started) / 1000).toFixed(1)
    console.log(`ok    ${took}s`)
    results.push({ name: s.name, about: s.about, ok: true, seconds: Number(took), notes: ctx.notes })
  } catch (e) {
    const took = ((Date.now() - started) / 1000).toFixed(1)
    const kind = e instanceof Failed ? "FAIL" : "ERROR"
    console.log(`${kind}  ${took}s\n  ${e.message}`)
    if (!(e instanceof Failed)) console.log(String(e.stack).split("\n").slice(1, 4).join("\n"))
    results.push({
      name: s.name, about: s.about, ok: false, kind, seconds: Number(took),
      error: e.message, notes: ctx.notes
    })
    if (keep) {
      console.log("  --keep: leaving processes up. Ctrl-C when done.")
      await new Promise(() => {})
    }
  } finally {
    for (const w of worlds) await w.dispose()
  }
}

if (relay) await relay.stop()

const passed = results.filter((r) => r.ok).length
console.log(`\n${passed}/${results.length} passed`)

// Into the run directory with the logs it describes, not into results/. results/ is
// curated evidence that gets committed, and most runs are not evidence of anything.
const transcript = join(runDir, "transcript.json")
writeFileSync(transcript, JSON.stringify({ stamp, exe, relayHost, results }, null, 2))
console.log(`Transcript: ${transcript}`)

process.exit(passed === results.length ? 0 : 1)
