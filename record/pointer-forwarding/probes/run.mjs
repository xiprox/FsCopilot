/*
 * Run a console probe, or arbitrary JavaScript, in a live panel document.
 *
 *   npm run pages                              list inspectable documents
 *   npm run pages -- vcockpit                  filter by title
 *
 *   npm run probe -- "A220 CTP" p04-iframe.js  run a probe in a page
 *   npm run probe -- 24 p01-wasm-shell.js
 *   npm run probe -- 24 p03-replay.js --settle 6000
 *
 *   npm run eval -- 24 "document.title"        one expression
 *   npm run eval -- 24 "__P02.report()"        drive a probe already installed
 *
 * Page selection accepts an id or a case-insensitive title substring, and
 * refuses an ambiguous match rather than guessing — the wrong panel gives a
 * meaningless result, not a weaker one.
 *
 * Probe output is echoed and written to results/.
 */

import { readFileSync, mkdirSync, writeFileSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, join, basename } from "node:path"

import { listPages, selectPage, Inspector, runProbe, drain } from "./lib/inspector.mjs"

const HERE = dirname(fileURLToPath(import.meta.url))
const ROOT = join(HERE, "..")

const argv = process.argv.slice(2)
const mode = argv[0]
const rest = argv.slice(1)

function flag(name, fallback) {
  const i = rest.indexOf(`--${name}`)
  if (i === -1) return fallback
  const v = rest[i + 1]
  rest.splice(i, 2)
  return v
}

function save(name, text) {
  mkdirSync(join(ROOT, "results"), { recursive: true })
  const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")
  const file = join(ROOT, "results", `${name}-${stamp}.txt`)
  writeFileSync(file, text.endsWith("\n") ? text : text + "\n")
  return file
}

function slug(s) {
  return String(s).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 48)
}

async function main() {
  if (mode === "pages") {
    const pages = await listPages()
    const filter = rest[0] ? String(rest[0]).toLowerCase() : null
    const shown = filter ? pages.filter((p) => String(p.title || "").toLowerCase().includes(filter)) : pages
    console.log(`${shown.length} of ${pages.length} inspectable documents\n`)
    for (const p of shown) {
      console.log(`  ${String(p.id).padStart(3)}  ${String(p.title || "(untitled)").padEnd(38)} ${p.url || ""}`)
    }
    if (!shown.length) console.log("  (nothing matched)")
    return
  }

  const settle = Number(flag("settle", 2500))
  const target = rest[0]
  if (!target) throw new Error("give a page id or title substring")

  const pages = await listPages()
  const page = selectPage(pages, target)

  const insp = new Inspector(page.id)
  await insp.open()

  try {
    if (mode === "eval") {
      const expr = rest.slice(1).join(" ")
      if (!expr) throw new Error("give an expression to evaluate")
      // Anything the expression logs is wanted too — a probe's report() prints
      // rather than returns.
      await insp.evaluate((await import("./lib/inspector.mjs")).CAPTURE_SHIM)
      const value = await insp.evaluate(expr)
      const logged = await drain(insp)
      if (logged) console.log(logged)
      if (value !== undefined) console.log(typeof value === "string" ? value : JSON.stringify(value, null, 1))
      return
    }

    if (mode === "probe") {
      const file = rest[1]
      if (!file) throw new Error("give a probe filename, e.g. p04-iframe.js")
      const path = file.includes("/") || file.includes("\\") ? file : join(HERE, file)
      const source = readFileSync(path, "utf8")

      const header =
        `page ${page.id}  ${page.title}\n` +
        `url  ${page.url}\n` +
        `probe ${basename(path)}   settle ${settle}ms\n` +
        `${new Date().toISOString()}\n` +
        "=".repeat(72) + "\n"

      const out = await runProbe(insp, source, { settle })
      const text = header + (out || "(the probe printed nothing)")
      console.log(text)
      console.log(`\nwritten: ${save(`${basename(path, ".js")}-${slug(page.title)}`, text)}`)
      return
    }

    throw new Error(`unknown mode ${JSON.stringify(mode)} — use pages, probe or eval`)
  } finally {
    insp.close()
  }
}

main().catch((e) => {
  console.error("\n" + e.message + "\n")
  process.exit(1)
})
