/*
 * A quit and a crash look identical on a dropped socket, and the two must not be
 * treated the same: one warns the pilot that sync broke, the other broke nothing.
 * This is the deliberate half. crash-is-an-outage is the other.
 */

import { ok, eq } from "../lib/expect.mjs"

export const about = "quitting tells panels it was deliberate, and ends the peer's session"

export default async function (t) {
  const w = await t.world()
  await w.pressAcross({ x: 0.4, y: 0.4 })
  // Settled first: a link a few hundred milliseconds old is not the case the
  // pilot is in, and it leaves "the departure raced the handshake" open.
  await w.settle()

  await w.A.quit()

  // The panel on the quitting side is told before the socket goes.
  ok(w.a.saidBye, "panel A was told goodbye")

  // The peer is told too, in the disconnect itself. "Left", not "lost": no outage
  // to bridge, so the session ends rather than degrading.
  await w.b.waitForSession("none", { timeout: 30000 })
  await w.B.waitForLog(/\[Pointer\] Peer left; session over/)

  eq(w.b.state.session, "none", "B's session after A left")
}
