// p13: does the replay interlock block without painting, and speak only once it has
// outlasted a gesture?
// Runs under Node against a stub DOM; no simulator, no panel to select. Unlike p11,
// which stubs Overlay away to get at the queue, this one loads the real overlay.js:
// the rendering branch is half of what is under test.
// REPLAY_NOTICE_MS is overridden to 400ms so the run takes two seconds instead of
// half a minute. The constant itself is not what this checks - the escalation is.
// Usage: npm run probe:13 -- <dir containing overlay.js and pointer.js>
import fs from 'node:fs';
import vm from 'node:vm';
import path from 'node:path';

const dir = process.argv[2];
if (!dir) { console.error('usage: node probes/p13-overlay-silence.mjs <dir>'); process.exit(2); }

function mk(tag) {
    return {
        tag, children: [], style: {}, attrs: {}, nodeType: 1, parentNode: null,
        textContent: '', styleText: '',
        setAttribute(k, v) { this.attrs[k] = v; if (k === 'style') this.styleText = v; },
        appendChild(c) { c.parentNode = this; this.children.push(c); return c; },
        removeChild(c) { this.children = this.children.filter(x => x !== c); c.parentNode = null; },
        dispatchEvent() { return true; },
        contains(o) { if (o === this) return true; return this.children.some(c => c.contains && c.contains(o)); }
    };
}

const body = mk('body'), head = mk('head'), panelTarget = mk('div');
const document = {
    createElement: mk, body, head,
    addEventListener() {}, removeEventListener() {},
    elementFromPoint() { return panelTarget; }
};
const window = { innerWidth: 1920, innerHeight: 1080 };
const ctx = {
    document, window, console, setTimeout, clearTimeout, setInterval, clearInterval, Date, Math, Object,
    MouseEvent: class { constructor(type, init) { this.type = type; Object.assign(this, init); } }
};
ctx.window.document = document;
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(path.join(dir, 'overlay.js'), 'utf8'), ctx);
vm.runInContext(fs.readFileSync(path.join(dir, 'pointer.js'), 'utf8'), ctx);
const Pointer = vm.runInContext('Pointer', ctx);

const NOTICE = 400;
Pointer.REPLAY_NOTICE_MS = NOTICE;

const RECT = {left: 0, top: 0, width: 800, height: 600};
const instrument = mk('div');
instrument.getBoundingClientRect = () => RECT;
const p = new Pointer(instrument, 'PFD');
p.updateState('live', 'master');

// silent = a node is there, covering the rect, drawing nothing.
function state() {
    const e = p._overlay.element();
    if (!e) return 'none';
    const painted = /background/.test(e.styleText || '') || e.children.length > 0;
    return painted ? 'visible' : 'silent';
}
function covers() {
    const e = p._overlay.element();
    if (!e) return false;
    return e.styleText.indexOf('width:' + RECT.width + 'px') >= 0 &&
           e.styleText.indexOf('height:' + RECT.height + 'px') >= 0;
}

const rows = [];
const check = (label, got, want) => rows.push([String(got) === String(want) ? 'ok  ' : 'FAIL', label, got, want]);
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const press = (hold) => p.replay({v: 5, k: 'press', key: 'PFD', nx: 0.5, ny: 0.5, hold, gap: 0, button: 0});

(async () => {
    // 1. A lone tap runs synchronously and was already silent before this change.
    press(0);
    check('lone tap: nothing at all', state(), 'none');

    // 2. A held press is the common case the veil used to flash on.
    press(250);
    check('hold: blocks', state() !== 'none', true);
    check('hold: paints nothing', state(), 'silent');
    check('hold: covers the instrument', covers(), true);
    const locked = p.stats().locked;
    check('hold: a real press on it counts locked',
        p._inside({target: p._overlay.element()}, {nx: 0.5, ny: 0.5}), false);
    check('hold: locked counter moved', p.stats().locked, locked + 1);
    await sleep(NOTICE);
    check('hold: gone once drained', state(), 'none');

    // 3. A drag held past the threshold: silent under it, visible over it.
    const pts = [];
    for (let i = 0; i < 12; i++) pts.push([i * 60, 0.3 + i * 0.02, 0.5]);
    p.replay({v: 5, k: 'drag', key: 'PFD', path: pts, gap: 0, button: 0});
    check('drag: silent at start', state(), 'silent');
    await sleep(NOTICE - 100);
    check('drag: still silent under the threshold', state(), 'silent');
    await sleep(200);
    check('drag: visible over the threshold', state(), 'visible');
    check('drag: the visible one is REPLAYING', p._overlay.showing(), 'replaying');

    await sleep(500);
    check('drag: gone once drained', state(), 'none');
    check('notice timer released', p._noticeTimer, null);
    check('block start cleared', p._replayStart, 0);

    // 4. The next gesture starts silent again rather than inheriting the escalation.
    press(250);
    check('next hold: silent again', state(), 'silent');
    p.stop();
    check('stop(): overlay removed', state(), 'none');

    rows.forEach(r => console.log(r[0], '|', r[1], '| got:', r[2], '| want:', r[3]));
    const failed = rows.filter(r => r[0] === 'FAIL').length;
    console.log(failed ? '\n' + failed + ' FAILED' : '\nall ' + rows.length + ' passed');
    process.exit(failed ? 1 : 0);
})();
