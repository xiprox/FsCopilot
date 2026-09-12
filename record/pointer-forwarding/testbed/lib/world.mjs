/*
 * Two instances, a panel on each, joined into a session. What a scenario starts from
 * unless it says otherwise.
 *
 * Every scenario gets its own pair of processes. Sharing them would be faster and
 * would also mean a scenario that crashes an instance hands the next one a world it
 * did not ask for, and the whole point here is testing what crashes leave behind.
 * The rendezvous is shared, because nothing under test can damage it.
 *
 * Ids are readable rather than random (BNCHA001, BNCHB001) so a log line names a
 * side, and numbered rather than fixed because the rendezvous holds a registration
 * for a while after the process behind it dies: a second world reusing the first
 * world's ids is rejected with PEER_ID_TAKEN, and every scenario after the first
 * fails at the join.
 */

import { rmSync } from "node:fs"
import { join } from "node:path"

import { App, sleep } from "./app.mjs"
import { Panel } from "./panel.mjs"

/** The panel key both sides opt in. Shaped like a real one: identifier|querystring. */
export const KEY = "BenchDisplay|index=1"
/** A second key, for routing questions that need two panels that are not the same panel. */
export const KEY2 = "BenchDisplay|index=2"

// A scenario that builds a second world must not hand it the first world's control
// ports either. The panel ports need no such care: PanelServer scans for a free one.
let worldNo = 0
let benchPort = 9520

export class World {
  constructor(ctx) {
    this.ctx = ctx
    this.apps = []
    this.panels = []
  }

  /**
   * @param {object} o
   * @param {boolean} [o.join]    join B to A and wait for both to go live
   * @param {string[]} [o.keys]   the pointer opt-in list on both instances
   * @param {boolean} [o.panels]  open a panel on each instance
   */
  async setup({ join: doJoin = true, keys = [KEY, KEY2], panels = true } = {}) {
    const w = this
    const n = String(++worldNo).padStart(3, "0")

    w.A = await w.app(`A${n}`, `BNCHA${n}`, benchPort++)
    w.B = await w.app(`B${n}`, `BNCHB${n}`, benchPort++)

    // An array configures both sides the same. {A: [...], B: [...]} gives them
    // different profiles, which is what the inbound filter defends against.
    const forA = Array.isArray(keys) ? keys : keys.A || []
    const forB = Array.isArray(keys) ? keys : keys.B || []
    await w.A.configure(forA)
    await w.B.configure(forB)

    if (panels) {
      w.a = await w.panel(w.A, KEY)
      w.b = await w.panel(w.B, KEY)
    }

    if (doJoin) await w.join()
    return w
  }

  /* Registered before it is started, not after: an instance that fails on the way up
   * still has a process holding a port, and a world that threw halfway through used
   * to leave both of them running. Five leaked instances take the whole 9020-9024
   * range and every scenario after that fails to bind. */
  async app(name, peerId, benchPort) {
    const dir = join(this.ctx.runDir, name)
    rmSync(dir, { recursive: true, force: true })
    const app = new App({ name, peerId, benchPort, dir, exe: this.ctx.exe, relay: this.ctx.relayHost })
    this.apps.push(app)
    await app.launch()
    return app
  }

  async panel(app, key, opts = {}) {
    const p = new Panel({ key, port: app.port, label: `${app.name}:${key}`, ...opts })
    this.panels.push(p)
    await p.connect()
    return p
  }

  /** B joins A, and both sides see sync go live in their panels. */
  async join() {
    await this.B.join(this.A.peerId)
    await Promise.all([
      this.a ? this.a.waitForSync("live") : null,
      this.b ? this.b.waitForSync("live") : null
    ])
    // The ack timer is what clears send history, and it only runs while live. A
    // scenario that wants "acknowledged" rather than "delivered" waits for this.
    return this
  }

  /** Waits out one ack interval plus slack, so both sides have acked what they hold. */
  settle() { return sleep(4000) }

  /** A presses, B replays it. The base case every outage scenario is measured against. */
  async pressAcross({ from = this.a, to = this.b, ...opts } = {}) {
    const before = to.replays.length
    const sent = from.press(opts)
    await to.waitForReplays(before + 1)
    return { sent, replay: to.replays[before] }
  }

  async dispose() {
    for (const p of this.panels) { try { p.close() } catch { /* going anyway */ } }
    for (const a of this.apps) await a.dispose()
  }
}

/** A drag path of `n` points over `ms`, along the diagonal. */
export function diagonal(n, ms = 500) {
  const path = []
  for (let i = 0; i < n; i++) {
    const f = n === 1 ? 0 : i / (n - 1)
    path.push([Math.round(f * ms), 0.2 + f * 0.6, 0.3 + f * 0.4])
  }
  return path
}
