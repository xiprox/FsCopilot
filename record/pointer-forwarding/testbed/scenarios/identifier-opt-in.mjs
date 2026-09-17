/*
 * A query string is not the profile author's to predict. The A220 declares its DisplayUnits
 * as ?config=[config], so the panel helloes as config=N324DU on that livery and the profile
 * key naming config=Default matched one livery and nothing else.
 *
 * So a profile entry without a '|' names the instrument identifier and takes every panel
 * with it. An entry with one still names a single panel, which is how an aircraft with two
 * CTPs opts in one of them.
 */

import { eq, sameGesture } from "../lib/expect.mjs"
import { sleep } from "../lib/app.mjs"

const LIVERY_KEY = "BenchDisplay|config=N324DU"

export const about = "an identifier entry opts in a panel whose key carries a query"

export default async function (t) {
  // Opted in by identifier alone; no entry anywhere names this panel's whole key.
  const w = await t.world({ keys: ["BenchDisplay"], panels: false, join: false })
  w.a = await w.panel(w.A, LIVERY_KEY)
  w.b = await w.panel(w.B, LIVERY_KEY)
  await w.join()

  const { sent, replay } = await w.pressAcross({ x: 0.25, y: 0.5, hold: 90 })
  sameGesture(replay, sent, "press across, keyed by livery")

  // An exact entry is still exact: naming another livery's key opts this panel out again,
  // which is what narrowing to one of two same-identifier panels relies on.
  const fromHere = w.b.received.length
  await w.A.configure(["BenchDisplay|config=Other"])
  await w.B.configure(["BenchDisplay|config=Other"])
  await w.b.waitFor((m) => m.t === "config", { from: fromHere, label: "config for the narrowed key" })
  const before = w.b.replays.length
  w.a.press({ x: 0.6, y: 0.2 })
  await sleep(2000)
  eq(w.b.replays.length, before, "replays after the panel was opted out")
}
