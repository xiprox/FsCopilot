/*
 * FSCPP link — the panel's side of the transport.
 *
 * A WebSocket from the cockpit document to a local process. Q08 established that
 * a coui:// document can hold one, full duplex, which is what makes this whole
 * shape possible: no WASM module, no SimConnect, no CommBus, and none of the
 * 512-byte single-slot constraints that come with them.
 *
 * Everything transport-shaped lives here rather than in agent.js — framing,
 * reconnection, identification, backpressure — so the agent stays portable to a
 * different transport if this one ever proves unshippable. (It might: Q08 was
 * demonstrated with the inspector running, and a normal session with DevMode off
 * is untested.)
 *
 * Chrome 49. No optional chaining, no ??, no class fields.
 *
 *   var link = FSCPP_Link(url, { name: key })
 *   link.onMessage(fn)      fn(obj) for each inbound message
 *   link.send(obj)
 *   link.state()            "connecting" | "open" | "closed"
 *   link.close()            stop, and stop reconnecting
 */

window.FSCPP_Link = function (url, opts) {
  opts = opts || {}

  var ws = null
  var closed = false
  var handlers = []
  var queue = []
  var attempt = 0
  var timer = null

  var stats = { sent: 0, received: 0, dropped: 0, reconnects: 0 }

  // A panel document is reloaded whenever the view changes, and the host may be
  // started after the simulator. Neither should need anyone to intervene, so the
  // socket reconnects on its own, backing off so a host that is simply absent
  // does not spin.
  var BACKOFF = [500, 1000, 2000, 4000, 8000, 15000]

  // Bounded, because an unbounded queue against an absent host is a slow leak in
  // a process nobody restarts. Interaction messages are worthless once stale, so
  // the oldest go first.
  var MAX_QUEUE = 200

  function log(s) { console.log("[FSCPP] link " + s) }

  function flush() {
    while (queue.length && ws && ws.readyState === 1) {
      ws.send(queue.shift())
    }
  }

  function connect() {
    if (closed) return
    try {
      ws = new WebSocket(url)
    } catch (e) {
      log("construct failed: " + e)
      schedule()
      return
    }

    ws.onopen = function () {
      attempt = 0
      log("open " + url)
      // Identify immediately: the host routes by panel key and cannot know which
      // document this is otherwise.
      try {
        ws.send(JSON.stringify({ t: "hello", name: opts.name || "unknown", url: location.href }))
      } catch (e) { /* the socket may already be closing */ }
      flush()
    }

    ws.onmessage = function (ev) {
      var msg
      try { msg = JSON.parse(String(ev.data)) } catch (e) { return }
      stats.received++
      for (var i = 0; i < handlers.length; i++) {
        try { handlers[i](msg) } catch (e) { console.error("[FSCPP] link handler threw", e) }
      }
    }

    ws.onerror = function () { /* onclose always follows; handle it there */ }

    ws.onclose = function () {
      ws = null
      if (closed) return
      schedule()
    }
  }

  function schedule() {
    if (closed || timer) return
    var wait = BACKOFF[attempt < BACKOFF.length ? attempt : BACKOFF.length - 1]
    attempt++
    if (attempt > 1) stats.reconnects++
    timer = setTimeout(function () { timer = null; connect() }, wait)
  }

  connect()

  return {
    onMessage: function (fn) { handlers.push(fn) },
    send: function (obj) {
      var text
      try { text = JSON.stringify(obj) } catch (e) { return false }
      stats.sent++
      if (ws && ws.readyState === 1) { ws.send(text); return true }
      queue.push(text)
      while (queue.length > MAX_QUEUE) { queue.shift(); stats.dropped++ }
      return false
    },
    state: function () {
      if (closed) return "closed"
      if (ws && ws.readyState === 1) return "open"
      return "connecting"
    },
    stats: function () {
      var s = JSON.parse(JSON.stringify(stats))
      s.queued = queue.length
      s.state = (ws && ws.readyState === 1) ? "open" : "connecting"
      return s
    },
    close: function () {
      closed = true
      if (timer) { clearTimeout(timer); timer = null }
      if (ws) { try { ws.close() } catch (e) { /* already gone */ } ws = null }
    }
  }
};
