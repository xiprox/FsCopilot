/*
 * A WebSocket from the panel document to the FS Copilot app, bypassing the
 * CommBus and its 512-byte slot. The app binds the first free port in PORTS;
 * this side tries each in turn, and reconnects on its own.
 *
 * Coherent GT is Chrome 49: no optional chaining, no ??, no class fields.
 */

/*
 * DIAGNOSTIC BUILD. fscLog sends a line to the app's log file as {t:'log'}, because a
 * remote tester has no inspector. Lines have their own queue: sharing the gesture queue
 * would let a burst of logging push a gesture out of it.
 */
window.fscLogQueue = window.fscLogQueue || [];
window.fscLogDropped = window.fscLogDropped || 0;
window.fscLog = function (src, text) {
    try {
        const line = {t: 'log', src: src, msg: String(text), at: Date.now()};
        console.log('[FsCopilot] [' + src + '] ' + text);
        const ch = window.fscChannel;
        if (ch && ch._ws && ch._ws.readyState === 1) {
            ch._rawSend(JSON.stringify(line));
            return;
        }
        window.fscLogQueue.push(line);
        while (window.fscLogQueue.length > Channel.MAX_LOG_QUEUE) {
            window.fscLogQueue.shift();
            window.fscLogDropped++;
        }
    } catch (e) { /* logging must never break the panel */ }
};

/* tag#id.class, enough to tell two buttons apart in a log line. */
window.fscDescribe = function (el) {
    try {
        if (!el) return '(null)';
        if (el.nodeType !== 1) return '(node type ' + el.nodeType + ')';
        let s = String(el.tagName || '?').toLowerCase();
        const id = el.getAttribute('id');
        if (id) s += '#' + id;
        // SVG className is an object, so read the attribute.
        const cls = el.getAttribute('class');
        if (cls) s += '.' + String(cls).trim().split(/\s+/).slice(0, 3).join('.');
        const parent = el.parentElement;
        if (parent) {
            const pid = parent.getAttribute('id');
            const pcls = parent.getAttribute('class');
            s += ' in ' + String(parent.tagName || '?').toLowerCase() +
                (pid ? '#' + pid : '') + (pcls ? '.' + String(pcls).trim().split(/\s+/)[0] : '');
        }
        return s;
    } catch (e) { return '(describe failed)'; }
};

class Channel extends Emitter {
    constructor() {
        super();

        this._ws = null;
        this._queue = [];      // {text, at}; bounded and age-capped, since stale input is worse than none
        this._hellos = [];     // re-sent on every reconnect
        this._attempt = 0;
        this._portIndex = 0;
        this._timer = null;
        this._confirmTimer = null;
        this._confirmed = false;
        this._deliberate = false;  // the app said goodbye; the next close is not a fault
        this._stats = {sent: 0, received: 0, dropped: 0, reconnects: 0};

        this._failedSinceOpen = 0;
        this._closedAt = 0;
        this._openedAt = 0;
        this._lastSync = null;
        this._lastConfig = null;

        this._log('Created for ' + location.href);
        this._connect();
    }

    _log(text) { window.fscLog('Channel', text); }

    /* extra is merged into the hello, e.g. the instrument rect. */
    /* One hello per name: a re-announcement replaces it, so a reconnect replays the
     * latest rect and not the pre-layout one. */
    hello(name, extra) {
        const msg = Object.assign({t: 'hello', name: name, url: location.href}, extra || {});
        let replaced = false;
        for (let i = 0; i < this._hellos.length; i++) {
            if (this._hellos[i].name !== name) continue;
            this._hellos[i] = msg;
            replaced = true;
            break;
        }
        if (!replaced) this._hellos.push(msg);
        const open = !!(this._ws && this._ws.readyState === 1);
        this._log('Hello ' + name + (msg.rect ? ' rect=' + msg.rect[0] + 'x' + msg.rect[1] : ' rect=none') +
            (replaced ? ' (replaces earlier)' : '') + (open ? ' sent now' : ' held until open'));
        if (open) this._rawSend(JSON.stringify(msg));
    }

