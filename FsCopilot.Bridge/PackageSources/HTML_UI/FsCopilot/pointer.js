/*
 * Captures gestures on an instrument as fractions of its bounding rect, and
 * replays the peer's gestures at the same fractions.
 *
 * Only adds listeners and dispatches events; stop() removes everything it added.
 * The lock overlay stays up only while the app keeps renewing the sync state.
 * While a replay runs it blocks without painting: a veil over every drag would
 * cover the instrument at the moment the pilot is watching it change.
 *
 * Coherent GT is Chrome 49: MouseEvent only, no optional chaining, no ??, no class fields.
 */
class Pointer {
    constructor(instrument, key) {
        this._instrument = instrument;
        this.key = key;
        this._listeners = [];
        this._captureFns = [];
        // echo: our own replayed events seen by capture, two per replayed press.
        this._stats = {captured: 0, replayed: 0, missed: 0, echo: 0, outside: 0, locked: 0, stalled: 0};
        this._pendingDown = null;
        this._lastGestureEnd = 0;   // capture side: when the previous gesture ended

        this._queue = [];
        this._busy = false;
        this._pumping = false;
        this._deadman = null;
        this._lastReplayEnd = 0;    // replay side: when the previous gesture finished here
        this._replayStart = 0;      // when the current unbroken run of blocking began
        this._noticeTimer = null;

        this._overlay = new Overlay(() => this._rect());
        this._overlayMuted = false;
        this._debugHold = false;
        // One red warning per outage. While the app is down the channel closes on every
        // retry, and each close would otherwise show it again.
        this._warned = false;
        this._lostTimer = null;
        this._lastState = 0;
        this._syncState = 'none';
        this._role = 'master';
        this._lastWant = undefined;   // DIAGNOSTIC: last overlay decision, logged on change
        this._cid = 0;                // DIAGNOSTIC: capture counter, sent as msg.cid
        this._rid = 0;                // DIAGNOSTIC: replay counter
        this._lastRect = '';

        this._listen(document, 'mousedown', (ev) => this._onDown(ev));
        this._listen(document, 'mousemove', (ev) => this._onMove(ev));
        this._listen(document, 'mouseup', (ev) => this._onUp(ev));

        // No renewal means the app is gone, and a blocking overlay must not outlive it.
        this._watchdog = setInterval(() => {
            if (this._debugHold || this._overlayMuted) return;
            if (this._lastState && Date.now() - this._lastState > Pointer.STATE_DEADMAN_MS) {
                if (!this._warned) this._log('Watchdog: no state renewal for ' + (Date.now() - this._lastState) + 'ms');
                this._showLost();
            }
        }, 2000);

        // A fixed global, so it works even if every other handle is lost.
        window.fscUnlock = () => {
            this._log('fscUnlock() called: overlay muted');
            this._overlayMuted = true;
            this._debugHold = false;
            this._clearLostTimer();
            this._endReplayNotice();
            this._overlay.remove();
        };

        // Debug: fscOverlay('connecting'|'degraded'|'replaying'|'lost'|'clear') holds a state.
        window.fscOverlay = (state) => {
            this._log('fscOverlay(' + state + ') called');
            if (state === 'clear') {
                this._debugHold = false;
                this._overlay.remove();
                return 'cleared - next state renewal restores truth';
            }
            this._debugHold = true;
            this._overlay.apply(this._overlay.has(state) ? state : 'connecting');
            return 'holding ' + state + ' - fscOverlay(\'clear\') to release';
        };

        // Locked until the app sends the real state, which follows config immediately.
        this.updateState('connecting', 'master');

        const r = this._rect();
        this._log('Active: rect=' + this._rectText(r) + ' viewport=' + window.innerWidth + 'x' + window.innerHeight +
            ' dpr=' + window.devicePixelRatio + ' instrument=' + window.fscDescribe(instrument));
    }

    _log(text) { window.fscLog('Pointer ' + this.key, text); }

