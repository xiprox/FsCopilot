/*
 * A WebSocket from the panel document straight to the FS Copilot desktop app,
 * bypassing the CommBus/WASM/SimConnect bus and its 512-byte single-slot buffer.
 *
 * The app binds the first free port in PORTS; this side rotates through the same
 * range while reconnecting, so neither side needs to be told where the other is.
 * Panel documents reload on view changes and the app may start after the sim, so
 * the socket reconnects on its own with backoff and re-identifies every hook.
 *
 * A close alone cannot tell a deliberate app shutdown from a crash, so the app
 * announces the deliberate one with a goodbye first and the close event carries
 * which it was - the overlay policy for the two is not the same.
 *
 * Coherent GT is Chrome 49: no optional chaining, no ??, no class fields.
 */
class Channel extends Emitter {
    constructor() {
        super();

        this._ws = null;
        this._queue = [];      // {text, at} - bounded, age-capped: stale input is worse than none
        this._hellos = [];     // re-sent on every (re)connect so the app can rebuild its routing
        this._attempt = 0;
        this._portIndex = 0;
        this._timer = null;
        this._confirmTimer = null;
        this._confirmed = false;
        this._deliberate = false;  // the app said goodbye; the next close is not a fault
        this._stats = {sent: 0, received: 0, dropped: 0, reconnects: 0};

        this._connect();
    }

    /*
     * Registers this hook's identity. Sent now if the socket is open and again on
     * every reconnect. extra is merged into the hello (e.g. the instrument rect,
     * so the app can compare geometry across machines).
     */
    hello(name, extra) {
        const msg = Object.assign({t: 'hello', name: name, url: location.href}, extra || {});
        this._hellos.push(msg);
        if (this._ws && this._ws.readyState === 1) this._rawSend(JSON.stringify(msg));
    }

    send(data) {
        let text;
        try { text = JSON.stringify(data); } catch (e) { return; }
        this._stats.sent++;
        if (this._ws && this._ws.readyState === 1) {
            this._rawSend(text);
            return;
        }
        this._queue.push({text: text, at: Date.now()});
        while (this._queue.length > Channel.MAX_QUEUE) {
            this._queue.shift();
            this._stats.dropped++;
        }
    }

    state() { return this._ws && this._ws.readyState === 1 ? 'open' : 'connecting'; }

    stats() {
        const s = Object.assign({}, this._stats);
        s.queued = this._queue.length;
        s.state = this.state();
        return s;
    }

    _connect() {
        const port = Channel.PORTS[this._portIndex % Channel.PORTS.length];
        let ws;
        try {
            ws = new WebSocket('ws://127.0.0.1:' + port + '/');
        } catch (e) {
            this._schedule();
            return;
        }
        this._ws = ws;

        ws.onopen = () => {
            console.log('[FsCopilot] [Channel] Connected on port ' + port);
            this._attempt = 0;
            for (let i = 0; i < this._hellos.length; i++) {
                this._rawSend(JSON.stringify(this._hellos[i]));
            }
            this._flush();
            // A non-FS-Copilot server could accept the socket and say nothing, which
            // would pin us to the wrong port forever. The app answers every hello
            // immediately, so silence means wrong server: drop and keep rotating.
            this._confirmed = false;
            this._confirmTimer = setTimeout(() => {
                if (this._confirmed) return;
                console.warn('[FsCopilot] [Channel] No reply on port ' + port + '; rotating');
                try { ws.close(); } catch (e) { /* already gone */ }
            }, Channel.CONFIRM_MS);
        };

        ws.onmessage = (ev) => {
            this._confirmed = true;
            let msg;
            try { msg = JSON.parse(String(ev.data)); } catch (e) { return; }
            this._stats.received++;
            // The goodbye is the last thing the app ever sends, and any other reply
            // proves it is alive. So one assignment covers both: a bye sets the flag,
            // and the next message of any other kind clears it. A stale goodbye
            // cannot then silence a later real break.
            this._deliberate = !!msg && msg.t === 'bye';
            if (this._deliberate) console.log('[FsCopilot] [Channel] App announced shutdown');
            this.dispatchEvent('message', msg);
        };

        ws.onerror = () => { /* onclose always follows */ };

        ws.onclose = () => {
            this._ws = null;
            if (this._confirmTimer) { clearTimeout(this._confirmTimer); this._confirmTimer = null; }
            this.dispatchEvent('close', {deliberate: this._deliberate});
            this._portIndex++;
            this._schedule();
        };
    }

    _schedule() {
        if (this._timer) return;
        // The backoff tier advances once per full lap of the port range, not per
        // attempt - otherwise a restarted app takes ports x max-backoff (over a
        // minute) to be rediscovered. A refused loopback connect costs nothing,
        // so the steady state of one lap per 20s is cheap.
        const backoff = Channel.BACKOFF;
        const tier = Math.floor(this._attempt / Channel.PORTS.length);
        const wait = backoff[tier < backoff.length ? tier : backoff.length - 1];
        this._attempt++;
        if (this._attempt > 1) this._stats.reconnects++;
        this._timer = setTimeout(() => { this._timer = null; this._connect(); }, wait);
    }

    _flush() {
        const now = Date.now();
        while (this._queue.length && this._ws && this._ws.readyState === 1) {
            const item = this._queue.shift();
            if (now - item.at > Channel.MAX_QUEUE_AGE_MS) { this._stats.dropped++; continue; }
            this._rawSend(item.text);
        }
    }

    _rawSend(text) {
        try { this._ws.send(text); } catch (e) { /* the socket may be mid-close */ }
    }
}

Channel.PORTS = [9020, 9021, 9022, 9023, 9024];
Channel.BACKOFF = [500, 1000, 2000, 4000]; // per lap of PORTS; see _schedule

Channel.MAX_QUEUE = 200;
Channel.MAX_QUEUE_AGE_MS = 30000;
Channel.CONFIRM_MS = 10000;

/*
 * The routing key for one instrument. instrumentIdentifier alone is not unique:
 * the A220 reports 'CTP' for both CTPs and 'FCP' for all four FCPs. The side is
 * carried only in the instrument's url attribute query, so the key appends that.
 * The query comes from panel.cfg and is identical across machines.
 */
Channel.keyFor = function (instrument) {
    const id = instrument.instrumentIdentifier || instrument.tagName.toLowerCase();
    const url = instrument.getAttribute('url') || instrument.getAttribute('Url') || '';
    const q = url.indexOf('?');
    return q >= 0 ? id + '|' + url.substring(q + 1) : id;
};
