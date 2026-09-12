/*
 * Leave is the one departure with no outage behind it. Nothing needs bridging, so
 * held history is dropped and the panels come out of the lock rather than sitting
 * blue waiting for somebody who is not coming back.
 */

import { eq, ok } from "../lib/expect.mjs"

export const about = "leaving ends the session at once and unlocks panels"

export default async function (t) {
  const w = await t.world()
  await w.pressAcross({ x: 0.45, y: 0.55 })
  await w.settle()

  await w.B.leave()

  // The leaver ends its own session directly rather than waiting for a transport
  // event, so this is immediate on B and takes a disconnect on A.
  await w.b.waitForSync("none", { timeout: 15000 })
  eq(w.b.state.sync, "none", "B's sync after leaving")

  await w.a.waitForSync("none", { timeout: 45000 })
  await w.A.waitForLog(/\[Pointer\] Peer left; sync ended/)

  ok(w.A.logMatches(/Re-sent \d+ unacknowledged events/).length === 0,
    "nothing was re-sent to a peer that left")
}
