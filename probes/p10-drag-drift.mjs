/*
 * p10 — how much does replay compress a drag's timing? (Q10)
 *
 * Replayed drags drift. This measures one candidate cause without any simulator
 * time: `replayDrag` rebuilds timing by summing per-step intervals clamped to
 * DRAG_MAX_STEP_MS, so replay is shorter than capture whenever the pilot's
 * samples were sparse. Sparse samples are routine, because DRAG_MIN_STEP_PX
 * gates out sub-2px motion.
 *
 * Reads the drags already in recordings/ and replays the exact loop from
 * agent.js over them. Node only — no sim, no panel, no cockpit.
 *
 *   npm run drift
 *
 * Spatial note: the path coordinates themselves are replayed verbatim, so a
 * replayed drag's endpoint is exact. Timing compression only becomes spatial if
 * the receiving display does velocity, easing or inertia work on the path.
 *
 * Node probes take no dependencies. Node 22 is what is installed.
 */

import { readFileSync, readdirSync } from "node:fs"
import { join } from "node:path"

const DIR = "recordings"
const DRAG_MAX_STEP_MS = 250

let n = 0
for (const f of readdirSync(DIR).filter((x) => x.endsWith(".ndjson"))) {
  for (const line of readFileSync(join(DIR, f), "utf8").split("\n")) {
    if (!line.trim()) continue
    let rec
    try { rec = JSON.parse(line) } catch { continue }
    const msg = rec.msg || rec
    if (!msg || msg.k !== "drag" || !msg.path) continue

    const pts = msg.path
    const captured = pts[pts.length - 1][0]

    // Exactly the loop in replayDrag.
    let elapsed = 0, prev = 0, clamped = 0, maxGap = 0
    for (let i = 1; i < pts.length; i++) {
      let step = pts[i][0] - prev
      if (step > maxGap) maxGap = step
      if (step < 0) step = 0
      if (step > DRAG_MAX_STEP_MS) { step = DRAG_MAX_STEP_MS; clamped++ }
      prev = pts[i][0]
      elapsed += step
    }

    // Spatial extent, to see how far the gesture actually travelled.
    let span = 0
    for (let i = 1; i < pts.length; i++) {
      span += Math.abs(pts[i][1] - pts[i - 1][1]) + Math.abs(pts[i][2] - pts[i - 1][2])
    }

    n++
    const lost = captured - elapsed
    console.log(
      `#${String(n).padStart(2)}  pts=${String(pts.length).padStart(3)}` +
      `  captured=${String(captured).padStart(5)}ms  replay=${String(elapsed).padStart(5)}ms` +
      `  lost=${String(lost).padStart(5)}ms ${lost > 0 ? `(${Math.round((lost / captured) * 100)}%)` : ""}` +
      `  gaps>250ms=${clamped}  maxgap=${maxGap}ms  span=${span.toFixed(3)}`)
  }
}
console.log(`\n${n} drags examined`)
