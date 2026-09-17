/*
 * p04: which instruments in installed aircraft are HTML, what identifies them, and what
 * would a profile have to write to opt one in?
 *
 * Reads panel.cfg rather than flying: every VCockpit entry names an HTML gauge by URL, and
 * the panel key FS Copilot uses is <instrumentIdentifier>|<query string> from that URL. The
 * identifier is not the URL path - it comes from the instrument's own templateID - so the
 * scan resolves it out of the package's JavaScript where it can.
 *
 *   node scan.mjs [packages-dir] > report.txt
 *
 * A query of [config] or similar is a per-livery variable: the A220's DisplayUnits is
 * ?config=[config] and helloes as config=N324DU on that livery, which is how a profile key
 * naming config=Default came to match nothing.
 */

import { readdirSync, readFileSync, statSync, existsSync } from "node:fs"
import { join, dirname } from "node:path"

const PACKAGES = process.argv[2] ||
  "C:/Users/wayne/AppData/Roaming/Microsoft Flight Simulator 2024/Packages/Community"

/** Directories that cannot contain an aircraft, skipped so the walk stays quick. */
const SKIP = new Set(["texture", "sound", "model", "effects", "scenery", "ContentInfo", "modules", "html_ui"])

function findPanelCfgs(dir, depth = 0, out = []) {
  // The A220's panel.cfg sits ten deep, under attachments/<vendor>/Part_Interior_*/panel.
  if (depth > 12) return out
  let entries
  try { entries = readdirSync(dir, { withFileTypes: true }) } catch (e) { return out }
  for (const e of entries) {
    if (e.isDirectory()) {
      if (SKIP.has(e.name)) continue
      findPanelCfgs(join(dir, e.name), depth + 1, out)
    } else if (e.name.toLowerCase() === "panel.cfg") {
      out.push(join(dir, e.name))
    }
  }
  return out
}

/** The package root is the folder holding html_ui, walking up from the panel.cfg. */
function packageRoot(file) {
  let dir = dirname(file)
  for (let i = 0; i < 8; i++) {
    if (existsSync(join(dir, "html_ui"))) return dir
    dir = dirname(dir)
  }
  return null
}

const idCache = new Map()

/** templateID is what BaseInstrument reports as instrumentIdentifier. */
function identifierFor(root, urlPath) {
  const dir = join(root, "html_ui/Pages/VCockpit/Instruments", dirname(urlPath))
  if (idCache.has(dir)) return idCache.get(dir)
  let found = null
  try {
    for (const f of readdirSync(dir)) {
      if (!f.endsWith(".js") && !f.endsWith(".html")) continue
      const p = join(dir, f)
      if (statSync(p).size > 8 * 1024 * 1024) continue
      const text = readFileSync(p, "latin1")
      const m = text.match(/templateID\s*\(\s*\)\s*\{\s*return\s*["'`]([\w-]+)["'`]/) ||
        text.match(/templateID\s*[:=]\s*["'`]([\w-]+)["'`]/)
      if (m) { found = m[1]; break }
    }
  } catch (e) { /* no such directory, or unreadable */ }
  idCache.set(dir, found)
  return found
}

const rows = []
for (const cfg of findPanelCfgs(PACKAGES)) {
  const root = packageRoot(cfg)
  let text
  try { text = readFileSync(cfg, "latin1") } catch (e) { continue }
  const aircraft = cfg.replace(PACKAGES + "\\", "").replace(PACKAGES + "/", "").split(/[\\/]/).slice(0, 1)[0]
  const simobject = (cfg.match(/Airplanes[\\/]([^\\/]+)/i) || [])[1] || "?"
  for (const line of text.split(/\r?\n/)) {
    const m = line.match(/^\s*htmlgauge\d+\s*=\s*([^,]+)/i)
    if (!m) continue
    const url = m[1].trim()
    const q = url.indexOf("?")
    const path = q >= 0 ? url.slice(0, q) : url
    const query = q >= 0 ? url.slice(q + 1) : ""
    rows.push({
      aircraft, simobject, path, query,
      variable: /\[[^\]]+\]/.test(query),
      identifier: root ? identifierFor(root, path) : null
    })
  }
}

// One aircraft can hold several panel.cfg files (attachments, variants); collapse identical rows.
const byAircraft = new Map()
for (const r of rows) {
  const list = byAircraft.get(r.simobject) || []
  if (!list.some((x) => x.path === r.path && x.query === r.query)) list.push(r)
  byAircraft.set(r.simobject, list)
}

let totalHtml = 0, totalVariable = 0, totalResolved = 0
const shared = []
for (const [simobject, list] of [...byAircraft].sort()) {
  const wasm = list.filter((r) => /WasmInstrument/i.test(r.path))
  const html = list.filter((r) => !/WasmInstrument/i.test(r.path))
  if (html.length === 0) continue
  console.log(`\n=== ${simobject}  (${html.length} html, ${wasm.length} wasm)`)
  const byId = new Map()
  for (const r of html) {
    const id = r.identifier || `?${r.path}`
    byId.set(id, (byId.get(id) || 0) + 1)
  }
  for (const r of html) {
    totalHtml++
    if (r.variable) totalVariable++
    if (r.identifier) totalResolved++
    const id = r.identifier || "(unresolved)"
    const dup = byId.get(r.identifier || `?${r.path}`)
    const key = r.query ? `${id}|${r.query}` : id
    console.log(`  ${key.padEnd(52)} ${dup > 1 ? `x${dup} same identifier` : ""}${r.variable ? "  [livery variable]" : ""}`)
    if (dup > 1 && r.identifier && !shared.includes(`${simobject}:${r.identifier}`)) shared.push(`${simobject}:${r.identifier}`)
  }
}

console.log(`\n--- ${byAircraft.size} aircraft scanned, ${totalHtml} html gauges`)
console.log(`identifier resolved: ${totalResolved}/${totalHtml}`)
console.log(`query holds a livery variable: ${totalVariable}`)
console.log(`identifiers used by more than one instrument in one aircraft: ${shared.length}`)
for (const s of shared) console.log(`  ${s}`)
