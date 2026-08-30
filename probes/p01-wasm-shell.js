/*
 * P01 — Does WasmInstrument.html route DOM input into the WASM module?
 *
 * Settles Q05. Run in the Coherent GT debugger Console, with an A350 display
 * panel selected in the frame picker (MFD, EFIS, SD — any wasm-instrument one).
 * Also worth running on the A220's VCockpit01 and on a PMDG display for
 * comparison; the shell is a core sim file and should be identical everywhere.
 *
 * Prints a report. Full sources are stashed on window.__P01 — after it runs:
 *
 *   __P01.files                     the fetched sources, keyed by url
 *   __P01.show('WasmInstrument')    print one in full
 *   __P01.find('pointer')           search every fetched file
 *
 * Paste the printed report into results/ as p01-<aircraft>-<date>.txt.
 */

(function () {
  var OUT = [];
  function say(s) { OUT.push(s == null ? "" : String(s)); }
  function flush() { console.log(OUT.join("\n")); }

  // Tokens worth knowing about in the shell. Presence of the first group means
  // the shell listens to the DOM; the second means it has a channel to the
  // module; the third is how it would carry coordinates.
  var TOKENS = [
    "addEventListener", "pointerdown", "pointerup", "pointermove",
    "mousedown", "mouseup", "mousemove", "onclick", "click",
    "MouseEvent", "PointerEvent", "touchstart",
    "CommBus", "callWasm", "RegisterCommBusListener", "RegisterViewListener",
    "Coherent.trigger", "Coherent.on", "Coherent.call",
    "clientX", "offsetX", "pageX", "getBoundingClientRect", "elementFromPoint",
    "MOUSE", "mouse_rect", "input", "InputGroup", "inputGroup",
    "LiveView", "wasm-sim-canvas", "wasm_gauge", "wasm_module"
  ];

  function get(url) {
    return new Promise(function (resolve) {
      try {
        var x = new XMLHttpRequest();
        x.open("GET", url, true);
        x.onload = function () { resolve({ url: url, ok: true, status: x.status, text: x.responseText || "" }); };
        x.onerror = function () { resolve({ url: url, ok: false, status: x.status || 0, text: "" }); };
        x.send();
      } catch (e) {
        resolve({ url: url, ok: false, status: -1, text: "", error: String(e) });
      }
    });
  }

  function scan(name, text) {
    var hits = [];
    for (var i = 0; i < TOKENS.length; i++) {
      var tok = TOKENS[i];
      var n = 0, at = -1, first = [];
      while ((at = text.indexOf(tok, at + 1)) !== -1) {
        n++;
        if (first.length < 2) {
          first.push(text.slice(Math.max(0, at - 90), at + tok.length + 90).replace(/\s+/g, " "));
        }
        if (n > 500) break;
      }
      if (n) hits.push({ tok: tok, n: n, first: first });
    }
    say("");
    say("--- tokens in " + name + " ---");
    if (!hits.length) { say("  (none of the watched tokens appear)"); return; }
    for (var j = 0; j < hits.length; j++) {
      say("  " + hits[j].tok + "  x" + hits[j].n);
      for (var k = 0; k < hits[j].first.length; k++) say("      … " + hits[j].first[k] + " …");
    }
  }

  // ---- 1. where are we -----------------------------------------------------
  say("=========================================================");
  say("P01  WasmInstrument input routing");
  say("=========================================================");
  say("title    " + document.title);
  say("href     " + location.href);
  say("bridge   " + (window.fscListeners ? "fscopilot-bridge IS loaded in this panel" : "no fscListeners — bridge not loaded here"));
  say("hook     " + (typeof Hook !== "undefined" ? "Hook class present" : "no Hook class"));

  var panel = document.getElementById("panel");
  say("");
  say("--- panel children ---");
  if (!panel) {
    say("  no #panel in this document");
  } else {
    for (var i = 0; i < panel.children.length; i++) {
      var el = panel.children[i];
      var attrs = [];
      for (var a = 0; a < el.attributes.length; a++) {
        var v = el.attributes[a].value;
        attrs.push(el.attributes[a].name + "=" + (v.length > 120 ? v.slice(0, 120) + "…" : v));
      }
      say("  <" + el.tagName.toLowerCase() + ">");
      say("      ctor              " + (el.constructor && el.constructor.name));
      say("      instrumentId      " + el.instrumentIdentifier);
      say("      isInteractive     " + el.isInteractive);
      say("      urlConfig.noEvent " + (el.urlConfig && el.urlConfig.noEvent));
      for (var b = 0; b < attrs.length; b++) say("      @ " + attrs[b]);
    }
  }

  // ---- 2. what does the custom element expose? -----------------------------
  // The method names on the prototype are the fastest tell. A shell that takes
  // DOM input will have something mouse- or pointer-shaped here.
  say("");
  say("--- custom element prototypes ---");
  ["wasm-instrument", "wasm-sim-canvas"].forEach(function (name) {
    var ctor = window.customElements && customElements.get(name);
    if (!ctor) { say("  " + name + ": not defined in this document"); return; }
    var names = [], proto = ctor.prototype, depth = 0;
    while (proto && proto !== HTMLElement.prototype && depth++ < 6) {
      names = names.concat(Object.getOwnPropertyNames(proto));
      proto = Object.getPrototypeOf(proto);
    }
    say("  " + name + " (" + ctor.name + "):");
    say("      " + names.filter(function (n, i, s) { return s.indexOf(n) === i; }).sort().join(", "));
  });

  // ---- 3. is anything listening on this document? --------------------------
  // getEventListeners is an inspector builtin and may not exist here; the
  // bridge's own fscListeners map is a usable fallback where the bridge is loaded.
  say("");
  say("--- listeners ---");
  try {
    if (typeof getEventListeners === "function") {
      var gl = getEventListeners(document);
      say("  document: " + Object.keys(gl).join(", "));
      if (panel && panel.children[0]) {
        say("  instrument: " + Object.keys(getEventListeners(panel.children[0])).join(", "));
      }
    } else {
      say("  getEventListeners not available in this console");
    }
  } catch (e) { say("  getEventListeners threw: " + e); }

  // ---- 4. fetch the shell and whatever it pulls in --------------------------
  var SHELL = "coui://html_ui/Pages/VCockpit/Instruments/WasmInstrument/WasmInstrument.html";
  var CANDIDATES = [
    SHELL,
    "/Pages/VCockpit/Instruments/WasmInstrument/WasmInstrument.html",
    "coui://html_ui/Pages/VCockpit/Instruments/WasmInstrument/WasmInstrument.js"
  ];

  window.__P01 = {
    files: {},
    show: function (frag) {
      var k = Object.keys(this.files).filter(function (u) { return u.indexOf(frag) !== -1; })[0];
      console.log(k ? this.files[k] : "no fetched file matching " + frag);
    },
    find: function (needle) {
      var files = this.files;
      Object.keys(files).forEach(function (u) {
        var t = files[u], at = -1, n = 0;
        while ((at = t.indexOf(needle, at + 1)) !== -1 && n++ < 20) {
          console.log(u + " @" + at + "  … " + t.slice(Math.max(0, at - 100), at + needle.length + 100).replace(/\s+/g, " ") + " …");
        }
      });
    }
  };

  Promise.all(CANDIDATES.map(get)).then(function (results) {
    say("");
    say("--- fetch ---");
    var got = [];
    results.forEach(function (r) {
      say("  " + (r.ok ? "ok  " : "FAIL") + " status=" + r.status + " len=" + r.text.length + "  " + r.url);
      if (r.error) say("       " + r.error);
      if (r.ok && r.text.length) { window.__P01.files[r.url] = r.text; got.push(r); }
    });

    if (!got.length) {
      say("");
      say("  Nothing fetched. Either coui:// is not readable via XHR from here, or the");
      say("  path is wrong. Fall back to the debugger's Resources tab: find");
      say("  WasmInstrument.html under html_ui and read it there.");
      flush();
      return;
    }

    // The shell is small; the logic is normally in a script it imports.
    var shell = got[0].text;
    var refs = [];
    var rx = /(?:import-script|src|href)\s*=\s*"([^"]+\.js[^"]*)"/g, m;
    while ((m = rx.exec(shell)) !== null) refs.push(m[1]);
    say("");
    say("--- scripts referenced by the shell ---");
    say(refs.length ? "  " + refs.join("\n  ") : "  (none)");

    got.forEach(function (r) { scan(r.url.split("/").pop(), r.text); });

    if (!refs.length) { finish(); return; }

    Promise.all(refs.map(function (p) {
      return get(p.indexOf("//") === -1 && p[0] === "/" ? "coui://html_ui" + p : p);
    })).then(function (more) {
      say("");
      say("--- referenced scripts ---");
      more.forEach(function (r) {
        say("  " + (r.ok ? "ok  " : "FAIL") + " status=" + r.status + " len=" + r.text.length + "  " + r.url);
        if (r.ok && r.text.length) {
          window.__P01.files[r.url] = r.text;
          scan(r.url.split("/").pop(), r.text);
        }
      });
      finish();
    });

    function finish() {
      say("");
      say("--- read this as ---");
      say("  addEventListener + clientX/offsetX in the shell  -> it takes DOM input;");
      say("     synthetic pointer events at coordinates should reach the module.");
      say("  CommBus/callWasm/Coherent.trigger with coordinates -> even better: we can");
      say("     call that channel directly and skip DOM synthesis entirely.");
      say("  neither -> the sim routes cockpit input to the gauge natively and no");
      say("     DOM-level scheme reaches WASM instruments. A350/A400M/PMDG are out.");
      say("");
      say("  Full sources on window.__P01 — __P01.show('WasmInstrument'), __P01.find('mouse')");
      flush();
    }
  });
})();
