/*
 * P05 — Can a coui:// panel document reach loopback, and over what?
 *
 * Settles Q08. If a WebSocket opens, the interaction path can bypass CommBus,
 * the WASM module and SimConnect entirely — see docs/04-transport.md for what
 * that buys.
 *
 * Start the other half first:
 *
 *   npm run probe:05-server
 *
 * Then paste this into the Coherent GT debugger console, on any panel — this one
 * does not care which, because it is testing the document's network permissions
 * rather than anything about the instrument.
 *
 *   __P05.run()          run all three tests again
 *   __P05.send('hi')     send on the open socket, if there is one
 *   __P05.close()
 *
 * Three tests, because the interesting failures are partial: HTTP working while
 * WebSocket does not is a different answer from loopback being blocked outright.
 */

(function () {
  const PORT = 9002
  const HTTP = "http://127.0.0.1:" + PORT + "/ping"
  const WS = "ws://127.0.0.1:" + PORT + "/"

  let socket = null

  function line(s) { console.log("[P05] " + s) }

  function testXHR() {
    return new Promise((resolve) => {
      try {
        const x = new XMLHttpRequest()
        x.open("GET", HTTP, true)
        x.timeout = 4000
        x.onload = () => resolve("XHR      ok    status=" + x.status + "  body=" + String(x.responseText).slice(0, 80))
        x.onerror = () => resolve("XHR      FAILED (onerror — blocked, refused, or no server)")
        x.ontimeout = () => resolve("XHR      TIMEOUT")
        x.send()
      } catch (e) { resolve("XHR      THREW: " + e) }
    })
  }

  function testFetch() {
    return new Promise((resolve) => {
      if (typeof fetch !== "function") { resolve("fetch    not available in this engine"); return }
      let settled = false
      const done = (s) => { if (!settled) { settled = true; resolve(s) } }
      setTimeout(() => done("fetch    TIMEOUT"), 4000)
      try {
        fetch(HTTP)
          .then((r) => r.text().then((t) => done("fetch    ok    status=" + r.status + "  body=" + t.slice(0, 80))))
          .catch((e) => done("fetch    FAILED: " + e))
      } catch (e) { done("fetch    THREW: " + e) }
    })
  }

  function testWS() {
    return new Promise((resolve) => {
      if (typeof WebSocket !== "function") { resolve("WS       WebSocket constructor does not exist in this engine"); return }
      let settled = false
      const done = (s) => { if (!settled) { settled = true; resolve(s) } }
      setTimeout(() => done("WS       TIMEOUT — no open, no error within 5s"), 5000)
      try {
        const ws = new WebSocket(WS)
        socket = ws
        ws.onopen = () => {
          ws.send(JSON.stringify({ probe: "p05", from: document.title, at: Date.now() }))
          done("WS       OPEN — a cockpit document can hold a WebSocket to loopback")
        }
        ws.onmessage = (e) => line("WS recv  " + String(e.data).slice(0, 200))
        ws.onerror = () => done("WS       ERROR (blocked, refused, or no server listening)")
        ws.onclose = (e) => {
          line("WS close code=" + e.code + " reason=" + (e.reason || "(none)") + " clean=" + e.wasClean)
          done("WS       CLOSED before opening — code=" + e.code)
        }
      } catch (e) { done("WS       THREW: " + e) }
    })
  }

  function run() {
    console.log("=========================================================")
    console.log("P05  loopback reachability from a cockpit document")
    console.log("=========================================================")
    console.log("document  " + document.title)
    console.log("origin    " + location.href)
    console.log("target    " + HTTP + "  and  " + WS)
    console.log("")
    console.log("Make sure `npm run probe:05-server` is running, or every test below")
    console.log("fails for the boring reason.")
    console.log("")

    Promise.all([testXHR(), testFetch(), testWS()]).then((results) => {
      results.forEach((r) => console.log("  " + r))
      console.log("")
      console.log("--- read this as ---")
      console.log("  WS OPEN                  -> Q08 yes. The 512-byte bus stops being a")
      console.log("     constraint; interaction moves to a direct channel.")
      console.log("  HTTP ok, WS blocked      -> loopback is allowed but sockets are not.")
      console.log("     Long-poll or chunked HTTP is a fallback, but latency makes tier 3")
      console.log("     doubtful. Widening the existing bus is then the better route.")
      console.log("  all blocked              -> the Fenix EFB reaches localhost only")
      console.log("     because it is an iframe with an http:// origin of its own; coui://")
      console.log("     documents do not get the same permission. Stay on the bus.")
      console.log("")
      console.log("  Check the server window too — it logs an UPGRADED line on success,")
      console.log("  which is proof independent of anything this side reports.")
    })
  }

  window.__P05 = {
    run: run,
    send: function (s) {
      if (!socket || socket.readyState !== 1) { console.warn("[P05] no open socket"); return }
      socket.send(typeof s === "string" ? s : JSON.stringify(s))
    },
    close: function () { if (socket) { socket.close(); socket = null } },
    socket: function () { return socket }
  }

  run()
})();