    _rectText(r) {
        return Math.round(r.width) + 'x' + Math.round(r.height) + '@' + Math.round(r.left) + ',' + Math.round(r.top);
    }

    /* Logged whenever it differs from the last one seen, since a rect change moves every replay. */
    _noteRect(r, where) {
        const t = this._rectText(r);
        if (t === this._lastRect) return;
        this._log('Rect ' + (this._lastRect || '(none)') + ' -> ' + t + ' (seen at ' + where + ')');
        this._lastRect = t;
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
        this._log('stop(): removing listeners, dropping ' + this._queue.length + ' queued replay(s), busy=' + this._busy);
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
        this._endReplayNotice();
        clearInterval(this._watchdog);
        this._clearLostTimer();
        this._overlay.remove();
    }

    /* Sync state -> overlay --------------------------------------------------- */

    updateState(sync, role) {
        if (sync !== this._syncState || role !== this._role) {
            this._log('Sync ' + this._syncState + '/' + this._role + ' -> ' + sync + '/' + role +
                (this._debugHold ? ' (debug hold active)' : '') + (this._overlayMuted ? ' (overlay muted)' : ''));
        }
        this._syncState = sync;
        this._role = role;
        this._lastState = Date.now();
        this._warned = false;
        this._clearLostTimer();
        if (this._debugHold) return; // a forced debug overlay outranks real state
        if (sync === 'live' || sync === 'none') this._overlayMuted = false;
        this._refreshOverlay(false);
    }

    /* Blocks only while the state is fresh: connecting on both sides, degraded on the
     * slave, and a replay in progress. The red warning runs on its own timer. */
    _refreshOverlay(fromReplay) {
        if (this._debugHold) return;
        if (this._overlayMuted) { this._overlay.remove(); return; }
        if (fromReplay && this._overlay.showing() === 'lost') return;

        const fresh = this._lastState > 0 && Date.now() - this._lastState <= Pointer.STATE_DEADMAN_MS;
        let want = null;
        if (fresh && this._syncState === 'connecting') want = 'connecting';
        else if (fresh && this._syncState === 'degraded' && this._role === 'slave') want = 'degraded';
        else if (fresh && (this._busy || this._queue.length)) want = this._replayOverlay();

        if (want !== this._lastWant) {
            this._log('Overlay ' + (this._lastWant || 'none') + ' -> ' + (want || 'none') +
                ' (sync=' + this._syncState + ' role=' + this._role + ' fresh=' + fresh +
                ' busy=' + this._busy + ' queued=' + this._queue.length + ')');
            this._lastWant = want;
        }
        if (want) this._overlay.apply(want);
        else this._overlay.remove();
        if (want !== 'replayingQuiet' && want !== 'replaying') this._endReplayNotice();
    }

    /* Silent until the block outlasts any one gesture. Its own timer, because
     * _refreshOverlay runs when a gesture starts or finishes and a long drag does neither. */
    _replayOverlay() {
        if (!this._replayStart) {
            this._replayStart = Date.now();
            this._noticeTimer = setTimeout(() => {
                this._noticeTimer = null;
                this._refreshOverlay(true);
            }, Pointer.REPLAY_NOTICE_MS);
        }
        return Date.now() - this._replayStart >= Pointer.REPLAY_NOTICE_MS
            ? 'replaying' : 'replayingQuiet';
    }

    _endReplayNotice() {
        this._replayStart = 0;
        if (!this._noticeTimer) return;
        clearTimeout(this._noticeTimer);
        this._noticeTimer = null;
    }

    /* The app says goodbye before a deliberate quit, so only an unannounced close warns. */
    linkClosed(deliberate) {
        if (!this._warned) this._log('Link closed, deliberate=' + deliberate + ' (overlay showing ' + this._overlay.showing() + ')');
        this._lastState = 0;
        if (this._overlayMuted || this._debugHold) return;
        if (deliberate) {
            this._clearLostTimer();
            this._overlay.remove();
            this._lastWant = null;
            return;
        }
        this._showLost();
    }

