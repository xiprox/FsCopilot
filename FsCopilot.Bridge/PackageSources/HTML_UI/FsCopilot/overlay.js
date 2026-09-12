/*
 * The pointer-sync panel overlay: veil, diagonal weave, and status card drawn
 * over the instrument while a pointer-synced panel is locked (connecting, sync
 * degraded on the slave) or warned (sync broken).
 *
 * Pure renderer: which state shows, when it clears, muting, the deadman and the
 * debug hold are all Pointer's policy - this class only draws what it is told.
 * A blocking state swallows input by covering it (removing the node is the
 * complete restore, unlike listener suppression); a non-blocking state sets
 * pointer-events:none and must never eat a click.
 *
 * Coherent GT is Chrome 49: no optional chaining, no ??, no class fields.
 */
class Overlay {
    /* rectFn returns the instrument's bounding rect; called on every apply so a
     * pre-layout zero rect falls back to the whole viewport. */
    constructor(rectFn) {
        this._rectFn = rectFn;
        this._el = null;
        this._name = null;
    }

    /* Idempotent per state: renewals arrive every 2s, and rebuilding an
     * identical overlay would restart its fade-in and heartbeat. */
    apply(name) {
        if (this._el && this._name === name) return;
        this._name = name;
        const spec = Overlay.STATES[name];
        this.remove();

        if (!window.fscOverlayCss) {
            const css = document.createElement('style');
            css.textContent =
                '@keyframes fscPulse{0%,100%{opacity:1}50%{opacity:0.25}}' +
                '@keyframes fscFadeIn{from{opacity:0}to{opacity:1}}';
            document.head.appendChild(css);
            window.fscOverlayCss = true;
        }

        const r = this._rectFn();
        const w = r.width || window.innerWidth;
        const h = r.height || window.innerHeight;
        const accent = spec.accent;
        const font = 'font-family:"Segoe UI",Roboto,Arial,sans-serif;';
        const weave = 'rgba(' + spec.line + ',' + Overlay.WEAVE_ALPHA + ')';

        const el = document.createElement('div');
        el.setAttribute('style',
            'position:fixed;z-index:2147483647;box-sizing:border-box;overflow:hidden;' +
            'left:' + (r.width ? r.left : 0) + 'px;top:' + (r.height ? r.top : 0) + 'px;' +
            'width:' + w + 'px;height:' + h + 'px;' +
            'background-color:' + spec.veil + ';' +
            // A quiet diagonal weave reads as "disabled" at any panel size. Built
            // as a tiny tile repeated by background-size, NOT one repeating
            // gradient: Coherent evaluates gradients in reduced precision, and a
            // few-px period across a 7410px-wide document degenerates into huge
            // smeared bands.
            'background-image:linear-gradient(45deg,' + weave + ' 25%,transparent 25%,' +
            'transparent 50%,' + weave + ' 50%,' + weave + ' 75%,transparent 75%,transparent);' +
            'background-size:' + Overlay.WEAVE_TILE_PX + 'px ' + Overlay.WEAVE_TILE_PX + 'px;' +
            'animation:fscFadeIn 200ms ease-out;' +
            // The warning must not eat the pilot's clicks; the lock exists to.
            (spec.block ? '' : 'pointer-events:none;'));

        // The card: title row (status dot, wordmark, state), description beneath.
        // Opaque so it reads over any display content.
        const compact = h < 140 || w < 320;
        const card = document.createElement('div');
        card.setAttribute('style',
            'position:absolute;left:50%;top:50%;transform:translate(-50%,-50%);' +
            'display:flex;flex-direction:column;align-items:center;' +
            'max-width:' + Math.min(Math.round(w * 0.9), 540) + 'px;' +
            'padding:' + (compact ? '7px 14px' : '13px 22px') + ';border-radius:10px;' +
            'background:rgb(' + spec.card + ');');

        const title = document.createElement('div');
        title.setAttribute('style', 'display:flex;align-items:center;white-space:nowrap;');

        const dot = document.createElement('div');
        dot.setAttribute('style',
            'width:' + (compact ? 6 : 8) + 'px;height:' + (compact ? 6 : 8) + 'px;border-radius:50%;' +
            'margin-right:' + (compact ? 8 : 11) + 'px;background:rgb(' + accent + ');' +
            'box-shadow:0 0 ' + (compact ? 6 : 10) + 'px rgba(' + accent + ',0.9);' +
            // A live app renewing the lock earns a heartbeat; a dead link holds still.
            (spec.block ? 'animation:fscPulse 1.6s ease-in-out infinite;' : ''));
        title.appendChild(dot);

        const brand = document.createElement('span');
        brand.textContent = 'FS COPILOT';
        brand.setAttribute('style',
            'color:rgba(255,255,255,0.85);' + font +
            'font-size:' + (compact ? 10 : 12) + 'px;font-weight:600;letter-spacing:0.14em;');
        title.appendChild(brand);

        const stat = document.createElement('span');
        stat.textContent = spec.title;
        stat.setAttribute('style',
            'color:rgb(' + accent + ');' + font +
            'font-size:' + (compact ? 10 : 12) + 'px;font-weight:600;letter-spacing:0.14em;' +
            'margin-left:' + (compact ? 8 : 11) + 'px;padding-left:' + (compact ? 8 : 11) + 'px;' +
            'border-left:1px solid rgba(255,255,255,0.14);');
        title.appendChild(stat);

        card.appendChild(title);

        // Tiny panels (the FCP windows are 460x102) have no room for prose.
        if (!compact) {
            const desc = document.createElement('div');
            desc.textContent = spec.desc;
            desc.setAttribute('style',
                'margin-top:8px;max-width:' + Math.min(Math.round(w * 0.85), 500) + 'px;' +
                'color:rgba(255,255,255,0.6);' + font +
                'font-size:14px;font-weight:400;line-height:1.45;text-align:center;');
            card.appendChild(desc);
        }

        el.appendChild(card);
        document.body.appendChild(el);
        this._el = el;
    }

