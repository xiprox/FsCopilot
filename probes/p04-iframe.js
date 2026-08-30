/*
 * P04 — What is inside a panel, and can we reach across an iframe boundary?
 *
 * Settles Q06 (cross-origin reach) and Q07 (what the TDS GTN actually is).
 * Also usable as a general "what kind of surface is this" classifier — run it on
 * any panel and it will say which of the five surfaces in docs/03-scope.md it is.
 *
 * Run in the Coherent GT debugger Console with the panel selected in the frame
 * picker. Targets worth doing: Fenix EFB (captain and FO), TDS GTN 750, A350
 * MFD, A220 CTP and MKP.
 *
 *   __P04.frames        the iframe records
 *   __P04.watch(0)      attach a logging listener inside frame 0, if reachable
 *
 * Paste the report into results/ as p04-<aircraft>-<panel>-<date>.txt.
 */

(function () {
  const L = []
  const say = (s) => L.push(s == null ? "" : String(s))

  function describe(n) {
    if (!n) return "(null)"
    let s = n.tagName ? n.tagName.toLowerCase() : String(n.nodeName)
    if (n.id) s += "#" + n.id
    else if (n.getAttribute && n.getAttribute("class")) s += "." + String(n.getAttribute("class")).split(/\s+/)[0]
    return s
  }

  function originOf(url) {
    try {
      if (!url || url === "about:blank") return url || "(none)"
      const m = /^([a-z-]+:)\/\/([^/]*)/i.exec(url)
      return m ? m[1] + "//" + m[2] : url.split("/")[0] || "(relative)"
    } catch (e) { return "(unparseable)" }
  }

  say("=========================================================")
  say("P04  panel composition and iframe reach")
  say("=========================================================")
  say("title      " + document.title)
  say("href       " + location.href)
  say("origin     " + originOf(location.href))

  // ---- what kind of surface is this? --------------------------------------
  const panel = document.getElementById("panel")
  say("")
  say("--- instruments ---")
  if (!panel) say("  no #panel in this document")
  else {
    for (let i = 0; i < panel.children.length; i++) {
      const el = panel.children[i]
      say("  <" + el.tagName.toLowerCase() + ">  id=" + el.instrumentIdentifier +
          "  interactive=" + el.isInteractive)
      const url = el.getAttribute("url") || el.getAttribute("Url")
      if (url) say("      url  " + url)
      const ig = el.getAttribute("data-input-group")
      if (ig) say("      data-input-group  " + ig)
    }
  }

  const counts = {
    svg: document.getElementsByTagName("svg").length,
    canvas: document.getElementsByTagName("canvas").length,
    img: document.getElementsByTagName("img").length,
    iframe: document.getElementsByTagName("iframe").length,
    elements: document.getElementsByTagName("*").length
  }
  say("")
  say("--- composition ---")
  say("  " + counts.elements + " elements   svg=" + counts.svg + "  canvas=" + counts.canvas +
      "  img=" + counts.img + "  iframe=" + counts.iframe)

  const liveView = Array.prototype.filter.call(document.getElementsByTagName("img"), (im) =>
    (im.getAttribute("src") || "").indexOf("LiveView") !== -1)
  if (liveView.length) {
    say("  " + liveView.length + " live-view <img>: " +
        liveView.map((im) => im.getAttribute("src")).join(", "))
  }

  let surface = "unclassified"
  if (counts.iframe) surface = "external iframe"
  else if (liveView.length) surface = "WASM gauge (live-view blit)"
  else if (counts.canvas) surface = "canvas"
  else if (counts.svg) surface = "SVG (likely React)"
  else if (counts.elements > 20) surface = "HTML DOM"
  say("  -> looks like: " + surface)

  // ---- iframes -------------------------------------------------------------
  const iframes = Array.prototype.slice.call(document.getElementsByTagName("iframe"))
  const records = []

  say("")
  say("--- iframes ---")
  if (!iframes.length) {
    say("  none. Q06 does not apply to this panel.")
  } else {
    say("  window.frames.length = " + window.frames.length)
    iframes.forEach((f, i) => {
      const src = f.getAttribute("src") || ""
      const rec = { index: i, el: f, src: src, origin: originOf(src) }
      say("")
      say("  [" + i + "] " + describe(f))
      say("      src        " + (src || "(none)"))
      say("      origin     " + rec.origin + (rec.origin === originOf(location.href) ? "  (same as parent)" : "  (CROSS-ORIGIN)"))

      // contentDocument is the whole question. Reading it throws or returns null
      // when blocked, and both outcomes are informative.
      try {
        const doc = f.contentDocument
        rec.doc = doc
        if (!doc) say("      contentDocument  null — blocked, or not yet loaded")
        else {
          say("      contentDocument  REACHABLE")
          say("        readyState " + doc.readyState)
          say("        title      " + doc.title)
          say("        elements   " + doc.getElementsByTagName("*").length +
              "   svg=" + doc.getElementsByTagName("svg").length +
              "  canvas=" + doc.getElementsByTagName("canvas").length)
          say("        body       " + describe(doc.body))
          // If we can read it we can almost certainly listen on it, which is what
          // capture-inside-the-child would need.
          try {
            const noop = function () {}
            doc.addEventListener("pointerdown", noop, true)
            doc.removeEventListener("pointerdown", noop, true)
            say("        listeners  attachable — capture could run inside this frame")
            rec.attachable = true
          } catch (e) {
            say("        listeners  NOT attachable: " + e)
            rec.attachable = false
          }
        }
      } catch (e) {
        rec.error = String(e)
        say("      contentDocument  THREW: " + e)
      }

      try {
        const w = f.contentWindow
        say("      contentWindow    " + (w ? "non-null" : "null") +
            (w ? "  (postMessage available: " + (typeof w.postMessage === "function") + ")" : ""))
      } catch (e) { say("      contentWindow    THREW: " + e) }

      records.push(rec)
    })
  }

  say("")
  say("--- read this as ---")
  say("  no iframe                     -> this panel's clicks land in this document;")
  say("     Q06 is irrelevant here and P02/P03 are the probes that matter.")
  say("  iframe, contentDocument null  -> same-origin policy is enforced. A")
  say("     cross-origin child needs the addon developer's cooperation; there is")
  say("     no unilateral route in.")
  say("  iframe, REACHABLE + attachable-> we can run capture and replay inside the")
  say("     child. Note this is required even for a same-origin child: a real click")
  say("     inside an iframe never reaches the parent's listeners.")
  say("  live-view <img> and no iframe -> WASM gauge. Q05 decides it, not this.")

  window.__P04 = {
    frames: records,
    watch: function (i) {
      const rec = records[i]
      if (!rec) { console.warn("[P04] no frame " + i); return }
      if (!rec.doc) { console.warn("[P04] frame " + i + " is not reachable"); return }
      const seen = []
      const types = ["pointerdown", "pointerup", "mousedown", "mouseup", "click"]
      types.forEach((t) => rec.doc.addEventListener(t, (ev) => {
        seen.push(t + " trusted=" + ev.isTrusted + " at (" + ev.clientX + "," + ev.clientY + ") on " + describe(ev.target))
        console.log("[P04 frame " + i + "] " + seen[seen.length - 1])
      }, true))
      console.log("[P04] listening inside frame " + i + ". Click inside it in the cockpit.")
      return seen
    }
  }

  console.log(L.join("\n"))
})();
