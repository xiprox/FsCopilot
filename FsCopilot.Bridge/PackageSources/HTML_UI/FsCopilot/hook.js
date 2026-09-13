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

        if (!window.fscStatsTimer) {
            window.fscStatsTimer = setInterval(() => {
                channel.send({t: 'stats', link: channel.stats()});
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
        events.addEventListener('emit', ev => bus
            .send({type: 'interact', instrument: id, event: ev.type, id: ev.id, value: ev.value}));
        bus.addEventListener('message', msg => {
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
}
