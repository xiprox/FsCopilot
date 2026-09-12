/*
 * Pointer sync: capture and replay of cockpit pointer input by position.
 *
 * Nothing depends on how the display is built. Event at position x sent, event
 * at position x replayed, where x is a fraction of the instrument element's
 * bounding rect. This reaches displays the element-name scheme cannot:
 * React-over-SVG and canvas surfaces have nothing stable to name, but they
 * hit-test their own documents fine. It must never be enabled for WasmInstrument
 * panels: the sim owns their hit-testing and discards synthetic coordinates
 * while latching the press.
 *
 * Deliberately additive: it only ADDS listeners and dispatches events; it wraps
 * and replaces nothing, so it cannot disable an aircraft, and stop() removes
 * everything it added. The one thing it puts in the pilot's way - the lock
 * overlay - is a visible DOM node kept alive only by state renewals from the
 * app: silence removes it, window.fscUnlock() force-removes it.
 *
 * Replay is a queue. Gestures run one at a time, never compressed - a hold is
 * the gesture, not idle time between gestures - and the idle time the pilot
 * left before each one is carried on the wire (gap) and preserved up to a
 * second, so a panel that loads something after a click gets the time the
 * pilot gave it. While the queue is busy a blocking overlay keeps real input
 * off the panel, so the local pilot can neither race the replay nor diverge
 * from it unseen; a lone tap runs synchronously and shows nothing.
 *
 * Coherent GT is Chrome 49: MouseEvent only (PointerEvent does not exist), no
 * optional chaining, no ??, no class fields.
 */
class Pointer {
    constructor(instrument, key) {
        this._instrument = instrument;
        this.key = key;
        this._listeners = [];
        this._captureFns = [];
        // captured and replayed count gestures. echo is a synthetic event of our own
        // seen by capture - two per replayed press, down and up, so a clean run reads
        // exactly 2:1. missed is a replay with no target. Anything rejected for any
        // other reason gets its own counter, never a shared one, so a discrepancy
        // shows up in the reports instead of hiding inside them.
        this._stats = {captured: 0, replayed: 0, missed: 0, echo: 0, outside: 0, locked: 0, stalled: 0};
        this._pendingDown = null;
        this._lastGestureEnd = 0;   // capture side: when the previous gesture ended

        // The replay queue. One gesture in flight at a time; see _arm for the stall.
        this._queue = [];
        this._busy = false;
        this._pumping = false;
        this._deadman = null;
        this._lastReplayEnd = 0;    // replay side: when the previous gesture finished here

        this._overlay = new Overlay(() => this._rect());
        this._overlayMuted = false;
        this._debugHold = false;
        // The red warning speaks once per outage, then lingers out. Latched, because
        // the channel keeps retrying and closes every few seconds while the app is
        // down - without this, every retry would repaint it and the linger would
        // never win. Cleared by a state renewal: the app is back, so a later break
        // is a new outage and gets its own warning.
        this._warned = false;
        this._lostTimer = null;
        this._lastState = 0;
        this._syncState = 'none';
        this._role = 'master';

        this._listen(document, 'mousedown', (ev) => this._onDown(ev));
        this._listen(document, 'mousemove', (ev) => this._onMove(ev));
        this._listen(document, 'mouseup', (ev) => this._onUp(ev));

        // The app renews sync state every 2s; losing it means the app is gone.
        // A blocking overlay with nobody alive to lift it would violate fail-open,
        // so it degrades to the non-blocking warning - never to silence.
        this._watchdog = setInterval(() => {
            if (this._debugHold || this._overlayMuted) return;
            if (this._lastState && Date.now() - this._lastState > Pointer.STATE_DEADMAN_MS) {
                this._showLost();
            }
        }, 2000);

        // The emergency escape hatch, at a fixed global so it survives losing every
        // other handle. Mutes overlays until sync next goes live.
        window.fscUnlock = () => {
            this._overlayMuted = true;
            this._debugHold = false;
            this._clearLostTimer();
            this._overlay.remove();
        };

        // Debug: force an overlay state from the console and hold it against the 2s
        // state renewals until cleared. fscOverlay('connecting'|'degraded'|'replaying'|'lost'|'clear')
        window.fscOverlay = (state) => {
            if (state === 'clear') {
                this._debugHold = false;
                this._overlay.remove();
                return 'cleared - next state renewal restores truth';
            }
            this._debugHold = true;
            this._overlay.apply(this._overlay.has(state) ? state : 'connecting');
            return 'holding ' + state + ' - fscOverlay(\'clear\') to release';
        };

        // Locked from the start: entering pointer mode means the profile opted this
        // panel in, and until the app confirms the sync state the safe reading
        // is "connecting". The state that follows config or hello refines it
        // within milliseconds.
        this.updateState('connecting', 'master');

        const r = this._rect();
        console.log('[FsCopilot] [Pointer] Active on ' + this.key +
            ' rect=' + Math.round(r.width) + 'x' + Math.round(r.height));
    }

