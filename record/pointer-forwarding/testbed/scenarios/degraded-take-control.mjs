/*
 * The way out of a degraded lock.
 *
 * Q12 says a dropped peer link never re-establishes itself, so a pilot can sit
 * degraded until the five-minute timeout. What makes that survivable is that the
 * lock is not on both of them: pointer.js stands the amber overlay only while
 *
 *     fresh && session === 'degraded' && role === 'slave'
 *
 * and the card names the move - "Take control to keep flying". Take Control flips
 * the role, the Coordinator re-broadcasts the session state, and the overlay's own
 * condition stops holding.
 *
 * The overlay is DOM and a fake panel has none. What this settles is the state
 * that drives it: both inputs to that line, before and after. If the role does not
 * flip, no amount of clicking clears anything.
 */

import { eq } from "../lib/expect.mjs"

export const about = "taking control clears the degraded lock's condition on the slave"

export default async function (t) {
  const w = await t.world()

  // B joined, so B is the slave and B is the one who gets locked.
  eq(w.a.state.role, "master", "A's role while live")
  eq(w.b.state.role, "slave", "B's role while live")

  await w.settle()
  await w.A.suspend()
  t.log("A suspended")

  await w.b.waitForSession("degraded", { timeout: 45000 })
  eq(w.b.state.role, "slave", "B's role while degraded")
  // Both halves of the overlay's condition hold: this is the locked panel.

  await w.B.takeControl()

  await w.b.waitFor((m) => m.t === "state" && m.role === "master",
    { timeout: 15000, label: "role=master", from: w.b.received.length })

  eq(w.b.state.role, "master", "B's role after taking control")
  // The session is still degraded - taking control does not bring the peer back,
  // it stops this side waiting for one.
  eq(w.b.state.session, "degraded", "B's session after taking control")
}
