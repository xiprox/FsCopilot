/*
 * The reason history exists.
 *
 * The pilot keeps pressing while the link is down. Everything after the last
 * acknowledgement is held, and on recovery it is re-sent in Seq order, so the
 * receiving panel ends where the sending one did instead of somewhere in between.
 *
 * The outage here is a suspended process: sockets open, nothing answering. A kill
 * would end the process and take the receiving side's dedupe state with it, which
 * is a different case (and the one that makes the ack floor rather than the dedupe
 * do the work).
 */

import { ok, eq, atLeast } from "../lib/expect.mjs"
import { sleep } from "../lib/app.mjs"

export const about = "presses made during an outage arrive, in order, on reconnect"

export default async function (t) {
  const w = await t.world()

  await w.pressAcross({ x: 0.1, y: 0.1 })
  // Let the ack timer run, so what follows is held because of the outage rather
  // than because nothing had been acknowledged yet.
  await w.settle()

  await w.B.suspend()
  t.log("B suspended")
  await w.a.waitForSession("degraded", { timeout: 45000 })

  const during = [
    { x: 0.21, y: 0.31 },
    { x: 0.22, y: 0.32 },
    { x: 0.23, y: 0.33 }
  ]
  for (const p of during) w.a.press({ ...p, hold: 50 })
  await sleep(500)

  await w.B.resume()
  t.log("B resumed")

  // The link does not come back by itself on the direct path - see
  // outage-and-recovery. Re-joining is what a pilot would do, and it is the
  // recovery this scenario is about.
  await w.B.join(w.A.peerId)
  await w.a.waitForSession("live", { timeout: 45000 })

  const replays = await w.b.waitForReplays(1 + during.length, { timeout: 30000 })
  const after = replays.slice(1)

  eq(after.length, during.length, "presses replayed after recovery")
  for (let i = 0; i < during.length; i++) {
    eq(Math.round(after[i].nx * 100) / 100, during[i].x, `replay ${i} x`)
    eq(Math.round(after[i].ny * 100) / 100, during[i].y, `replay ${i} y`)
  }

  const resent = w.A.logMatches(/Re-sent (\d+) unacknowledged events after reconnect/)
  atLeast(resent.length, 1, "A logged a resend")

  // Held events are re-sent whole, so the receiver sees some twice. It must apply
  // each once: that is what (Session, Seq) is for.
  eq(w.b.replays.length, 1 + during.length, "no gesture applied twice")
  ok(w.B.logMatches(/events from the peer never arrived/).length === 0,
    "B reported no gap")
}
