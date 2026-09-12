/*
 * Does a link that went away come back without the pilot doing anything?
 *
 * Coordinator treats "degraded -> live" as a recovery and re-sends history on it,
 * which assumes something re-establishes the link. This scenario asks whether
 * anything does. Its answer is the difference between held history being delivered
 * automatically and only being delivered when somebody presses Join again.
 */

import { eq } from "../lib/expect.mjs"

export const about = "whether a blackholed peer link re-establishes on its own"

export default async function (t) {
  const w = await t.world()
  await w.pressAcross({ x: 0.5, y: 0.5 })
  await w.settle()

  await w.B.suspend()
  t.log("B suspended")
  await w.a.waitForSession("degraded", { timeout: 45000 })
  eq(w.a.state.session, "degraded", "A's session while B is frozen")

  await w.B.resume()
  t.log("B resumed; waiting to see whether the link returns unaided")

  // Three minutes: long enough that "it retries slowly" is not the answer.
  await w.a.waitForSession("live", { timeout: 180000 })
  eq(w.a.state.session, "live", "A's session after B came back")
}