    send(data) {
        let text;
        try { text = JSON.stringify(data); } catch (e) {
            this._log('send: JSON.stringify failed (' + e + '); message lost');
            return;
        }
        this._stats.sent++;
        if (this._ws && this._ws.readyState === 1) {
            this._rawSend(text);
            return;
        }
        this._queue.push({text: text, at: Date.now()});
        if (data && data.t === 'pointer') {
            this._log('Gesture queued: socket not open (state=' + (this._ws ? this._ws.readyState : 'none') +
                '), queue=' + this._queue.length);
        }
        while (this._queue.length > Channel.MAX_QUEUE) {
            const lost = this._queue.shift();
            this._stats.dropped++;
            this._log('Queue over ' + Channel.MAX_QUEUE + ': dropped the oldest (' + lost.text.substring(0, 80) + ')');
        }
    }

    state() { return this._ws && this._ws.readyState === 1 ? 'open' : 'connecting'; }

    stats() {
        const s = Object.assign({}, this._stats);
        s.queued = this._queue.length;
        s.state = this.state();
        s.logQueued = window.fscLogQueue.length;
        s.logDropped = window.fscLogDropped;
        return s;
    }

    _connect() {
        const port = Channel.PORTS[this._portIndex % Channel.PORTS.length];
        let ws;
        try {
            ws = new WebSocket('ws://127.0.0.1:' + port + '/');
        } catch (e) {
            this._log('new WebSocket on port ' + port + ' threw: ' + e);
            this._schedule();
            return;
        }
        this._ws = ws;
        let opened = false;

        ws.onopen = () => {
            opened = true;
            this._openedAt = Date.now();
            const failed = this._failedSinceOpen;
            this._failedSinceOpen = 0;
            this._attempt = 0;
            for (let i = 0; i < this._hellos.length; i++) {
                this._rawSend(JSON.stringify(this._hellos[i]));
            }
            // Log lines held while closed go before the open line, so the log reads in order.
            const held = window.fscLogQueue.length;
            const lostLines = window.fscLogDropped;
            window.fscLogDropped = 0;
            if (lostLines) this._rawSend(JSON.stringify({t: 'log', src: 'Channel', at: Date.now(),
                msg: lostLines + ' log line(s) dropped while the socket was closed'}));
            while (window.fscLogQueue.length) this._rawSend(JSON.stringify(window.fscLogQueue.shift()));
            this._log('Open on port ' + port + ' after ' + failed + ' failed attempt(s)' +
                (this._closedAt ? ', ' + (this._openedAt - this._closedAt) + 'ms after the last close' : '') +
                '; re-sent ' + this._hellos.length + ' hello(s), ' + held + ' held log line(s), flushing ' +
                this._queue.length + ' queued message(s)');
            this._flush();
            // Another server on the port could accept and never answer. The app answers
            // every hello immediately, so silence means the wrong server: move on.
            this._confirmed = false;
            this._confirmTimer = setTimeout(() => {
                if (this._confirmed) return;
                this._log('No reply on port ' + port + ' within ' + Channel.CONFIRM_MS + 'ms; rotating');
                try { ws.close(); } catch (e) { /* already gone */ }
            }, Channel.CONFIRM_MS);
        };

        ws.onmessage = (ev) => {
            if (!this._confirmed) this._log('First reply on port ' + port + ' ' + (Date.now() - this._openedAt) + 'ms after open');
            this._confirmed = true;
            let msg;
            try { msg = JSON.parse(String(ev.data)); } catch (e) {
                this._log('Unparseable message from the app: ' + String(ev.data).substring(0, 120));
                return;
            }
            this._stats.received++;
            // bye sets the flag and any later message clears it, so an old goodbye
            // cannot hide a real break.
            this._deliberate = !!msg && msg.t === 'bye';
            if (this._deliberate) this._log('App announced shutdown');
            if (msg && msg.t === 'config') {
                const cfg = JSON.stringify(msg.pointer || []);
                if (cfg !== this._lastConfig) this._log('Config pointer=' + cfg);
                this._lastConfig = cfg;
            } else if (msg && msg.t === 'state') {
                const s = msg.sync + '/' + msg.role;
                if (s !== this._lastSync) this._log('State ' + (this._lastSync || '(first)') + ' -> ' + s);
                this._lastSync = s;
            } else if (msg && msg.t === 'pointer') {
                const m = msg.msg || {};
                this._log('Rx pointer seq=' + msg.seq + ' key=' + m.key + ' k=' + m.k);
            } else if (msg && msg.t !== 'bye' && msg.t !== 'panels') {
                this._log('Rx unknown message t=' + (msg ? msg.t : msg));
            }
            this.dispatchEvent('message', msg);
        };

        ws.onerror = () => { /* onclose always follows */ };

        ws.onclose = (ev) => {
            this._ws = null;
            if (this._confirmTimer) { clearTimeout(this._confirmTimer); this._confirmTimer = null; }
            if (opened) {
                this._closedAt = Date.now();
                this._lastSync = null;
                // Queued, so it reaches the log on the next open.
                this._log('Closed on port ' + port + ' after ' + (this._closedAt - this._openedAt) + 'ms open' +
                    ' (code=' + (ev ? ev.code : '?') + ', confirmed=' + this._confirmed +
                    ', deliberate=' + this._deliberate + ')');
            } else {
                this._failedSinceOpen++;
                if (this._failedSinceOpen % 25 === 0) {
                    this._log('Still connecting: ' + this._failedSinceOpen + ' failed attempts since the last open');
                }
            }
            this.dispatchEvent('close', {deliberate: this._deliberate});
            this._portIndex++;
            this._schedule();
        };
    }

