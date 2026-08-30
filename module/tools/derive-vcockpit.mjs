/*
 * Produce our patched VCockpit.js.
 *
 * Getting JavaScript into every panel document requires overriding
 * html_ui/Pages/VCockpit/Core/VCockpit.js, because it is the one file the
 * simulator loads into every VCockpit panel. FS Copilot does the same thing, for
 * the same reason.
 *
 * MSFS 2024 streams its core packages, so the stock file is not on disk. What is
 * on disk is FS Copilot's copy, which its own header describes as a direct copy of
 * the original plus two clearly delimited blocks. Stripping those blocks gives
 * back the stock file, and this script does that mechanically rather than by hand
 * so it can be re-run after a simulator update.
 *
 *   node module/tools/derive-vcockpit.mjs
 *   node module/tools/derive-vcockpit.mjs --stock-only    (write the stock file, no patch)
 *
 * If the markers ever stop matching, this fails loudly rather than emitting a
 * half-stripped file. The alternative source, if FS Copilot's copy is ever
 * unavailable, is the inspector:
 *   npm run sources -- <page> get VCockpit.js
 * though that returns whichever copy is currently overriding, so it is only stock
 * when no patching package is installed.
 */

import { readFileSync, writeFileSync, mkdirSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { dirname, join } from "node:path"

const HERE = dirname(fileURLToPath(import.meta.url))
const ROOT = join(HERE, "..", "..")
const SOURCE = join(ROOT, "..", "fscopilot", "src", "FsCopilot.Bridge",
  "PackageSources", "HTML_UI", "Pages", "VCockpit", "Core", "VCockpit.js")
const OUT_DIR = join(ROOT, "module", "PackageSources", "html_ui", "Pages", "VCockpit", "Core")
const STOCK = join(ROOT, "module", "vendor", "VCockpit.stock.js")

const stockOnly = process.argv.includes("--stock-only")

/** Cut an inclusive region delimited by two markers, asserting it is found once. */
function cut(text, open, close, label) {
  const a = text.indexOf(open)
  if (a === -1) throw new Error(`derive: could not find the opening marker for ${label}`)
  const b = text.indexOf(close, a)
  if (b === -1) throw new Error(`derive: could not find the closing marker for ${label}`)
  const before = text.slice(0, a)
  const after = text.slice(b + close.length)
  // Leave no double blank line where the block was.
  return before.replace(/[ \t]*$/, "") + after.replace(/^\n/, "")
}

let src = readFileSync(SOURCE, "utf8")
const originalLength = src.length

// 1. FS Copilot's own header comment, which is not part of the stock file.
if (src.startsWith("/*")) {
  const end = src.indexOf("*/")
  const head = src.slice(0, end)
  if (head.includes("FS Copilot")) src = src.slice(end + 2).replace(/^\s*\n/, "")
}

// 2. The two integration blocks, both delimited by the same marker pair.
src = cut(src, "/* FS Copilot Integration */", "/* End of FS Copilot Integration */", "top-level block")
src = cut(src, "/* FS Copilot Integration */", "/* End of FS Copilot Integration */", "createInstrument block")

// Sanity: the stock file must still be recognisably itself.
for (const needle of ["class VCockpitPanel", "loadNextInstrument", "createInstrument", "ShowVCockpitPanel"]) {
  if (!src.includes(needle)) throw new Error(`derive: stripped file is missing ${needle} — markers are wrong`)
}
for (const banned of ["fscListeners", "templateToLoad", "new Hook("]) {
  if (src.includes(banned)) throw new Error(`derive: ${banned} survived the strip`)
}

mkdirSync(dirname(STOCK), { recursive: true })
writeFileSync(STOCK, src)
console.log(`stock VCockpit.js  ${originalLength} -> ${src.length} chars  ->  ${STOCK}`)

if (stockOnly) process.exit(0)

/* ---- our patch ---------------------------------------------------------- */

// Deliberately smaller than FS Copilot's. We do not need the addEventListener
// monkeypatch (that exists to decide which elements are worth naming, and we do
// not name elements), and we hold pending instruments in an ARRAY rather than a
// single slot — FS Copilot's single `templateToLoad` silently drops every
// instrument but the last when a panel has more than one and the imports have not
// resolved yet, which is a real case: the A350 has such a panel.
const LOADER = `
/* FSCPP */
var FSCPP_pending = [];
var FSCPP_loaded = false;

Include.addImports(['/FSCPP/link.js'], () =>
Include.addImports(['/FSCPP/agent.js'], () =>
Include.addImports(['/FSCPP/boot.js'], () => {
    FSCPP_loaded = true;
    console.log('[FSCPP] scripts loaded');
    while (FSCPP_pending.length) {
        try { FSCPP_boot(FSCPP_pending.shift()); }
        catch (e) { console.error('[FSCPP] boot failed', e); }
    }
})));
/* end FSCPP */
`

const HOOK = `
            /* FSCPP */
            if (FSCPP_loaded) {
                try { FSCPP_boot(template); }
                catch (error) { console.error('[FSCPP] boot failed', error); }
            }
            else {
                FSCPP_pending.push(template);
            }
            /* end FSCPP */
`

// Top-level loader goes just before the class definitions, matching where FS
// Copilot puts its own, so the Include chain starts as early as possible.
const anchor = "class VCockpitPanel extends HTMLElement {"
if (!src.includes(anchor)) throw new Error("derive: could not find the VCockpitPanel anchor")
let out = src.replace(anchor, LOADER + "\n" + anchor)

// The per-instrument hook goes where the instrument is known to exist and be
// attached: immediately after the title line in createInstrument.
const titleLine = 'document.title += " - " + template.instrumentIdentifier;'
if (!out.includes(titleLine)) throw new Error("derive: could not find the createInstrument anchor")
out = out.replace(titleLine, titleLine + "\n" + HOOK)

mkdirSync(OUT_DIR, { recursive: true })
const dest = join(OUT_DIR, "VCockpit.js")
writeFileSync(dest, out)
console.log(`patched VCockpit.js  ${out.length} chars  ->  ${dest}`)
