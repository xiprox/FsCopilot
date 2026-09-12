/*
 * An outage that outlives DegradedTimeout stops being an outage. Held history goes,
 * the slave unlocks, and the pilots are told in the log that the panels may be
 * out of step — which is honest, because five minutes of one pilot pressing things
 * cannot be reconstructed from a history that was thrown away.
 *
 * Slow by construction: the timeout is five minutes and nothing here shortens it.
 * Making it configurable would mean a timing knob in shipping code that exists only
 * for the bench, so instead this runs on demand.
 */

import { eq, atLeast } from "../lib/expect.mjs"

export const about = "an outage past the timeout ends the session and unlocks the slave"
export const slow = true

export default async function (t) {
  const w = await t.world()
  await w.pressAcross({ x: 0.5, y: 0.5 })
  await w.settle()

  await w.B.suspend()
  t.log("B suspended; waiting out the five-minute timeout")
  await w.a.waitForSession("degraded", { timeout: 45000 })

  // Presses during the outage are held, and are what the timeout discards.
  for (let i = 0; i < 5; i++) w.a.press({ x: 0.3 + i / 100, y: 0.3 })

  await w.a.waitForSession("none", { timeout: 7 * 60 * 1000 })
  await w.A.waitForLog(/Peer did not return within .*panels may be desynced/)
  eq(w.a.state.session, "none", "A's session after the timeout")

  await w.B.resume()
  // Rejoining after the session ended is a fresh session, not a recovery: the
  // history that would have been re-sent is gone.
  await w.B.join(w.A.peerId)
  await w.a.waitForSession("live", { timeout: 45000 })

  const resent = w.A.logMatches(/Re-sent (\d+) unacknowledged events/)
  eq(resent.length, 0, "nothing re-sent after the session had ended")
  atLeast(w.b.replays.length, 1, "the pre-outage press is still the only one B applied")
  eq(w.b.replays.length, 1, "presses made during the discarded outage did not arrive")
}
