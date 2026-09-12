/*
 * Panel documents reload on every view change, which makes reconnect the normal
 * case rather than the exceptional one. The app's routing is built from hellos, so
 * a panel that comes back has to say who it is again before anything can reach it.
 */

import { eq, ok } from "../lib/expect.mjs"

export const about = "a panel that reloads re-identifies and keeps receiving"

export default async function (t) {
  const w = await t.world()
  await w.pressAcross({ x: 0.2, y: 0.2 })

  await w.b.reload()
  ok(w.b.connects >= 2, "panel B reconnected")

  // Config and state follow every hello, so a reloaded panel learns its mode
  // without waiting for the next renewal.
  ok(w.b.config && w.b.config.includes(w.b.key), "config after reload")
  eq(w.b.state.sync, "live", "sync after reload")

  const { sent, replay } = await w.pressAcross({ x: 0.9, y: 0.1 })
  eq(replay.key, sent.key, "routing resumed after reload")

  // The capturing side reloading is the same question from the other end.
  await w.a.reload()
  const back = await w.pressAcross({ x: 0.35, y: 0.65 })
  eq(back.replay.key, back.sent.key, "capture resumed after the sender reloaded")
}
