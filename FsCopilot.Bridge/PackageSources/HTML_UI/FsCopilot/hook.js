class Hook {
    constructor(instrument) {
        const hookId = Math.floor(Math.random() * 100000);
        const id = instrument.instrumentIdentifier;
        // document.title = 'FS Copilot Hook - ' + id;
        SimVar.SetSimVarValue('L:FSC_HOOK', 'number', hookId);

        const bus = new Bus();

        // One channel per document, shared by every hook in it. The hello identifies
        // this instrument to the desktop app; Channel re-sends it on every reconnect.
        if (!window.fscChannel) window.fscChannel = new Channel();
        const channel = window.fscChannel;
        this.key = Channel.keyFor(instrument);
        let rect = null;
        try {
            const r = instrument.getBoundingClientRect();
            if (r && r.width > 0) rect = [Math.round(r.width), Math.round(r.height)];
        } catch (e) { /* not laid out yet; stats reports it later */ }
        channel.hello(this.key, rect ? {rect: rect} : null);

        // Pointer mode is a per-instrument profile opt-in delivered over the channel.
        // While on, this instrument's element-name sync is suppressed in both
        // directions - one press must not actuate twice. No config ever (app absent)
        // leaves everything exactly as it was: events mode, no overlays.
        //
        // One Pointer instance per document; the hook that created it is the owner
        // and the only one that replays and tracks state. A duplicate hook for the
        // same instrument (multi-instrument documents construct one per instrument)
        // still suppresses its own events path, but does not double-replay.
        this._pointerMode = false;
        this._pointerOwner = false;
        channel.addEventListener('message', (msg) => {
            if (!msg) return;
            if (msg.t === 'config') {
                this._configure(msg.pointer || [], instrument, channel);
            } else if (msg.t === 'state') {
                if (this._pointerOwner && window.fscPointer) window.fscPointer.updateState(msg.session, msg.role);
            } else if (msg.t === 'pointer') {
                if (this._pointerOwner && window.fscPointer && msg.msg) window.fscPointer.replay(msg.msg);
            }
        });
        channel.addEventListener('close', () => {
            if (this._pointerOwner && window.fscPointer) window.fscPointer.linkLost();
        });

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

    _configure(pointerKeys, instrument, channel) {
        const want = pointerKeys.indexOf(this.key) >= 0;
        if (want && !this._pointerMode) {
            if (window.fscPointer && window.fscPointer.key !== this.key) {
                // v1 restriction: one pointer agent per document; first opted
                // instrument wins. Known limitation for multi-instrument documents.
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