    /* Non-blocking, but it still covers the display, so it retracts after a while. */
    _showLost() {
        if (this._warned) return;
        this._warned = true;
        this._log('Overlay -> lost (red, non-blocking) for ' + Pointer.LOST_LINGER_MS + 'ms');
        this._lastWant = 'lost';
        this._overlay.apply('lost');
        this._lostTimer = setTimeout(() => {
            this._lostTimer = null;
            // A state that arrived meanwhile may have replaced it.
            if (this._overlay.showing() === 'lost') {
                this._log('Lost overlay retracted after ' + Pointer.LOST_LINGER_MS + 'ms');
                this._overlay.remove();
                this._lastWant = null;
            }
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
        msg.cid = ++this._cid;
        if (msg.k === 'drag') {
            const p = msg.path;
            const last = p[p.length - 1];
            this._log('CAPTURE cid=' + msg.cid + ' drag button=' + msg.button + ' gap=' + msg.gap + ' points=' + p.length +
                ' dur=' + last[0] + 'ms from ' + p[0][1] + ',' + p[0][2] + ' to ' + last[1] + ',' + last[2] +
                ' (sync=' + this._syncState + ' role=' + this._role + ' overlay=' + this._overlay.showing() + ')');
        } else {
            this._log('CAPTURE cid=' + msg.cid + ' press button=' + msg.button + ' at ' + msg.nx + ',' + msg.ny +
                ' up ' + msg.ux + ',' + msg.uy + ' hold=' + msg.hold + ' gap=' + msg.gap +
                ' (sync=' + this._syncState + ' role=' + this._role + ' overlay=' + this._overlay.showing() + ')');
        }
        for (let i = 0; i < this._captureFns.length; i++) {
            try { this._captureFns[i](msg); } catch (e) { this._log('Capture consumer threw: ' + e); }
        }
    }

    /* selfEmit marks our replayed events; isTrusted catches anything else synthetic. */
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

    /* Capped, so a long pause or an outage does not replay at full length. */
    _gapBefore(at) {
        if (!this._lastGestureEnd) return 0;
        const gap = at - this._lastGestureEnd;
        if (gap < 0) return 0;
        return gap > Pointer.GAP_MAX_MS ? Pointer.GAP_MAX_MS : gap;
    }

    /* Tests the target, not the point: in a document with several instruments, a click
     * on a neighbour is synced by element name and would otherwise actuate twice. */
    _inside(ev, n) {
        const t = ev.target;
        const lock = this._overlay.element();
        if (t && lock && (t === lock || (typeof lock.contains === 'function' && lock.contains(t)))) {
            this._stats.locked++;
            this._log('Input ' + ev.type + ' at ' + n.nx + ',' + n.ny + ' BLOCKED by the ' + this._overlay.showing() +
                ' overlay (sync=' + this._syncState + ' role=' + this._role + ' busy=' + this._busy +
                ' queued=' + this._queue.length + ')');
            return false;
        }
        let inside;
        let how;
        if (t && t.nodeType === 1 && typeof this._instrument.contains === 'function') {
            inside = this._instrument.contains(t);
            how = 'target';
        } else {
            const tol = Pointer.EDGE_TOLERANCE;
            inside = n.nx >= -tol && n.nx <= 1 + tol && n.ny >= -tol && n.ny <= 1 + tol;
            how = 'point';
        }
        if (!inside) {
            this._stats.outside++;
            this._log('Input ' + ev.type + ' at ' + n.nx + ',' + n.ny + ' OUTSIDE the instrument (by ' + how +
                ', target ' + window.fscDescribe(t) + '); not captured');
        }
        return inside;
    }

    _onDown(ev) {
        if (this._isOurs(ev)) {
            this._stats.echo++;
            this._log('Echo mousedown ignored (selfEmit=' + (ev.selfEmit === true) + ' isTrusted=' + ev.isTrusted +
                ' target ' + window.fscDescribe(ev.target) + ')');
            return;
        }
        const r = this._rect();
        this._noteRect(r, 'mousedown');
        const n = this._normalise(ev);
        this._log('Raw mousedown button=' + ev.button + ' client=' + ev.clientX + ',' + ev.clientY +
            ' n=' + (n ? n.nx + ',' + n.ny : 'none (rect ' + this._rectText(r) + ')') +
            ' target ' + window.fscDescribe(ev.target) + ' defaultPrevented=' + ev.defaultPrevented +
            (this._pendingDown ? ' (replaces an unfinished down)' : ''));
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
        if (this._isOurs(ev)) {
            this._stats.echo++;
            this._log('Echo mouseup ignored (selfEmit=' + (ev.selfEmit === true) + ' isTrusted=' + ev.isTrusted +
                ' target ' + window.fscDescribe(ev.target) + ')');
            return;
        }
        const n = this._normalise(ev);
        const d0 = this._pendingDown;
        this._log('Raw mouseup button=' + ev.button + ' client=' + ev.clientX + ',' + ev.clientY +
            ' n=' + (n ? n.nx + ',' + n.ny : 'none') + ' target ' + window.fscDescribe(ev.target) +
            (d0 ? ' held=' + (Date.now() - d0.at) + 'ms travelled=' + d0.travelled + 'px points=' + d0.path.length
                : ' (no pending down)'));
        if (!n) return;

        // An up without a matching down counts as a press at the up position.
        const now = Date.now();
        const d = this._pendingDown;
        if (!d && !this._inside(ev, n)) return;
        const held = d ? now - d.at : 0;
        const from = d || n;
        const button = typeof ev.button === 'number' ? ev.button : 0;
        const gap = this._gapBefore(d ? d.at : now);
        this._pendingDown = null;
        this._lastGestureEnd = now;

        // A flick faster than the sample interval is still a press.
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
        // Capture phase, because panel handlers may stopPropagation.
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
        const ok = target.dispatchEvent(ev);
        if (type !== 'mousemove') {
            this._log('  fire ' + type + ' at ' + x + ',' + y + ' on ' + window.fscDescribe(target) +
                (ok ? '' : ' (handler called preventDefault)'));
        }
        return ok;
    }

    replay(msg, seq) {
        if (!msg) return false;
        if (msg.key && msg.key !== this.key) {
            this._log('REPLAY seq=' + seq + ' REJECTED: message key ' + msg.key + ' is not this pointer (' + this.key + ')');
            return false;
        }
        if (msg.k !== 'press' && msg.k !== 'drag') {
            this._log('REPLAY seq=' + seq + ' REJECTED: unknown kind ' + msg.k);
            return false;
        }
        msg._rid = ++this._rid;
        msg._seq = seq;
        msg._rxAt = Date.now();
        this._log('REPLAY rid=' + msg._rid + ' seq=' + seq + ' queued: ' + this._describeMsg(msg) +
            ' (busy=' + this._busy + ', ahead of it=' + this._queue.length + ')');
        this._queue.push(msg);
        this._pump();
        return true;
    }

    _describeMsg(m) {
        if (m.k === 'drag') {
            const p = m.path || [];
            const last = p.length ? p[p.length - 1] : null;
            return 'drag button=' + m.button + ' gap=' + m.gap + ' points=' + p.length +
                (last ? ' dur=' + last[0] + 'ms from ' + p[0][1] + ',' + p[0][2] + ' to ' + last[1] + ',' + last[2] : '');
        }
        return 'press button=' + m.button + ' at ' + m.nx + ',' + m.ny + ' up ' + m.ux + ',' + m.uy +
            ' hold=' + m.hold + ' gap=' + m.gap;
    }

    /* Re-entrant: a gesture that finishes synchronously calls back in from the loop. */
    _pump() {
        if (this._pumping) return;
        this._pumping = true;
        try {
            while (!this._busy && this._queue.length) {
                const msg = this._queue.shift();
                const wait = this._remainingGap(msg);
                if (wait > 0) {
                    this._log('REPLAY rid=' + msg._rid + ' waits ' + wait + 'ms to keep the sender\'s gap of ' + msg.gap + 'ms');
                    // Busy during the wait, so the replaying overlay stays up.
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

    /* A live gesture already waited out its gap on the sender. A resent burst arrives
     * all at once, so it waits here. */
    _remainingGap(msg) {
        if (!this._lastReplayEnd || !msg.gap) return 0;
        const wait = msg.gap - (Date.now() - this._lastReplayEnd);
        return wait > 0 ? wait : 0;
    }

    _run(msg) {
        this._busy = true;
        const startedAt = Date.now();
        this._log('REPLAY rid=' + msg._rid + ' seq=' + msg._seq + ' starts ' + (startedAt - msg._rxAt) + 'ms after arrival' +
            ' (sync=' + this._syncState + ' role=' + this._role + ')');
        const done = this._once(() => {
            this._disarm();
            this._busy = false;
            this._lastReplayEnd = Date.now();
            this._log('REPLAY rid=' + msg._rid + ' done in ' + (this._lastReplayEnd - startedAt) + 'ms; ' +
                this._queue.length + ' left in the queue');
            this._pump();
        });
        let expect;
        try {
            expect = msg.k === 'drag' ? this._replayDrag(msg, done) : this._replayPress(msg, done);
        } catch (e) {
            // Unchanged behaviour: a throw here propagated before too. Logged, then rethrown.
            this._log('REPLAY rid=' + msg._rid + ' THREW: ' + e + (e && e.stack ? ' ' + String(e.stack).substring(0, 300) : ''));
            throw e;
        }
        if (expect < 0) { done(); return; }   // nothing to hit: nothing to wait for
        if (!this._busy) return;              // finished synchronously
        this._arm(expect + Pointer.REPLAY_DEADMAN_MS, done);
        this._refreshOverlay(true);
    }

    /* Releases the queue if a gesture's timers never finish. */
    _arm(ms, release) {
        this._disarm();
        this._deadman = setTimeout(() => {
            this._deadman = null;
            this._log('STALL: a replay did not complete within ' + ms + 'ms - releasing the queue (' +
                this._queue.length + ' waiting)');
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

    /* Returns the gesture's duration, or -1 if nothing was under the point. */
    _replayPress(msg, done) {
        const r = this._rect();
        this._noteRect(r, 'replay');
        if (!r.width || !r.height) {
            this._stats.missed++;
            this._log('REPLAY rid=' + msg._rid + ' MISSED: instrument rect is ' + this._rectText(r));
            return -1;
        }

        const x = Math.round(r.left + msg.nx * r.width);
        const y = Math.round(r.top + msg.ny * r.height);
        const ux = msg.ux == null ? msg.nx : msg.ux;
        const uy = msg.uy == null ? msg.ny : msg.uy;
        const x2 = Math.round(r.left + ux * r.width);
        const y2 = Math.round(r.top + uy * r.height);

        const target = this._overlay.hitTest(x, y);
        if (!target) {
            this._stats.missed++;
            this._log('REPLAY rid=' + msg._rid + ' MISSED: nothing at ' + x + ',' + y + ' (rect ' + this._rectText(r) + ')');
            return -1;
        }
        const inside = typeof this._instrument.contains === 'function' ? this._instrument.contains(target) : '?';
        this._log('REPLAY rid=' + msg._rid + ' press at ' + x + ',' + y + ' (rect ' + this._rectText(r) + ') hits ' +
            window.fscDescribe(target) + (inside === false ? ' - NOT INSIDE THE INSTRUMENT' : '') +
            '; hold ' + (msg.hold || 0) + 'ms');

        this._fire(target, 'mousedown', x, y, 1, msg.button);

        const finish = () => {
            const upHit = this._overlay.hitTest(x2, y2);
            const upTarget = upHit || target;
            if (upTarget !== target) {
                this._log('REPLAY rid=' + msg._rid + ' up target differs from down: ' + window.fscDescribe(upTarget) +
                    (upHit ? '' : ' (nothing at up point; reused the down target)'));
            }
            this._fire(upTarget, 'mouseup', x2, y2, 0, msg.button);
            this._fire(upTarget, 'click', x2, y2, 0, msg.button);
            this._stats.replayed++;
            done();
        };

        // The hold duration is kept, because panels read it (hold-to-reset and similar).
        if (msg.hold && msg.hold > 40) { setTimeout(finish, msg.hold); return msg.hold; }
        finish();
        return 0;
    }

    /* No click at the end: a pan that ends on another element must not also select it. */
    _replayDrag(msg, done) {
        const pts = msg.path;
        if (!pts || pts.length < 2) {
            this._stats.missed++;
            this._log('REPLAY rid=' + msg._rid + ' MISSED: drag path has ' + (pts ? pts.length : 'no') + ' point(s)');
            return -1;
        }

        const r = this._rect();
        this._noteRect(r, 'replay');
        if (!r.width || !r.height) {
            this._stats.missed++;
            this._log('REPLAY rid=' + msg._rid + ' MISSED: instrument rect is ' + this._rectText(r));
            return -1;
        }

        const px = (i) => ({
            x: Math.round(r.left + pts[i][1] * r.width),
            y: Math.round(r.top + pts[i][2] * r.height)
        });

        const first = px(0);
        const start = this._overlay.hitTest(first.x, first.y);
        if (!start) {
            this._stats.missed++;
            this._log('REPLAY rid=' + msg._rid + ' MISSED: nothing at drag start ' + first.x + ',' + first.y);
            return -1;
        }
        this._log('REPLAY rid=' + msg._rid + ' drag of ' + pts.length + ' points starts at ' + first.x + ',' + first.y +
            ' (rect ' + this._rectText(r) + ') on ' + window.fscDescribe(start));
        let lastTarget = start;

        this._fire(start, 'mousedown', first.x, first.y, 1, msg.button);

        // Scheduled from the gesture start rather than chained, so timer delays don't add up.
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
                    if (target !== lastTarget) {
                        this._log('REPLAY rid=' + msg._rid + ' drag point ' + index + ' at ' + p.x + ',' + p.y +
                            ' moves onto ' + window.fscDescribe(target));
                        lastTarget = target;
                    }
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

/* Pixel thresholds, not rect fractions, so they mean the same on every panel size. */
Pointer.DRAG_MIN_PX = 4;        // below this the gesture was a press
Pointer.DRAG_SAMPLE_MS = 33;    // ~30 Hz
Pointer.DRAG_MIN_STEP_PX = 2;   // ignore jitter between samples
Pointer.DRAG_MAX_POINTS = 240;  // bounds the message; ~8s of dragging
Pointer.DRAG_MAX_STEP_MS = 250; // a mid-drag pause replays as a bounded pause
Pointer.EDGE_TOLERANCE = 0.02;  // rect fraction allowed outside [0,1] when only the point can be tested
Pointer.GAP_MAX_MS = 1000;      // idle time between gestures is preserved up to this
Pointer.REPLAY_DEADMAN_MS = 3000; // past a gesture's expected end, release the queue
// One gesture fits underneath: a full drag replays in ~8 s, and one that stalls is released
// by the deadman around 11. Past this the block is a backlog, which nothing bounds.
Pointer.REPLAY_NOTICE_MS = 12000;
Pointer.STATE_DEADMAN_MS = 8000;
Pointer.LOST_LINGER_MS = 10000;  // how long the red warning stands before retracting itself
