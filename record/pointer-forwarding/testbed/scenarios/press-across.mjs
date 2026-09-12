/* The base case. Everything else is this one plus something going wrong. */

import { sameGesture, eq } from "../lib/expect.mjs"

export const about = "a press on one machine replays on the other, unchanged"

export default async function (t) {
  const w = await t.world()

  const { sent, replay } = await w.pressAcross({ x: 0.31, y: 0.72, hold: 120, button: 2 })
  sameGesture(replay, sent, "press A to B")

  // Symmetric: the feature is not gated on master, and B is the joiner.
  const back = await w.pressAcross({ from: w.b, to: w.a, x: 0.8, y: 0.15 })
  sameGesture(back.replay, back.sent, "press B to A")

  // A capture must not come back to the panel that made it.
  eq(w.a.replays.length, 1, "replays seen by A")
  eq(w.b.replays.length, 1, "replays seen by B")
}
