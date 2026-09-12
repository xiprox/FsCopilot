/*
 * A panel document, without the simulator.
 *
 * It speaks exactly what channel.js speaks — hello on connect and on every
 * reconnect, {t:"pointer"} captures out, config/state/pointer/bye in — so the app
 * cannot tell one of these from a cockpit panel, and the bench gets two things the
 * cockpit cannot give: it knows precisely what it sent, and it can send what a real
 * panel never would (a key nobody configured, a 1500-point path, a gesture into a
 * panel that is about to disappear).
 *
 * The app broadcasts {t:"state"} every two seconds, so this is also where the bench
 * observes the sync state machine. There is no second channel for test state:
 * what the bench asserts on is what a pilot's panel would have been told.
 */

import { EventEmitter } from "node:events"

const PORTS = [9020, 9021, 9022, 9023, 9024]

const t0 = Date.now()
export const now = () => Date.now() - t0

export class Panel extends EventEmitter {
  /**
   * @param {object} o
   * @param {string} o.key      the panel key, as Channel.keyFor builds it
   * @param {number} [o.port]   a fixed port; omit to rotate PORTS like channel.js
   * @param {number[]} [o.rect] instrument size reported in the hello
   * @param {string} [o.label]  what this panel is called in the transcript
   */
  constructor({ key, port, rect = [1920, 1080], label }) {
    super()
    this.key = key
    this.label = label || key
    this.rect = rect
    this.ports = port === undefined ? PORTS.slice() : [port]

    /** Every message the app sent, in order: {at, msg}. */
    this.received = []
    /** Just the replays, which is what most assertions are about. */
    this.replays = []
    /** Last {t:"state"}: {sync, role}. */
    this.state = null
    /** Last {t:"config"} pointer list. */
    this.config = null
    /** Set when the app said goodbye, which is a quit rather than a fault. */
    this.saidBye = false
    /** What this panel sent, so a scenario can diff sent against replayed. */
    this.sent = []

    this.connects = 0
    this.closes = 0
    this._portIndex = 0
    this._ws = null
    this._closing = false
    this._reconnect = true
  }

  static async open(opts) {
    const p = new Panel(opts)
    await p.connect()
    return p
  }

  async connect() {
    this._closing = false
    await this._dial()
    // The app answers every hello with config and state. Waiting for the state
    // means "attached and told where we stand" rather than "TCP is up", and every
    // scenario that follows can assume it.
    await this.waitFor((m) => m.t === "state", { label: "first state" })
    return this
  }

  _dial() {
    return new Promise((resolve, reject) => {
      const port = this.ports[this._portIndex % this.ports.length]
      const ws = new WebSocket(`ws://127.0.0.1:${port}/`)
      this._ws = ws
      let settled = false

      const fail = (why) => {
        if (settled) return
        settled = true
        this._portIndex++
        if (this.ports.length === 1) return reject(new Error(`${this.label}: ${why} on ${port}`))
        // Rotating, as channel.js does. Give the whole range a chance before
        // calling it a failure.
        if (this._portIndex >= this.ports.length * 2) return reject(new Error(`${this.label}: no app on ${this.ports}`))
        this._dial().then(resolve, reject)
      }

      ws.onopen = () => {
        settled = true
        this.connects++
        this.port = port
        ws.send(JSON.stringify({ t: "hello", name: this.key, url: `coui://bench/${this.key}`, rect: this.rect }))
        resolve()
      }
      ws.onerror = () => fail("connect failed")
      ws.onclose = () => {
        this.closes++
        if (!settled) return fail("closed before open")
        this.emit("close")
        if (this._reconnect && !this._closing) setTimeout(() => this._dial().catch(() => {}), 250)
      }
      ws.onmessage = (ev) => this._onMessage(String(ev.data))
    })
  }