    onCapture(fn) { this._captureFns.push(fn); }

    stats() {
        const s = Object.assign({}, this._stats);
        const r = this._rect();
        s.rect = [Math.round(r.width), Math.round(r.height)];
        s.queued = this._queue.length;
        return s;
    }

    stop() {
        for (let i = 0; i < this._listeners.length; i++) {
            const l = this._listeners[i];
            l[0].removeEventListener(l[1], l[2], true);
        }
        this._listeners = [];
        this._captureFns = [];
        this._pendingDown = null;
        this._queue = [];
        this._busy = false;
        this._disarm();
        clearInterval(this._watchdog);
        this._clearLostTimer();
        this._overlay.remove();
    }

    /* Sync state -> overlay --------------------------------------------------- */

    updateState(sync, role) {
        this._syncState = sync;
        this._role = role;
        this._lastState = Date.now();
        // The app answered, so whatever outage there was is over: drop the linger and
        // re-arm the warning for the next one.
        this._warned = false;
        this._clearLostTimer();
        if (this._debugHold) return; // a forced debug overlay outranks real state
        if (sync === 'live' || sync === 'none') this._overlayMuted = false;
        this._refreshOverlay(false);
    }

    /* One place decides which overlay stands. Blocking only while the app is alive
     * and renewing the lock (a fresh state): both sides during connecting, the
     * slave while the peer link is degraded, and either side while a replay is
     * in progress - in that order, so a replay never hides a sync lock. The
     * red warning is not decided here: it is a notice with its own timer, and the
     * replay path must never touch it (the app is gone; the queue drains on its
     * own). A state renewal replaces it, because the app is back. */
    _refreshOverlay(fromReplay) {
        if (this._debugHold) return;
        if (this._overlayMuted) { this._overlay.remove(); return; }
        if (fromReplay && this._overlay.showing() === 'lost') return;

        const fresh = this._lastState > 0 && Date.now() - this._lastState <= Pointer.STATE_DEADMAN_MS;
        let want = null;
        if (fresh && this._syncState === 'connecting') want = 'connecting';
        else if (fresh && this._syncState === 'degraded' && this._role === 'slave') want = 'degraded';
        else if (fresh && (this._busy || this._queue.length)) want = 'replaying';

        if (want) this._overlay.apply(want);
        else this._overlay.remove();
    }

    /* The channel closed. Either way the app can no longer lift a lock, so a
     * blocking overlay must not stand - but the two ways it can close are not the
     * same event. A deliberate app shutdown announces itself first (channel.js
     * carries the flag through): that is not a fault and says nothing at all. An
     * unannounced close is a broken link and warns, briefly. */
    linkClosed(deliberate) {
        this._lastState = 0;
        if (this._overlayMuted || this._debugHold) return;
        if (deliberate) {
            this._clearLostTimer();
            this._overlay.remove();
            return;
        }
        this._showLost();
    }

    /* The red warning is a notice, not a state marker. It is non-blocking, but the
     * veil still sits over an instrument the pilot has to read, and once it has been
     * seen it has said everything it can: leaving it up for the rest of the flight
     * costs more than it tells. Show it once per outage, then get out of the way. */
    _showLost() {
        if (this._warned) return;
        this._warned = true;
        this._overlay.apply('lost');
        this._lostTimer = setTimeout(() => {
            this._lostTimer = null;
            // Only ever retracts its own warning: a state that arrived meanwhile
            // (the app came back mid-linger) outranks it.
            if (this._overlay.showing() === 'lost') this._overlay.remove();
        }, Pointer.LOST_LINGER_MS);
    }

    _clearLostTimer() {
        if (!this._lostTimer) return;
        clearTimeout(this._lostTimer);
        this._lostTimer = null;
    }

    /* Capture ----------------------------------------------------------------- */

    _rect() {
        try { return this._instrument.getBoundingClientRect(); }
        catch (e) { return {left: 0, top: 0, width: 0, height: 0}; }
    }

    _emit(msg) {
        this._stats.captured++;
        for (let i = 0; i < this._captureFns.length; i++) {
            try { this._captureFns[i](msg); } catch (e) { /* a bad consumer must not break capture */ }
        }
    }

