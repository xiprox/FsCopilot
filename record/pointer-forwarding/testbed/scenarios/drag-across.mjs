/* A drag is one packet at mouse-up carrying the whole sampled path. The path
 * crosses as per-step deltas and is rebuilt into absolute times on the far side, so
 * a round trip is the only way to see that the two conversions agree. */

import { sameGesture, eq } from "../lib/expect.mjs"
import { diagonal } from "../lib/world.mjs"

export const about = "a drag crosses with its full path and timing intact"

export default async function (t) {
  const w = await t.world()

  const short = diagonal(12, 400)
  w.a.drag({ path: short, button: 0 })
  await w.b.waitForReplays(1)
  sameGesture(w.b.replays[0], { k: "drag", key: w.a.key, button: 0, path: short }, "12-point drag")

  // 240 is what capture bounds a gesture at, so it is the longest a panel will
  // ever send and the one worth proving.
  const full = diagonal(240, 8000)
  w.a.drag({ path: full, button: 0 })
  await w.b.waitForReplays(2)
  sameGesture(w.b.replays[1], { k: "drag", key: w.a.key, button: 0, path: full }, "240-point drag")

  eq(w.b.replays[1].path.length, 240, "path length at the cap")
}
