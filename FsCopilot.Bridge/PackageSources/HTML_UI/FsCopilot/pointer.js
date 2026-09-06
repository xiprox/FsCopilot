/*
 * Pointer sync: capture and replay of cockpit pointer input by position.
 *
 * Forwards WHERE the pointer went - fractions of the instrument element's bounding
 * rect - and lets the receiving panel's own DOM hit-testing decide what got
 * pressed. This reaches displays the element-name scheme cannot: React-over-SVG
 * and canvas surfaces have nothing stable to name, but they hit-test their own
 * documents fine. It must never be enabled for WasmInstrument panels - the sim
 * owns their hit-testing and discards synthetic coordinates while latching the
 * press.
 *
 * Deliberately additive: it only ADDS listeners and dispatches events; it wraps
 * and replaces nothing, so it cannot disable an aircraft, and stop() removes
 * everything it added. The one thing it puts in the pilot's way - the lock
 * overlay - is a visible DOM node kept alive only by state renewals from the
 * app: silence removes it, window.fscUnlock() force-removes it.
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
        this._stats = {captured: 0, replayed: 0, missed: 0, ignored: 0};

        // Non-zero while replaying, so a synthetic event that somehow loses its
        // selfEmit flag still cannot be captured and echoed back. A counter rather
        // than a flag: a drag replay spans many timeouts and a second message can
        // arrive inside it - a boolean would be cleared by whichever finished
        // first, reopening the echo path mid-drag.
        this._replaying = 0;
        this._pendingDown = null;

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
        this._session = 'none';
        this._role = 'master';

        this._listen(document, 'mousedown', (ev) => this._onDown(ev));
        this._listen(document, 'mousemove', (ev) => this._onMove(ev));
        this._listen(document, 'mouseup', (ev) => this._onUp(ev));

        // The app renews session state every 2s; losing it means the app is gone.
        // A blocking overlay with nobody alive to lift it would violate fail-open,
        // so it degrades to the non-blocking warning - never to silence.
        this._watchdog = setInterval(() => {
            if (this._debugHold || this._overlayMuted) return;
            if (this._lastState && Date.now() - this._lastState > Pointer.STATE_DEADMAN_MS) {
                this._showLost();
            }
        }, 2000);

        // The emergency escape hatch, at a fixed global so it survives losing every
        // other handle. Mutes overlays until the session next goes live.
        window.fscUnlock = () => {
            this._overlayMuted = true;
            this._debugHold = false;
            this._clearLostTimer();
            this._overlay.remove();
        };

        // Debug: force an overlay state from the console and hold it against the 2s
        // state renewals until cleared. fscOverlay('connecting'|'degraded'|'lost'|'clear')
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
        // panel in, and until the app confirms the session state the safe reading
        // is "connecting". The state reply that follows the hello refines it
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
        return s;
    }

    stop() {
        for (let i = 0; i < this._listeners.length; i++) {
            const l = this._listeners[i];
            l[0].removeEventListener(l[1], l[2], true);
        }
        this._listeners = [];
        this._captureFns = [];
        this._replaying = 0;
        this._pendingDown = null;
        clearInterval(this._watchdog);
        this._clearLostTimer();
        this._overlay.remove();
    }

    /* Session state -> overlay ------------------------------------------------ */

    updateState(session, role) {
        this._session = session;
        this._role = role;
        this._lastState = Date.now();
        // The app answered, so whatever outage there was is over: drop the linger and
        // re-arm the warning for the next one.
        this._warned = false;
        this._clearLostTimer();
        if (this._debugHold) return; // a forced debug overlay outranks real state
        if (session === 'live' || session === 'none') this._overlayMuted = false;

        if (this._overlayMuted) { this._overlay.remove(); return; }

        // Blocking only while the app is alive and renewing the lock: both sides
        // during connecting, the slave while the peer link is degraded - the
        // master must keep flying. Everything else is clear.
        if (session === 'connecting') this._overlay.apply('connecting');
        else if (session === 'degraded' && role === 'slave') this._overlay.apply('degraded');
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

    /* Two independent guards. selfEmit is set on everything FS Copilot
     * dispatches; isTrusted is the belt - real cockpit input is trusted and
     * nothing synthetic ever can be. */
    _isOurs(ev) {
        return ev.selfEmit === true || ev.isTrusted === false || this._replaying > 0;
    }

    _normalise(ev) {
        const r = this._rect();
        if (!r.width || !r.height) return null;
        return {
            nx: Math.round(((ev.clientX - r.left) / r.width) * 1e4) / 1e4,
            ny: Math.round(((ev.clientY - r.top) / r.height) * 1e4) / 1e4
        };
    }

    _onDown(ev) {
        if (this._isOurs(ev)) { this._stats.ignored++; return; }
        const n = this._normalise(ev);
        if (!n) return;
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

    /* Sampled only while a button is down. The listener still fires on every
     * move MSFS delivers - and it delivers a great many - so the early return
     * matters more than it looks. */
    _onMove(ev) {
        const d = this._pendingDown;
        if (!d) return;
        if (ev.selfEmit === true || ev.isTrusted === false || this._replaying > 0) return;

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
        if (this._isOurs(ev)) { this._stats.ignored++; return; }
        const n = this._normalise(ev);
        if (!n) return;

        // Hold duration is carried rather than a constant, so a press-and-hold
        // replays as one. Without a matching down - the press began outside the
        // panel - fall back to a nominal press.
        const d = this._pendingDown;
        const held = d ? Date.now() - d.at : 0;
        const from = d || n;
        const button = typeof ev.button === 'number' ? ev.button : 0;
        this._pendingDown = null;

        // Far enough to be a drag, with somewhere to drag along. A flick faster
        // than the sample interval is still a press, which is the right reading.
        if (d && d.travelled >= Pointer.DRAG_MIN_PX && d.path.length > 1) {
            const path = d.path.slice();
            path.push([Date.now() - d.at, n.nx, n.ny]);
            this._emit({v: 5, k: 'drag', key: this.key, button: button, path: path});
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
        if (msg.k === 'drag') return this._replayDrag(msg);
        if (msg.k !== 'press') return false;

        const r = this._rect();
        if (!r.width || !r.height) { this._stats.missed++; return false; }

        const x = Math.round(r.left + msg.nx * r.width);
        const y = Math.round(r.top + msg.ny * r.height);
        const ux = msg.ux == null ? msg.nx : msg.ux;
        const uy = msg.uy == null ? msg.ny : msg.uy;
        const x2 = Math.round(r.left + ux * r.width);
        const y2 = Math.round(r.top + uy * r.height);

        const target = document.elementFromPoint(x, y);
        if (!target) { this._stats.missed++; return false; }

        // MouseEvent only: PointerEvent does not exist in this engine, so a
        // handler bound to onPointerDown is unreachable by any means.
        this._replaying++;
        this._fire(target, 'mousedown', x, y, 1, msg.button);

        const finish = () => {
            const upTarget = document.elementFromPoint(x2, y2) || target;
            this._fire(upTarget, 'mouseup', x2, y2, 0, msg.button);
            this._fire(upTarget, 'click', x2, y2, 0, msg.button);
            this._replaying--;
            this._stats.replayed++;
        };

        // A held press keeps its duration; a tap goes through synchronously so
        // the three events share one tick.
        if (msg.hold && msg.hold > 40) setTimeout(finish, msg.hold);
        else finish();

        return true;
    }

    /* A drag replays as its recorded path with its original timing. Each move is
     * dispatched at whatever is under that point - what a real mouse does; there
     * is no pointer capture to imitate in this engine. Deliberately no click at
     * the end: a browser fires one only when down and up share a target, and a
     * map pan that ended elsewhere should not also register as a selection. */
    _replayDrag(msg) {
        const pts = msg.path;
        if (!pts || pts.length < 2) { this._stats.missed++; return false; }

        const r = this._rect();
        if (!r.width || !r.height) { this._stats.missed++; return false; }

        const px = (i) => ({
            x: Math.round(r.left + pts[i][1] * r.width),
            y: Math.round(r.top + pts[i][2] * r.height)
        });

        const first = px(0);
        const start = document.elementFromPoint(first.x, first.y);
        if (!start) { this._stats.missed++; return false; }

        this._replaying++;
        this._fire(start, 'mousedown', first.x, first.y, 1, msg.button);

        // _replaying suppresses capture while above zero, so a drag whose final
        // timeout never runs would silently disable capture for the session.
        // Release exactly once, and guarantee it even if the path never completes.
        let released = false;
        const release = () => {
            if (released) return;
            released = true;
            this._replaying--;
            this._stats.replayed++;
        };

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
                    const target = document.elementFromPoint(p.x, p.y) || start;
                    if (isLast) {
                        this._fire(target, 'mouseup', p.x, p.y, 0, msg.button);
                        release();
                    } else {
                        this._fire(target, 'mousemove', p.x, p.y, 1, msg.button);
                    }
                }, when);
            })(i, elapsed, i === pts.length - 1);
        }

        // The deadman: if the path never reaches its last point, release capture
        // anyway and say so, rather than leaving the panel mute.
        setTimeout(() => {
            if (released) return;
            console.warn('[FsCopilot] [Pointer] Drag replay did not complete - releasing capture');
            release();
        }, elapsed + 3000);

        return true;
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
Pointer.STATE_DEADMAN_MS = 8000;
Pointer.LOST_LINGER_MS = 10000;  // how long the red warning stands before retracting itself
