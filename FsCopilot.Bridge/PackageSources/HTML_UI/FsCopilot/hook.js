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
                if (this._pointerOwner && window.fscPointer && msg.msg) window.fscPointer.replay(msg.msg);
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
            }, 60000);
        }

        const interact = instrument.onInteractionEvent;
        instrument.onInteractionEvent = (_args) => {
            interact.call(instrument, _args);
            if (SimVar.GetSimVarValue('L:FSC_HOOK', 'number') != hookId) return;
            bus.send({type: 'hevent', name: _args[0]});
        }

        if (!instrument.isInteractive) return;

        const events = new HtmlEvents();
        events.addEventListener('emit', ev => {
            if (this._pointerMode) return;
            bus.send({type: 'interact', instrument: id, event: ev.type, id: ev.id, value: ev.value});
        });
        bus.addEventListener('message', msg => {
            if (this._pointerMode) return;
            if (msg.type !== 'interact') return;
            if (id !== msg.instrument) return;
            events.dispatch(msg.event, msg.id, msg.value);
        });
    }

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
                console.log('[FsCopilot] [Hook] ' + this.key + ' measured late: ' +
                    late[0] + 'x' + late[1] + ' after ' + (tries + 1) + 's');
            }
            if (late || ++tries >= 60) clearInterval(timer);
        }, 1000);
    }

    /* The app filters the same way, or a panel captures gestures the app then drops. */
    _configure(pointerKeys, instrument, channel) {
        const id = this.key.split('|')[0];
        const want = pointerKeys.indexOf(this.key) >= 0 || pointerKeys.indexOf(id) >= 0;
        if (want && !this._pointerMode) {
            if (window.fscPointer && window.fscPointer.key !== this.key) {
                // One pointer instrument per document: the first to opt in wins.
                console.warn('[FsCopilot] [Hook] Pointer already owned by ' + window.fscPointer.key +
                    '; ' + this.key + ' stays in events mode');
                return;
            }
            if (!window.fscPointer) {
                window.fscPointer = new Pointer(instrument, this.key);
                window.fscPointer.onCapture((m) => channel.send({t: 'pointer', msg: m}));
                this._pointerOwner = true;
            }
            this._pointerMode = true;
            console.log('[FsCopilot] [Hook] Pointer mode on for ' + this.key);
        } else if (!want && this._pointerMode) {
            this._pointerMode = false;
            if (this._pointerOwner && window.fscPointer) {
                window.fscPointer.stop();
                window.fscPointer = null;
                this._pointerOwner = false;
            }
            console.log('[FsCopilot] [Hook] Pointer mode off for ' + this.key + '; events mode restored');
        }
    }
}