    _schedule() {
        if (this._timer) return;
        // The backoff tier advances once per lap of the ports, not per attempt, or a
        // restarted app would take over a minute to be found again.
        const backoff = Channel.BACKOFF;
        const tier = Math.floor(this._attempt / Channel.PORTS.length);
        const wait = backoff[tier < backoff.length ? tier : backoff.length - 1];
        this._attempt++;
        if (this._attempt > 1) this._stats.reconnects++;
        this._timer = setTimeout(() => { this._timer = null; this._connect(); }, wait);
    }

    _flush() {
        const now = Date.now();
        let sent = 0;
        let aged = 0;
        while (this._queue.length && this._ws && this._ws.readyState === 1) {
            const item = this._queue.shift();
            if (now - item.at > Channel.MAX_QUEUE_AGE_MS) {
                this._stats.dropped++;
                aged++;
                this._log('Dropped a queued message ' + (now - item.at) + 'ms old: ' + item.text.substring(0, 80));
                continue;
            }
            this._rawSend(item.text);
            sent++;
        }
        if (sent || aged) this._log('Flushed ' + sent + ' queued message(s), dropped ' + aged + ' as too old');
    }

    _rawSend(text) {
        try { this._ws.send(text); } catch (e) {
            // Not through fscLog: that would try this same socket again.
            console.warn('[FsCopilot] [Channel] send threw: ' + e);
        }
    }
}

Channel.PORTS = [9020, 9021, 9022, 9023, 9024];
Channel.BACKOFF = [500, 1000, 2000, 4000]; // per lap of PORTS; see _schedule

Channel.MAX_QUEUE = 200;
Channel.MAX_QUEUE_AGE_MS = 30000;
Channel.CONFIRM_MS = 10000;
Channel.MAX_LOG_QUEUE = 500;

/*
 * instrumentIdentifier alone is not unique: the A220 reports 'CTP' for both CTPs.
 * The side is only in the instrument url's query (from panel.cfg), so it is appended.
 */
Channel.keyFor = function (instrument) {
    const id = instrument.instrumentIdentifier || instrument.tagName.toLowerCase();
    const url = instrument.getAttribute('url') || instrument.getAttribute('Url') || '';
    const q = url.indexOf('?');
    return q >= 0 ? id + '|' + url.substring(q + 1) : id;
};
