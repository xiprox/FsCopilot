class Hook {
    constructor(instrument) {
        const hookId = Math.floor(Math.random() * 100000);
        const id = instrument.instrumentIdentifier;
        // document.title = 'FS Copilot Hook - ' + id;
        SimVar.SetSimVarValue('L:FSC_HOOK', 'number', hookId);

        const bus = new Bus();

        // One channel per document, shared by every hook in it.
        if (!window.fscChannel) window.fscChannel = new Channel();
        const channel = window.fscChannel;
        this.key = Channel.keyFor(instrument);
        this._log('Hook created: id=' + id + ' hookId=' + hookId + ' interactive=' + !!instrument.isInteractive +
            ' url=' + (instrument.getAttribute('url') || instrument.getAttribute('Url') || '(none)'));
        this._announce(instrument, channel);

        this._pointerMode = false;
        this._pointerOwner = false;
        channel.addEventListener('message', (msg) => {
            if (!msg) return;
            if (msg.t === 'config') {
                this._configure(msg.pointer || [], instrument, channel);
            } else if (msg.t === 'state') {
                if (this._pointerOwner && window.fscPointer) window.fscPointer.updateState(msg.sync, msg.role);
            } else if (msg.t === 'pointer') {
                const m = msg.msg;
                if (this._pointerOwner && window.fscPointer && m) {
                    const took = window.fscPointer.replay(m, msg.seq);
                    if (!took) this._log('Replay refused seq=' + msg.seq + ' key=' + (m && m.key));
                } else if (m && m.key === this.key) {
                    // Routed here by key, but this document never went into pointer mode.
                    this._log('Pointer message seq=' + msg.seq + ' for ' + m.key + ' IGNORED: owner=' + this._pointerOwner +
                        ' pointerMode=' + this._pointerMode + ' fscPointer=' +
                        (window.fscPointer ? window.fscPointer.key : 'none'));
                }
            }
        });
        channel.addEventListener('close', (info) => {
            if (this._pointerOwner && window.fscPointer) {
                window.fscPointer.linkClosed(!!info && info.deliberate === true);
            }
        });

        if (!window.fscStatsTimer) {
            window.fscStatsTimer = setInterval(() => {
                const report = {t: 'stats', link: channel.stats()};
                if (window.fscPointer) {
                    report.key = window.fscPointer.key;
                    report.pointer = window.fscPointer.stats();
                }
                channel.send(report);
            }, Hook.STATS_MS);
        }

        const interact = instrument.onInteractionEvent;
        instrument.onInteractionEvent = (_args) => {
            interact.call(instrument, _args);
            if (SimVar.GetSimVarValue('L:FSC_HOOK', 'number') != hookId) return;
            // A replayed click that fires an H: event the profile also syncs would actuate twice.
            if (window.fscPointer) {
                this._log('H event ' + _args[0] + ' in a pointer-mode document (replaying=' +
                    !!window.fscPointer._busy + ', queued=' + window.fscPointer._queue.length + ')');
            }
            bus.send({type: 'hevent', name: _args[0]});
        }

        if (!instrument.isInteractive) return;

        const events = new HtmlEvents();
        events.addEventListener('emit', ev => {
            if (this._pointerMode) {
                this._log('Events-mode emit suppressed by pointer mode: ' + ev.type + ' id=' + ev.id +
                    (ev.value != null ? ' value=' + ev.value : ''));
                return;
            }
            bus.send({type: 'interact', instrument: id, event: ev.type, id: ev.id, value: ev.value});
        });
        bus.addEventListener('message', msg => {
            if (msg.type !== 'interact') return;
            if (id !== msg.instrument) return;
            if (this._pointerMode) {
                this._log('Inbound Interact ignored by pointer mode: ' + msg.event + ' id=' + msg.id);
                return;
            }
            events.dispatch(msg.event, msg.id, msg.value);
        });
    }

    _log(text) { window.fscLog('Hook ' + this.key, text); }

    /* An instrument not laid out yet measures as zero, so retry until it answers. */
    _announce(instrument, channel) {
        const measure = () => {
            try {
                const r = instrument.getBoundingClientRect();
                if (r && r.width > 0) return [Math.round(r.width), Math.round(r.height)];
            } catch (e) { /* not laid out */ }
            return null;
        };

        const rect = measure();
        channel.hello(this.key, rect ? {rect: rect} : null);
        if (rect) return;

        let tries = 0;
        const timer = setInterval(() => {
            const late = measure();
            if (late) {
                channel.hello(this.key, {rect: late});
                this._log('Measured late: ' + late[0] + 'x' + late[1] + ' after ' + (tries + 1) + 's');
            }
            if (late || ++tries >= 60) {
                if (!late) this._log('Still no rect after 60s; giving up on measuring');
                clearInterval(timer);
            }
        }, 1000);
    }

    /* The app filters the same way, or a panel captures gestures the app then drops. */
    _configure(pointerKeys, instrument, channel) {
        const id = this.key.split('|')[0];
        const byKey = pointerKeys.indexOf(this.key) >= 0;
        const byId = pointerKeys.indexOf(id) >= 0;
        const want = byKey || byId;
        this._log('Config [' + pointerKeys.join(', ') + ']: want=' + want +
            (want ? ' (matched by ' + (byKey ? 'key' : 'identifier ' + id) + ')' : '') +
            ' pointerMode=' + this._pointerMode + ' owner=' + this._pointerOwner);
        if (want && !this._pointerMode) {
            if (window.fscPointer && window.fscPointer.key !== this.key) {
                // One pointer instrument per document: the first to opt in wins.
                this._log('Pointer already owned by ' + window.fscPointer.key + '; this one stays in events mode');
                return;
            }
            if (!window.fscPointer) {
                window.fscPointer = new Pointer(instrument, this.key);
                window.fscPointer.onCapture((m) => channel.send({t: 'pointer', msg: m}));
                this._pointerOwner = true;
            }
            this._pointerMode = true;
            this._log('Pointer mode ON');
        } else if (!want && this._pointerMode) {
            this._pointerMode = false;
            if (this._pointerOwner && window.fscPointer) {
                window.fscPointer.stop();
                window.fscPointer = null;
                this._pointerOwner = false;
            }
            this._log('Pointer mode OFF; events mode restored');
        }
    }
}

Hook.STATS_MS = 10000;  // DIAGNOSTIC BUILD: 60000 in the PR
