/* Assertions that say what they were looking at when they failed. A bench that
 * reports "expected true" has spent the run and told nobody anything. */

export class Failed extends Error {}

const show = (v) => (typeof v === "object" ? JSON.stringify(v) : String(v))

export function ok(cond, what) {
  if (!cond) throw new Failed(what)
}

export function eq(actual, expected, what) {
  if (actual !== expected) throw new Failed(`${what}: expected ${show(expected)}, got ${show(actual)}`)
}

export function near(actual, expected, tolerance, what) {
  if (Math.abs(actual - expected) > tolerance) {
    throw new Failed(`${what}: expected ${show(expected)} ±${tolerance}, got ${show(actual)}`)
  }
}

export function atLeast(actual, min, what) {
  if (!(actual >= min)) throw new Failed(`${what}: expected at least ${show(min)}, got ${show(actual)}`)
}

export function atMost(actual, max, what) {
  if (!(actual <= max)) throw new Failed(`${what}: expected at most ${show(max)}, got ${show(actual)}`)
}

/** A replayed gesture against the one that was captured. Coordinates cross the wire
 *  as float32, so they come back close rather than equal. */
export function sameGesture(replay, sent, what = "gesture") {
  eq(replay.k, sent.k, `${what}: kind`)
  eq(replay.key, sent.key, `${what}: key`)
  eq(replay.button, sent.button, `${what}: button`)
  if (sent.k === "press") {
    near(replay.nx, sent.nx, 1e-5, `${what}: nx`)
    near(replay.ny, sent.ny, 1e-5, `${what}: ny`)
    near(replay.ux, sent.ux, 1e-5, `${what}: ux`)
    near(replay.uy, sent.uy, 1e-5, `${what}: uy`)
    eq(replay.hold, sent.hold, `${what}: hold`)
  } else {
    eq(replay.path.length, sent.path.length, `${what}: path length`)
    for (let i = 0; i < sent.path.length; i++) {
      near(replay.path[i][0], sent.path[i][0], 1, `${what}: path[${i}] time`)
      near(replay.path[i][1], sent.path[i][1], 1e-5, `${what}: path[${i}] x`)
      near(replay.path[i][2], sent.path[i][2], 1e-5, `${what}: path[${i}] y`)
    }
  }
}
