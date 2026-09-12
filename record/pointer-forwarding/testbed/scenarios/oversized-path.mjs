/*
 * Capture bounds a gesture at 240 points and the codec bounds it again at 1024, so
 * a panel sending 1500 is something no real one does. It is worth sending anyway:
 * the interesting question is not whether the extra points survive but whether the
 * link does.
 *
 * Decode rejects a count past 1024 rather than allocating for it. That branch
 * cannot be reached from this side — the encoder truncates to 1024 before writing —
 * so what this settles is the truncation, not the rejection. The rejection is a
 * defence against a corrupt or hostile peer and needs a hand-built packet to test.
 */

import { eq } from "../lib/expect.mjs"
import { diagonal } from "../lib/world.mjs"

export const about = "an over-long drag path is truncated, and the session survives it"

export default async function (t) {
  const w = await t.world()

  w.a.drag({ path: diagonal(1500, 20000) })
  const replays = await w.b.waitForReplays(1, { timeout: 20000 })
  eq(replays[0].k, "drag", "kind")
  eq(replays[0].path.length, 1024, "path truncated to the codec's cap")

  // The link is the thing being tested. A press after it proves nothing was wedged.
  const { sent, replay } = await w.pressAcross({ x: 0.25, y: 0.75 })
  eq(replay.key, sent.key, "the next gesture still crosses")
  eq(w.b.replays.length, 2, "replays in total")
}
