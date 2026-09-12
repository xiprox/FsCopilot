/*
 * GapMs is the pilot's own pacing, carried so the receiving panel can reproduce it:
 * a panel that loads a page after a click needs the time it was given, and a resend
 * burst arrives all at once, so arrival time tells the panel nothing.
 *
 * What the bench can settle is that the number survives the trip, including the 1 s
 * cap. The pacing itself is done by the replay queue in pointer.js, which a fake
 * panel does not have — reimplementing it here would test the reimplementation.
 * That one stays a cockpit question.
 */

import { eq } from "../lib/expect.mjs"

export const about = "gap survives the wire, capped at a second"

export default async function (t) {
  const w = await t.world()

  const gaps = [0, 250, 1000, 4000]
  for (const gap of gaps) w.a.press({ x: 0.5, y: 0.5, gap, hold: 30 })
  const replays = await w.b.waitForReplays(gaps.length)

  eq(replays[0].gap, 0, "no gap")
  eq(replays[1].gap, 250, "a quarter second")
  eq(replays[2].gap, 1000, "at the cap")
  eq(replays[3].gap, 1000, "past the cap, clamped")

  // A drag carries it too, and drags are where a long pause before the gesture is
  // most likely.
  w.a.drag({ path: [[0, 0.2, 0.2], [100, 0.5, 0.5], [200, 0.8, 0.8]], gap: 600 })
  const all = await w.b.waitForReplays(gaps.length + 1)
  eq(all[gaps.length].gap, 600, "gap on a drag")
}
