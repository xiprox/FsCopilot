/*
 * The filter runs on both ends. Outbound it is the opt-in; inbound it is a defence
 * against a peer whose profile says something different — an older profile, a
 * hand-edited one, or one downloaded between the two pilots starting up.
 */

import { eq } from "../lib/expect.mjs"
import { KEY } from "../lib/world.mjs"
import { sleep } from "../lib/app.mjs"

export const about = "a peer that did not opt the panel in applies nothing it is sent"

export default async function (t) {
  const w = await t.world({ keys: { A: [KEY], B: [] } })

  eq(w.b.config.length, 0, "B's panel was told it is not in pointer mode")

  w.a.press({ x: 0.5, y: 0.5 })
  await sleep(3000)
  eq(w.b.replays.length, 0, "replays applied by a peer without the key")

  // And once B's profile agrees, the same press crosses.
  await w.B.configure([KEY])
  const { sent, replay } = await w.pressAcross({ x: 0.6, y: 0.4 })
  eq(replay.key, sent.key, "press after B opted in")
}