  _onMessage(text) {
    let msg
    try { msg = JSON.parse(text) } catch { return }
    const entry = { at: now(), msg }
    this.received.push(entry)
    if (msg.t === "state") this.state = { sync: msg.sync, role: msg.role }
    if (msg.t === "config") this.config = msg.pointer || []
    if (msg.t === "bye") this.saidBye = true
    if (msg.t === "pointer") this.replays.push({ at: entry.at, ...msg.msg })
    this.emit("message", msg)
  }

  _send(obj) {
    if (!this._ws || this._ws.readyState !== 1) throw new Error(`${this.label}: socket not open`)
    this._ws.send(JSON.stringify(obj))
  }

  /** A press, or a press-and-hold. Coordinates are rect fractions, as on the wire. */
  press({ x = 0.5, y = 0.5, ux, uy, hold = 40, gap = 0, button = 0 } = {}) {
    const msg = { v: 5, k: "press", key: this.key, nx: x, ny: y, ux: ux === undefined ? x : ux,
      uy: uy === undefined ? y : uy, hold, gap, button }
    this.sent.push({ at: now(), ...msg })
    this._send({ t: "pointer", msg })
    return msg
  }

  /** A drag. `path` is [[msFromStart, x, y], ...], which is what capture emits. */
  drag({ path, gap = 0, button = 0 } = {}) {
    const msg = { v: 5, k: "drag", key: this.key, button, gap, path }
    this.sent.push({ at: now(), ...msg })
    this._send({ t: "pointer", msg })
    return msg
  }

  /** Anything at all, including things a real panel would never send. */
  raw(obj) {
    this._send(obj)
  }

  stats() {
    this._send({ t: "stats", captured: this.sent.length, replayed: this.replays.length })
  }

  /** Resolves with the first message matching `pred`, counting ones already in. */
  waitFor(pred, { timeout = 5000, label = "message", from = 0 } = {}) {
    const hit = this.received.slice(from).find((e) => pred(e.msg))
    if (hit) return Promise.resolve(hit.msg)
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.off("message", on)
        reject(new Error(`${this.label}: timed out after ${timeout}ms waiting for ${label}`))
      }, timeout)
      const on = (msg) => {
        if (!pred(msg)) return
        clearTimeout(timer)
        this.off("message", on)
        resolve(msg)
      }
      this.on("message", on)
    })
  }

  /*
   * Waits for a sync state. The app renews state every 2 s, so this settles on
   * the next renewal at worst rather than only on a transition.
   *
   * Only the current state and what comes after it, never the backlog: a panel's
   * first state is "none", so a search from the start matches that one and a wait
   * for sync to *end* returns immediately, before anything has happened. It
   * cost three scenarios that reported the state they were about to be in anyway.
   */
  waitForSync(sync, opts = {}) {
    if (this.state && this.state.sync === sync) return Promise.resolve(this.state)
    return this.waitFor((m) => m.t === "state" && m.sync === sync,
      { timeout: 20000, label: `sync=${sync}`, ...opts, from: this.received.length })
      .then(() => this.state)
  }

  /** Waits for `n` replays to have arrived. */
  async waitForReplays(n, { timeout = 10000 } = {}) {
    const deadline = Date.now() + timeout
    while (this.replays.length < n) {
      if (Date.now() > deadline) {
        throw new Error(`${this.label}: ${this.replays.length} of ${n} replays after ${timeout}ms`)
      }
      await new Promise((r) => setTimeout(r, 25))
    }
    return this.replays
  }

  /** Closes without reconnecting: the panel is gone, not reloading. */
  close() {
    this._reconnect = false
    this._closing = true
    try { this._ws && this._ws.close() } catch { /* going either way */ }
  }

  /** What a view change does: the document drops and comes back, hello and all. */
  async reload() {
    const before = this.connects
    try { this._ws && this._ws.close() } catch { /* going either way */ }
    const deadline = Date.now() + 5000
    while (this.connects === before) {
      if (Date.now() > deadline) throw new Error(`${this.label}: did not reconnect`)
      await new Promise((r) => setTimeout(r, 25))
    }
    await this.waitFor((m) => m.t === "state", { label: "state after reload", from: this.received.length - 4 })
  }
}