    /* Two independent guards. selfEmit is set on everything FS Copilot dispatches;
     * isTrusted is the belt - real cockpit input is trusted (probed in the cockpit,
     * Q02) and nothing synthetic ever can be. Nothing else: a third guard keyed on
     * "a replay is in flight" was found swallowing real input the panel still acted
     * on, which is a divergence nobody can see. The replaying overlay closes that
     * window from the other side - while a gesture replays, a real click reaches
     * neither the panel nor the capture. */
    _isOurs(ev) {
        return ev.selfEmit === true || ev.isTrusted === false;
    }

    _normalise(ev) {
        const r = this._rect();
        if (!r.width || !r.height) return null;
        return {
            nx: Math.round(((ev.clientX - r.left) / r.width) * 1e4) / 1e4,
            ny: Math.round(((ev.clientY - r.top) / r.height) * 1e4) / 1e4
        };
    }

    /* The idle time before a gesture, capped: only idle longer than the cap is
     * shortened on replay, so an outage does not take its own length to replay. */
    _gapBefore(at) {
        if (!this._lastGestureEnd) return 0;
        const gap = at - this._lastGestureEnd;
        if (gap < 0) return 0;
        return gap > Pointer.GAP_MAX_MS ? Pointer.GAP_MAX_MS : gap;
    }

    /* Capture listens on document, so it sees the whole document; only a gesture
     * that began inside this instrument is forwarded. In a multi-instrument
     * document a click on a neighbour is on the element-name path and would
     * otherwise actuate twice on the other side. The test is the target, not the
     * point: a press on our own blocking overlay is counted apart (locked), and
     * only when there is no element to test does the normalised point stand in,
     * with a small tolerance for edge presses on the owner. A drag that starts
     * inside and leaves is still forwarded whole - this bounds the press, not the
     * path. */
    _inside(ev, n) {
        const t = ev.target;
        const lock = this._overlay.element();
        if (t && lock && (t === lock || (typeof lock.contains === 'function' && lock.contains(t)))) {
            this._stats.locked++;
            return false;
        }
        let inside;
        if (t && t.nodeType === 1 && typeof this._instrument.contains === 'function') {
            inside = this._instrument.contains(t);
        } else {
            const tol = Pointer.EDGE_TOLERANCE;
            inside = n.nx >= -tol && n.nx <= 1 + tol && n.ny >= -tol && n.ny <= 1 + tol;
        }
        if (!inside) this._stats.outside++;
        return inside;
    }

    _onDown(ev) {
        if (this._isOurs(ev)) { this._stats.echo++; return; }
        const n = this._normalise(ev);
        if (!n) return;
        if (!this._inside(ev, n)) return;
        this._pendingDown = {
            nx: n.nx, ny: n.ny,
            x: ev.clientX, y: ev.clientY,        // raw, for the pixel thresholds
            lastX: ev.clientX, lastY: ev.clientY,
            at: Date.now(), lastAt: 0,
            button: ev.button,
            travelled: 0,
            path: [[0, n.nx, n.ny]]
        };
    }

    /* Sampled only while a button is down. The listener sees every move MSFS
     * delivers, so the early return runs far more often than the sampling
     * below it. */
    _onMove(ev) {
        const d = this._pendingDown;
        if (!d) return;
        if (this._isOurs(ev)) return;

        const far = Math.abs(ev.clientX - d.x) + Math.abs(ev.clientY - d.y);
        if (far > d.travelled) d.travelled = far;

        const dt = Date.now() - d.at;
        if (dt - d.lastAt < Pointer.DRAG_SAMPLE_MS) return;
        if (Math.abs(ev.clientX - d.lastX) + Math.abs(ev.clientY - d.lastY) < Pointer.DRAG_MIN_STEP_PX) return;

        const n = this._normalise(ev);
        if (!n) return;
        d.lastX = ev.clientX;
        d.lastY = ev.clientY;
        d.lastAt = dt;
        if (d.path.length < Pointer.DRAG_MAX_POINTS) d.path.push([dt, n.nx, n.ny]);
    }

