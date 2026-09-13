/*
 * Draws the pointer-sync overlay. Pointer decides which state shows and when.
 *
 * Blocking and painting are separate: a silent state stops the same clicks over
 * the same rect and draws nothing.
 *
 * Coherent GT is Chrome 49: no optional chaining, no ??, no class fields.
 */
class Overlay {
    /* Read on every apply: before layout the rect is zero and the viewport is used. */
    constructor(rectFn) {
        this._rectFn = rectFn;
        this._el = null;
        this._name = null;
    }

    /* Renewals arrive every 2 s; rebuilding the same state would restart its animations. */
    apply(name) {
        if (this._el && this._name === name) return;
        this._name = name;
        const spec = Overlay.STATES[name];
        this.remove();

        const r = this._rectFn();
        const w = r.width || window.innerWidth;
        const h = r.height || window.innerHeight;
        const box =
            'position:fixed;z-index:2147483647;box-sizing:border-box;overflow:hidden;' +
            'left:' + (r.width ? r.left : 0) + 'px;top:' + (r.height ? r.top : 0) + 'px;' +
            'width:' + w + 'px;height:' + h + 'px;';

        // No paint, so no fade-in and no stylesheet either.
        if (spec.silent) {
            const bare = document.createElement('div');
            bare.setAttribute('style', box);
            document.body.appendChild(bare);
            this._el = bare;
            return;
        }

        if (!window.fscOverlayCss) {
            const css = document.createElement('style');
            css.textContent =
                '@keyframes fscPulse{0%,100%{opacity:1}50%{opacity:0.25}}' +
                '@keyframes fscFadeIn{from{opacity:0}to{opacity:1}}';
            document.head.appendChild(css);
            window.fscOverlayCss = true;
        }

        const accent = spec.accent;
        const font = 'font-family:"Segoe UI",Roboto,Arial,sans-serif;';
        const weave = 'rgba(' + spec.line + ',' + Overlay.WEAVE_ALPHA + ')';

        const el = document.createElement('div');
        el.setAttribute('style',
            box +
            'background-color:' + spec.veil + ';' +
            // A small tile, not one repeating gradient: Coherent computes gradients at low
            // precision, and a few-px period across a wide document smears into bands.
            'background-image:linear-gradient(45deg,' + weave + ' 25%,transparent 25%,' +
            'transparent 50%,' + weave + ' 50%,' + weave + ' 75%,transparent 75%,transparent);' +
            'background-size:' + Overlay.WEAVE_TILE_PX + 'px ' + Overlay.WEAVE_TILE_PX + 'px;' +
            'animation:fscFadeIn 200ms ease-out;' +
            // A non-blocking state must not eat clicks.
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

    /* A blocking overlay is the topmost node, so it steps aside for elementFromPoint. */
    hitTest(x, y) {
        const el = this._el;
        if (!el) return document.elementFromPoint(x, y);
        const prev = el.style.pointerEvents;
        el.style.pointerEvents = 'none';
        try { return document.elementFromPoint(x, y); }
        finally { el.style.pointerEvents = prev; }
    }

    showing() { return this._el ? this._name : null; }

    element() { return this._el; }
}

Overlay.STATES = {
    connecting: {
        block: true, accent: '96,165,250', line: '38,80,140', card: '12,17,27', veil: 'rgba(9,14,24,0.42)',
        title: 'CONNECTING',
        desc: 'Input is paused to prevent desync before sync is live.'
    },
    degraded: {
        block: true, accent: '250,220,40', line: '100,84,14', card: '24,21,8', veil: 'rgba(24,20,4,0.40)',
        title: 'SYNC DEGRADED',
        // A crash on the other side also lands here, so the card names the way out.
        desc: 'The connection to the other pilot dropped; this panel is paused to prevent desync. ' +
            'Take control to keep flying, or wait for the connection to return.'
    },
    /* Almost every replay: blocks, shows nothing. Policy swaps in the one below. */
    replayingQuiet: {block: true, silent: true},
    replaying: {
        block: true, accent: '94,234,212', line: '18,84,76', card: '8,24,22', veil: 'rgba(6,20,18,0.40)',
        title: 'REPLAYING',
        desc: 'Applying the other pilot\'s inputs. Input is paused until this panel has caught up.'
    },
    lost: {
        block: false, accent: '248,113,113', line: '134,52,52', card: '27,12,12', veil: 'rgba(26,10,11,0.34)',
        title: 'SYNC BROKEN',
        desc: 'FS Copilot\'s internal chain broke. This panel cannot be synced.'
    }
};

Overlay.WEAVE_ALPHA = 0.3;   // presence of the diagonal weave
Overlay.WEAVE_TILE_PX = 8;   // weave tile; small on purpose - see apply()
