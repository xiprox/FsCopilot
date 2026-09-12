/*
 * The other half of quit-says-goodbye. taskkill /F gets no exit path, so nothing
 * announces anything: no goodbye to the panel, no "left" payload to the peer. The
 * peer must wait this one out rather than end the session.
 */

import { ok, eq } from "../lib/expect.mjs"

export const about = "a killed instance leaves the peer degraded, not ended"

export default async function (t) {
  const w = await t.world()
  await w.pressAcross({ x: 0.6, y: 0.2 })

  await w.A.crash()

  ok(!w.a.saidBye, "panel A was not told goodbye")

  // LiteNetLib's DisconnectTimeout is 15 s, so this is the slowest transition the
  // fast scenarios wait for.
  await w.b.waitForSession("degraded", { timeout: 45000 })
  eq(w.b.state.session, "degraded", "B's session after A was killed")

  // Degraded is a lock on the slave, and B is the joiner.
  eq(w.b.state.role, "slave", "B's role")

  ok(w.B.logMatches(/Peer left; session over/).length === 0,
    "B did not treat a kill as a deliberate departure")
}
