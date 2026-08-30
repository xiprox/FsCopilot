/*
 * P08 — What is actually available in a Coherent GT panel document?
 *
 * Exists because a design doc confidently specified dispatching PointerEvents,
 * and the constructor does not exist in this engine. That was discoverable in
 * thirty seconds. This probe is those thirty seconds, run once and written down.
 *
 * Reports what the engine has, not what a browser would have. Anything marked
 * MISSING is a thing no amount of clever code will bring back.
 *
 *   npm run probe -- <page> p08-environment.js
 *
 * Worth re-running after a simulator update: this is exactly the sort of thing
 * that changes silently.
 */

(function () {
  const L = []
  const say = (s) => L.push(s == null ? "" : String(s))

  function has(path, root) {
    try {
      const parts = path.split(".")
      let o = root || window
      for (const p of parts) { if (o == null) return false; o = o[p] }
      return o !== undefined && o !== null
    } catch (e) { return false }
  }

  function group(title, names, root) {
    say("")
    say("--- " + title + " ---")
    const present = [], missing = []
    names.forEach((n) => (has(n, root) ? present : missing).push(n))
    if (present.length) say("  have    " + present.join(", "))
    if (missing.length) say("  MISSING " + missing.join(", "))
  }

  /** Language features need evaluating, not property lookups. */
  function syntax(label, src) {
    try { (0, eval)(src); return label } catch (e) { return null }
  }

  say("=========================================================")
  say("P08  Coherent GT environment")
  say("=========================================================")
  say("document   " + document.title)
  say("url        " + location.href)
  say("userAgent  " + navigator.userAgent)
  say("platform   " + navigator.platform + "   vendor=" + navigator.vendor)
  say("viewport   " + window.innerWidth + "x" + window.innerHeight + "  dpr=" + window.devicePixelRatio)
  say("languages  " + navigator.language)

  group("event constructors", [
    "Event", "CustomEvent", "MouseEvent", "PointerEvent", "TouchEvent", "WheelEvent",
    "KeyboardEvent", "InputEvent", "FocusEvent", "DragEvent", "CompositionEvent",
    "UIEvent", "ProgressEvent", "MessageEvent", "ErrorEvent"
  ])

  group("pointer / touch support", [
    "navigator.maxTouchPoints", "PointerEvent", "document.elementFromPoint",
    "document.elementsFromPoint", "Element.prototype.setPointerCapture",
    "Element.prototype.releasePointerCapture", "document.hasFocus"
  ])

  group("observers and DOM", [
    "MutationObserver", "IntersectionObserver", "ResizeObserver", "PerformanceObserver",
    "customElements", "ShadowRoot", "Element.prototype.attachShadow",
    "Element.prototype.closest", "Element.prototype.matches",
    "Element.prototype.getBoundingClientRect", "DOMRect", "getComputedStyle",
    "document.createNodeIterator", "document.createTreeWalker", "Node.prototype.isConnected"
  ])

  group("network", [
    "fetch", "XMLHttpRequest", "WebSocket", "EventSource", "navigator.sendBeacon",
    "Request", "Response", "Headers", "AbortController", "FormData", "Blob", "FileReader"
  ])

  group("storage", [
    "localStorage", "sessionStorage", "indexedDB", "caches", "document.cookie"
  ])

  group("timing and scheduling", [
    "requestAnimationFrame", "cancelAnimationFrame", "requestIdleCallback",
    "queueMicrotask", "performance.now", "performance.getEntriesByType",
    "setTimeout", "setInterval", "Promise", "MessageChannel"
  ])

  group("workers and isolation", [
    "Worker", "SharedWorker", "navigator.serviceWorker", "structuredClone",
    "BroadcastChannel", "postMessage"
  ])

  group("javascript builtins", [
    "Proxy", "Reflect", "Symbol", "WeakMap", "WeakSet", "WeakRef", "FinalizationRegistry",
    "BigInt", "Intl", "Map", "Set", "ArrayBuffer", "SharedArrayBuffer", "TextEncoder",
    "TextDecoder", "URL", "URLSearchParams", "crypto.getRandomValues", "JSON.parse"
  ])

  say("")
  say("--- language syntax ---")
  const syn = [
    ["optional chaining a?.b", "({}).a?.b"],
    ["nullish ??", "null ?? 1"],
    ["spread", "[...[1,2]]"],
    ["arrow fn", "(()=>1)()"],
    ["class fields", "class T{x=1};new T().x"],
    ["private fields", "class T{#x=1;get(){return this.#x}};new T().get()"],
    ["async/await", "(async()=>1)()"],
    ["generators", "(function*(){yield 1})().next()"],
    ["template literal", "`a${1}b`"],
    ["destructuring", "const {a=1}={};a"],
    ["Array.prototype.at", "[1].at(0)"],
    ["Array.prototype.flat", "[[1]].flat()"],
    ["Object.fromEntries", "Object.fromEntries([['a',1]])"],
    ["String.replaceAll", "'a'.replaceAll('a','b')"],
    ["logical assignment ||=", "let z=0; z||=1; z"],
    ["numeric separators", "1_000"],
    ["Promise.allSettled", "Promise.allSettled([])"],
    ["globalThis", "globalThis"]
  ]
  const ok = [], no = []
  syn.forEach(([label, src]) => (syntax(label, src) ? ok : no).push(label))
  if (ok.length) say("  have    " + ok.join(", "))
  if (no.length) say("  MISSING " + no.join(", "))

  group("MSFS globals", [
    "Coherent", "SimVar", "Include", "RegisterViewListener", "RegisterCommBusListener",
    "BaseInstrument", "Avionics", "SimPlane", "Simplane", "GameState", "EDITION_MODE",
    "g_modelBehaviorsHelper", "getIsapiUrl", "Utils", "DataStore", "LaunchFlowEvent",
    "checkAutoload", "diffAndSetAttribute", "diffAndSetStyle", "StyleProperty"
  ])

  say("")
  say("--- Coherent surface ---")
  if (typeof Coherent === "undefined") say("  Coherent is not defined in this document")
  else {
    const names = []
    let o = Coherent, d = 0
    while (o && d++ < 4) { names.push(...Object.getOwnPropertyNames(o)); o = Object.getPrototypeOf(o) }
    say("  " + names.filter((n, i, s) => s.indexOf(n) === i && n !== "constructor").sort().join(", "))
  }

  say("")
  say("--- FS Copilot bridge ---")
  say("  fscListeners " + (window.fscListeners ? "present — VCockpit.js is the patched copy" : "absent"))
  say("  Hook class   " + (typeof Hook !== "undefined" ? "present" : "absent"))

  say("")
  say("--- what this means for replay ---")
  say("  PointerEvent MISSING -> dispatch MouseEvent only. Any framework handler")
  say("     bound to onPointerDown is dead code in this engine.")
  say("  TouchEvent MISSING   -> touch synthesis is not an option either.")
  say("  elementFromPoint     -> present, which is what coordinate replay needs.")

  console.log(L.join("\n"))
})();
