/*
 * Build and install the fscpp-bridge Community package.
 *
 * The package is HTML and JavaScript only — no WASM, no modules folder — so it
 * needs no MSFS SDK and no fspackagetool. manifest.json and layout.json are the
 * two files the simulator insists on, and both are small enough to write here.
 * (fsc-editor's link/ needs the whole SDK toolchain because it ships a WASM
 * module; this does not.)
 *
 *   node module/tools/build.mjs            build and install
 *   node module/tools/build.mjs --check    report state, change nothing
 *   node module/tools/build.mjs --remove   uninstall
 *
 * A sim restart is required after installing, because MSFS reads layout.json at
 * startup. Editing the JS afterwards does NOT need a rebuild — the files are
 * loaded as-is, so reloading the panel picks them up.
 */

import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync, statSync, copyFileSync, rmSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, join, relative, sep } from "node:path"

const HERE = dirname(fileURLToPath(import.meta.url))
const ROOT = join(HERE, "..", "..")
const SRC = join(ROOT, "module", "PackageSources")
const PKG = "fscpp-bridge"

const COMMUNITY = join(process.env.APPDATA || "", "Microsoft Flight Simulator 2024", "Packages", "Community")
const DEST = join(COMMUNITY, PKG)
const RIVAL = join(COMMUNITY, "fscopilot-bridge")

const mode = process.argv.includes("--remove") ? "remove"
  : process.argv.includes("--check") ? "check" : "install"

function walk(dir, base = dir, out = []) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name)
    if (statSync(full).isDirectory()) walk(full, base, out)
    else out.push(relative(base, full))
  }
  return out
}

/* Both packages override html_ui/Pages/VCockpit/Core/VCockpit.js. Whichever the
 * simulator resolves last wins, and which that is depends on load order we do not
 * control — so the outcome is silently one or the other, which is the worst way
 * to run an experiment. Refuse rather than guess. */
function conflict() {
  if (!existsSync(RIVAL)) return null
  return `fscopilot-bridge is installed at\n  ${RIVAL}\n\n` +
    `Both packages override VCockpit.js and only one can win, non-deterministically.\n` +
    `Rename it aside while testing, and back afterwards:\n\n` +
    `  Rename-Item "${RIVAL}" "fscopilot-bridge.off"\n`
}

if (!existsSync(COMMUNITY)) {
  console.error(`Community folder not found at ${COMMUNITY}`)
  process.exit(1)
}

if (mode === "check") {
  console.log(`community  ${COMMUNITY}`)
  console.log(`installed  ${existsSync(DEST) ? "yes  " + DEST : "no"}`)
  const c = conflict()
  console.log(`conflict   ${c ? "YES" : "none"}`)
  if (c) console.log("\n" + c)
  process.exit(0)
}

if (mode === "remove") {
  if (existsSync(DEST)) { rmSync(DEST, { recursive: true, force: true }); console.log(`removed ${DEST}`) }
  else console.log("not installed")
  process.exit(0)
}

const c = conflict()
if (c) {
  console.error("\nREFUSING TO INSTALL\n\n" + c)
  process.exit(1)
}

// Copy sources.
const files = walk(SRC)
if (!files.length) { console.error("no sources — run derive-vcockpit.mjs first"); process.exit(1) }
for (const rel of files) {
  const to = join(DEST, rel)
  mkdirSync(dirname(to), { recursive: true })
  copyFileSync(join(SRC, rel), to)
}

// manifest.json. minimum_game_version matches what fscopilot-bridge declares,
// which is known to work on this build.
const version = JSON.parse(readFileSync(join(ROOT, "package.json"), "utf8")).version || "0.1.0"
const manifest = {
  dependencies: [],
  content_type: "MISC",
  title: "FSCPP Pointer Bridge (experimental)",
  manufacturer: "",
  creator: "fsc-pointer-playground",
  package_version: version,
  minimum_game_version: "1.6.34",
  release_notes: { neutral: { LastUpdate: "", OlderHistory: "" } },
  // PANEL_PATCH is what lets an html_ui override of a core file take effect.
  package_order_hint: "PANEL_PATCH"
}
writeFileSync(join(DEST, "manifest.json"), JSON.stringify(manifest, null, 2))

// layout.json. Paths are lowercase and forward-slashed; the date field is
// Windows FILETIME (100ns ticks since 1601), which is what the sim expects.
const EPOCH_DIFF = 11644473600000n
const content = walk(DEST)
  .filter((rel) => rel !== "layout.json" && rel !== "manifest.json")
  .map((rel) => {
    const full = join(DEST, rel)
    const st = statSync(full)
    return {
      path: rel.split(sep).join("/").toLowerCase(),
      size: st.size,
      date: Number((BigInt(Math.floor(st.mtimeMs)) + EPOCH_DIFF) * 10000n)
    }
  })
writeFileSync(join(DEST, "layout.json"), JSON.stringify({ content }, null, 2))

console.log(`installed ${PKG} v${version} to`)
console.log(`  ${DEST}`)
console.log(`  ${content.length} files`)
for (const f of content) console.log(`    ${f.path}`)
console.log(`\nRestart MSFS for layout.json to be read.`)
console.log(`After that, editing the JS needs no rebuild — reload the panel.`)
