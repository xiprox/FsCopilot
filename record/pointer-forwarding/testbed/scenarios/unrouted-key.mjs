/*
 * Two different drops, at two different places, for two different reasons.
 *
 * A key nobody opted in is dropped by the sender's filter and never reaches the
 * wire. A key that is opted in but has no panel on the far side reaches the peer
 * and is dropped there, counted rather than held: a document that helloes later was
 * reloaded by the sim and starts from its default state, so replaying "next page"
 * three times into it is three random inputs.
 */

import { eq, atLeast } from "../lib/expect.mjs"
import { KEY, KEY2 } from "../lib/world.mjs"
import { sleep } from "../lib/app.mjs"

export const about = "an unconfigured key never leaves; a configured one with no panel is dropped and counted"

export default async function (t) {
  const w = await t.world({ keys: [KEY, KEY2] })

  // KEY2 is configured on both, but only KEY has a panel anywhere.
  w.a.raw({ t: "pointer", msg: { v: 5, k: "press", key: KEY2, nx: 0.5, ny: 0.5, ux: 0.5, uy: 0.5, hold: 40, gap: 0, button: 0 } })
  await w.B.waitForLog(new RegExp(`No panel for ${KEY2.replace("|", "\\|")}; event dropped`), { timeout: 15000 })

  // A key in no profile is dropped before the wire, so the peer never hears of it.
  const before = w.B.lines.length
  w.a.raw({ t: "pointer", msg: { v: 5, k: "press", key: "NotInAnyProfile", nx: 0.5, ny: 0.5, ux: 0.5, uy: 0.5, hold: 40, gap: 0, button: 0 } })
  await sleep(2000)
  eq(w.B.lines.slice(before).filter((l) => /NotInAnyProfile/.test(l.line)).length, 0,
    "lines about an unconfigured key on the peer")

  // The panel that does exist is unaffected by either.
  const { sent, replay } = await w.pressAcross({ x: 0.7, y: 0.7 })
  eq(replay.key, sent.key, "the configured panel still receives")
  atLeast(w.B.logMatches(/undelivered so far/).length, 1, "the undelivered counter moved")
}