    _onUp(ev) {
        if (this._isOurs(ev)) { this._stats.echo++; return; }
        const n = this._normalise(ev);
        if (!n) return;

        // Hold duration is carried rather than a constant, so a press-and-hold
        // replays as one. Without a matching down, the up stands alone and must
        // pass the same test the down would have; then it is a nominal press.
        const now = Date.now();
        const d = this._pendingDown;
        if (!d && !this._inside(ev, n)) return;
        const held = d ? now - d.at : 0;
        const from = d || n;
        const button = typeof ev.button === 'number' ? ev.button : 0;
        const gap = this._gapBefore(d ? d.at : now);
        this._pendingDown = null;
        this._lastGestureEnd = now;

        // Far enough to be a drag, with somewhere to drag along. A flick faster
        // than the sample interval is still a press.
        if (d && d.travelled >= Pointer.DRAG_MIN_PX && d.path.length > 1) {
            const path = d.path.slice();
            path.push([now - d.at, n.nx, n.ny]);
            this._emit({v: 5, k: 'drag', key: this.key, button: button, gap: gap, path: path});
            return;
        }

        this._emit({
            v: 5,
            k: 'press',
            key: this.key,
            nx: from.nx,
            ny: from.ny,
            ux: n.nx,
            uy: n.ny,
            hold: held > 1500 ? 1500 : held,
            gap: gap,
            button: button
        });
    }

    _listen(target, type, fn) {
        // Capture phase on document: the panel's own handlers may stopPropagation,
        // and MSFS delivers to the deepest node, not to the instrument element.
        target.addEventListener(type, fn, true);
        this._listeners.push([target, type, fn]);
    }

    /* Replay ------------------------------------------------------------------ */

    _fire(target, type, x, y, buttons, button) {
        const ev = new MouseEvent(type, {
            bubbles: true,
            cancelable: true,
            composed: true,
            view: window,
            clientX: x,
            clientY: y,
            screenX: x,
            screenY: y,
            button: button || 0,
            buttons: buttons,
            detail: 1
        });
        ev.selfEmit = true;
        return target.dispatchEvent(ev);
    }

    replay(msg) {
        if (!msg) return false;
        if (msg.key && msg.key !== this.key) return false;
        if (msg.k !== 'press' && msg.k !== 'drag') return false;
        this._queue.push(msg);
        this._pump();
        return true;
    }

    /* Drains the queue one gesture at a time. Re-entrant-safe: a gesture that
     * finishes synchronously calls back into here from inside the loop, and that
     * call must simply return. */
    _pump() {
        if (this._pumping) return;
        this._pumping = true;
        try {
            while (!this._busy && this._queue.length) {
                const msg = this._queue.shift();
                const wait = this._remainingGap(msg);
                if (wait > 0) {
                    // A busy step, so the overlay stands across the wait.
                    this._busy = true;
                    const go = this._once(() => {
                        this._disarm();
                        this._busy = false;
                        this._run(msg);
                        this._pump();
                    });
                    this._arm(wait + Pointer.REPLAY_DEADMAN_MS, go);
                    setTimeout(go, wait);
                    break;
                }
                this._run(msg);
            }
        } finally {
            this._pumping = false;
        }
        this._refreshOverlay(true);
    }

    /* Only the gap not already elapsed is waited: a live gesture arrives after its
     * gap has passed on the sender and waits nothing, a resend burst arrives all at
     * once and waits the full capped gap - the pilot's pacing is the only honest
     * source of how long the panel needed between two inputs. */
    _remainingGap(msg) {
        if (!this._lastReplayEnd || !msg.gap) return 0;
        const wait = msg.gap - (Date.now() - this._lastReplayEnd);
        return wait > 0 ? wait : 0;
    }

    _run(msg) {
        this._busy = true;
        const done = this._once(() => {
            this._disarm();
            this._busy = false;
            this._lastReplayEnd = Date.now();
            this._pump();
        });
        const expect = msg.k === 'drag' ? this._replayDrag(msg, done) : this._replayPress(msg, done);
        if (expect < 0) { done(); return; }   // nothing to hit: nothing to wait for
        if (!this._busy) return;              // finished synchronously
        this._arm(expect + Pointer.REPLAY_DEADMAN_MS, done);
        this._refreshOverlay(true);
    }

    /* The deadman: a gesture whose timers never reach their end releases the queue
     * anyway and says so, rather than jamming every gesture behind it. */
    _arm(ms, release) {
        this._disarm();
        this._deadman = setTimeout(() => {
            this._deadman = null;
            console.warn('[FsCopilot] [Pointer] Replay did not complete - releasing the queue');
            this._stats.stalled++;
            release();
        }, ms);
    }

    _disarm() {
        if (!this._deadman) return;
        clearTimeout(this._deadman);
        this._deadman = null;
    }

    _once(fn) {
        let ran = false;
        return () => {
            if (ran) return;
            ran = true;
            fn();
        };
    }

