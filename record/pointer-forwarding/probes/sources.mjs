/*
 * Read the simulator's own source through the remote inspector.
 *
 * MSFS 2024 streams its core packages rather than installing them, so the
 * JavaScript that actually runs the cockpit — BaseInstrument.js, VCockpit.js,
 * WasmSimCanvas.js, simvar.js, coherent.js — is not on disk anywhere. It is,
 * however, all loaded into panel documents, and the inspector will hand it over:
 *
 *   Page.getResourceTree      what a document loaded
 *   Page.getResourceContent   the bytes of any of it
 *   Page.searchInResources    full-text search across all of it
 *
 * That makes "what does MSFS actually do here" an answerable question rather
 * than a guess. It exists because one guess — that Coherent GT has PointerEvent —
 * cost a design assumption that thirty seconds of looking would have caught.
 *
 *   npm run sources -- <page> list              what this document loaded
 *   npm run sources -- <page> grep <text>       search every loaded resource
 *   npm run sources -- <page> get <fragment>    print one, matched by url substring
 *   npm run sources -- <page> dump [dir]        save every script to disk
 *
 * <page> is an id or a case-insensitive title substring, as everywhere else.
 * Dumps land in sim-sources/<page-slug>/ and are gitignored — they are Asobo's
 * code, kept locally for grepping, not committed.
 */

import { mkdirSync, writeFileSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, join } from "node:path"

import { listPages, selectPage, Inspector } from "./lib/inspector.mjs"

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..")

const [target, mode, ...rest] = process.argv.slice(2)
if (!target || !mode) {
  console.error("usage: npm run sources -- <page> list|grep|get|dump [arg]")
  process.exit(1)
}

/** Flatten the frame tree; child frames carry their own resources. */
function flatten(node, out = [], depth = 0) {
  const frameId = node.frame.id
  out.push({ frameId, depth, url: node.frame.url, type: "Frame", mimeType: "", isFrame: true })
  for (const r of node.resources || []) out.push({ ...r, frameId, depth })
  for (const c of node.childFrames || []) flatten(c, out, depth + 1)
  return out
}

const slug = (s) => String(s).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 48)
const leaf = (u) => (u.split("?")[0].split("/").pop() || "index").toLowerCase()

const page = selectPage(await listPages(), target)
const insp = new Inspector(page.id)
await insp.open()

try {
  await insp.send("Page.enable", {})
  const tree = await insp.send("Page.getResourceTree", {})
  const items = flatten(tree.result.frameTree)
  const scripts = items.filter((r) => /script/i.test(r.type) || /javascript/i.test(r.mimeType))

  console.log(`page ${page.id}  ${page.title}`)
  console.log(`${items.length} resources, ${scripts.length} scripts\n`)

  if (mode === "list") {
    for (const r of items) {
      const pad = "  ".repeat(r.depth)
      console.log(`  ${pad}${String(r.type).padEnd(10)} ${String(r.mimeType || "").padEnd(22)} ${r.url}`)
    }

  } else if (mode === "grep") {
    const text = rest.join(" ")
    if (!text) throw new Error("give something to search for")
    // Ask the engine first — it searches resources we might not have enumerated.
    const res = await insp.send("Page.searchInResources", { text, caseSensitive: false, isRegex: false })
    const hits = (res.result && res.result.result) || []
    console.log(`Page.searchInResources: ${hits.length} hits for ${JSON.stringify(text)}\n`)
    for (const h of hits) {
      console.log(`  ${h.url}`)
      for (const m of h.matches || []) {
        console.log(`      line ${m.lineNumber}: ${String(m.lineContent).trim().slice(0, 160)}`)
      }
    }
    // The engine's search can miss; confirm by scanning the scripts ourselves.
    let own = 0
    const needle = text.toLowerCase()
    for (const r of scripts) {
      const c = await insp.send("Page.getResourceContent", { frameId: r.frameId, url: r.url })
      const body = (c.result && c.result.content) || ""
      let at = -1, n = 0
      while ((at = body.toLowerCase().indexOf(needle, at + 1)) !== -1 && n < 3) {
        if (!n) console.log(`\n  [direct scan] ${r.url}`)
        console.log(`      … ${body.slice(Math.max(0, at - 90), at + needle.length + 90).replace(/\s+/g, " ")} …`)
        n++; own++
      }
    }
    if (!hits.length && !own) console.log("  nothing found in either the engine's index or a direct scan")

  } else if (mode === "get") {
    const frag = (rest[0] || "").toLowerCase()
    const hit = items.find((r) => r.url.toLowerCase().includes(frag))
    if (!hit) throw new Error(`no loaded resource whose url contains ${JSON.stringify(frag)}`)
    const c = await insp.send("Page.getResourceContent", { frameId: hit.frameId, url: hit.url })
    if (c.error) throw new Error(JSON.stringify(c.error))
    console.log(`// ${hit.url}\n`)
    console.log(c.result.content)

  } else if (mode === "dump") {
    const dir = rest[0] || join(ROOT, "sim-sources", slug(page.title))
    mkdirSync(dir, { recursive: true })
    let saved = 0, bytes = 0, failed = 0
    for (const r of scripts) {
      const c = await insp.send("Page.getResourceContent", { frameId: r.frameId, url: r.url })
      const body = (c.result && c.result.content) || ""
      if (!body) { failed++; continue }
      // Flatten the path into the filename so two files never collide.
      const name = r.url.replace(/^[a-z]+:\/\//i, "").replace(/[^A-Za-z0-9._-]+/g, "_").slice(-120)
      writeFileSync(join(dir, name), body)
      saved++; bytes += body.length
    }
    console.log(`saved ${saved} scripts (${(bytes / 1024).toFixed(0)} KB) to ${dir}`)
    if (failed) console.log(`${failed} returned no content`)

  } else {
    throw new Error(`unknown mode ${JSON.stringify(mode)}`)
  }
} finally {
  insp.close()
}
