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

        const interact = instrument.onInteractionEvent;
        instrument.onInteractionEvent = (_args) => {
            interact.call(instrument, _args);
            if (SimVar.GetSimVarValue('L:FSC_HOOK', 'number') != hookId) return;
            bus.send({type: 'hevent', name: _args[0]});
        }

        if (!instrument.isInteractive) return;

        const events = new HtmlEvents();
        events.addEventListener('emit', ev => bus
            .send({type: 'interact', instrument: id, event: ev.type, id: ev.id, value: ev.value}));
        bus.addEventListener('message', msg => {
            if (msg.type !== 'interact') return;
            if (id !== msg.instrument) return;
            events.dispatch(msg.event, msg.id, msg.value);
        });
    }
}