    /* Returns how long the gesture will take, or -1 if it found nothing to press.
     * done is called exactly when the gesture has finished, timers included. */
    _replayPress(msg, done) {
        const r = this._rect();
        if (!r.width || !r.height) { this._stats.missed++; return -1; }

        const x = Math.round(r.left + msg.nx * r.width);
        const y = Math.round(r.top + msg.ny * r.height);
        const ux = msg.ux == null ? msg.nx : msg.ux;
        const uy = msg.uy == null ? msg.ny : msg.uy;
        const x2 = Math.round(r.left + ux * r.width);
        const y2 = Math.round(r.top + uy * r.height);

        const target = this._overlay.hitTest(x, y);
        if (!target) { this._stats.missed++; return -1; }

        // MouseEvent only: PointerEvent does not exist in this engine, so a
        // handler bound to onPointerDown is unreachable by any means.
        this._fire(target, 'mousedown', x, y, 1, msg.button);

        const finish = () => {
            const upTarget = this._overlay.hitTest(x2, y2) || target;
            this._fire(upTarget, 'mouseup', x2, y2, 0, msg.button);
            this._fire(upTarget, 'click', x2, y2, 0, msg.button);
            this._stats.replayed++;
            done();
        };

        // A held press keeps its duration - hold-to-reset and hold-for-secondary
        // read it, so shortening it replays a different action, not a faster one.
        // A tap goes through synchronously so the three events share one tick.
        if (msg.hold && msg.hold > 40) { setTimeout(finish, msg.hold); return msg.hold; }
        finish();
        return 0;
    }

    /* A drag replays as its recorded path with its original timing. Each move is
     * dispatched at whatever is under that point: there is no pointer capture to
     * imitate in this engine. Deliberately no click at the end: a browser fires
     * one only when down and up share a target, and a map pan that ended
     * elsewhere should not also register as a selection. */
    _replayDrag(msg, done) {
        const pts = msg.path;
        if (!pts || pts.length < 2) { this._stats.missed++; return -1; }

        const r = this._rect();
        if (!r.width || !r.height) { this._stats.missed++; return -1; }

        const px = (i) => ({
            x: Math.round(r.left + pts[i][1] * r.width),
            y: Math.round(r.top + pts[i][2] * r.height)
        });

        const first = px(0);
        const start = this._overlay.hitTest(first.x, first.y);
        if (!start) { this._stats.missed++; return -1; }

        this._fire(start, 'mousedown', first.x, first.y, 1, msg.button);

        // Scheduled against the gesture start rather than chained, so one slow
        // timeout cannot compound into drift. A pause the pilot made mid-drag is
        // preserved but bounded.
        let elapsed = 0;
        let prev = 0;
        for (let i = 1; i < pts.length; i++) {
            let step = pts[i][0] - prev;
            if (step < 0) step = 0;
            if (step > Pointer.DRAG_MAX_STEP_MS) step = Pointer.DRAG_MAX_STEP_MS;
            prev = pts[i][0];
            elapsed += step;

            ((index, when, isLast) => {
                setTimeout(() => {
                    const p = px(index);
                    const target = this._overlay.hitTest(p.x, p.y) || start;
                    if (isLast) {
                        this._fire(target, 'mouseup', p.x, p.y, 0, msg.button);
                        this._stats.replayed++;
                        done();
                    } else {
                        this._fire(target, 'mousemove', p.x, p.y, 1, msg.button);
                    }
                }, when);
            })(i, elapsed, i === pts.length - 1);
        }

        return elapsed;
    }
}

/* A press and a drag are told apart by travel in viewport pixels - a threshold in
 * normalised units would mean something different on every panel size. The path
 * is sampled, not recorded whole: MSFS delivers mousemove at a high rate and a
 * map pan does not need every one to look right on the other side. */
Pointer.DRAG_MIN_PX = 4;        // below this the gesture was a press
Pointer.DRAG_SAMPLE_MS = 33;    // ~30 Hz
Pointer.DRAG_MIN_STEP_PX = 2;   // ignore jitter between samples
Pointer.DRAG_MAX_POINTS = 240;  // bounds the message; ~8s of dragging
Pointer.DRAG_MAX_STEP_MS = 250; // a mid-drag pause replays as a bounded pause
Pointer.EDGE_TOLERANCE = 0.02;  // rect fraction allowed outside [0,1] when only the point can be tested
Pointer.GAP_MAX_MS = 1000;      // idle time between gestures is preserved up to this
Pointer.REPLAY_DEADMAN_MS = 3000; // past a gesture's expected end, release the queue
Pointer.STATE_DEADMAN_MS = 8000;
Pointer.LOST_LINGER_MS = 10000;  // how long the red warning stands before retracting itself