    remove() {
        if (!this._el) return;
        try { this._el.parentNode.removeChild(this._el); } catch (e) { /* already gone */ }
        this._el = null;
    }

    has(name) { return Object.prototype.hasOwnProperty.call(Overlay.STATES, name); }

    /* Which state is on screen, or null. Lets policy retract one specific overlay
     * without stepping on a different one that replaced it meanwhile. */
    showing() { return this._el ? this._name : null; }
}

/* The overlay states. block also decides the heartbeat: a live app renewing a
 * lock pulses; a broken chain holds still and must never block. */
Overlay.STATES = {
    connecting: {
        block: true, accent: '96,165,250', line: '38,80,140', card: '12,17,27', veil: 'rgba(9,14,24,0.42)',
        title: 'CONNECTING',
        desc: 'Input is paused to prevent desync before the session is live.'
    },
    degraded: {
        block: true, accent: '250,220,40', line: '100,84,14', card: '24,21,8', veil: 'rgba(24,20,4,0.40)',
        title: 'SYNC DEGRADED',
        // Names the way out: a crash on the other side lands here too, and the pilot
        // should not need to know the lock rule to escape it.
        desc: 'The connection to the other pilot dropped; this panel is paused to prevent desync. ' +
            'Take control to keep flying, or wait for the connection to return.'
    },
    lost: {
        block: false, accent: '248,113,113', line: '134,52,52', card: '27,12,12', veil: 'rgba(26,10,11,0.34)',
        title: 'SYNC BROKEN',
        desc: 'FS Copilot\'s internal chain broke. This panel cannot be synced.'
    }
};

Overlay.WEAVE_ALPHA = 0.3;   // presence of the diagonal weave
Overlay.WEAVE_TILE_PX = 8;   // weave tile; small on purpose - see apply()
