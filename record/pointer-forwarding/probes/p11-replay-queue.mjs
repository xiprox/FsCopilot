// p11: does pointer.js's replay queue do what 12-pre-pr-review R06/R07/R17 decided?
// Runs under Node against a stub DOM; no simulator, no panel to select.
// Exercises pointer.js's replay queue under a stub DOM. Not the sim: it checks the
// queue's own logic - order, the remaining-gap wait, a lone tap staying synchronous
// and showing nothing, the deadman releasing a stalled gesture, the capture guards.
// Usage: npm run probe:11 -- <dir containing pointer.js>   (or node probes/p11-replay-queue.mjs <dir>)
import fs from 'node:fs';
import vm from 'node:vm';
import path from 'node:path';
const dir = process.argv[2];
const events = [];
const instrument = { getBoundingClientRect: () => ({left: 0, top: 0, width: 1000, height: 500}), contains: (el) => el !== null && el !== undefined && el.outside !== true };
const target = { dispatchEvent: (ev) => { events.push(ev.type + '@' + ev.clientX + ',' + ev.clientY); return true; } };
let overlayStates = [];
class Overlay {
    constructor() { this._name = null; this._el = null; }
    apply(n) { if (this._el && this._name === n) return; this._name = n; this._el = {}; overlayStates.push('+' + n); }
    remove() { if (!this._el) return; overlayStates.push('-' + this._name); this._el = null; this._name = null; }
    has(n) { return true; }
    showing() { return this._el ? this._name : null; }
    element() { return this._el; }
    hitTest(x, y) { return target; }
}
const ctx = { console, setTimeout, clearTimeout, setInterval, clearInterval, Date, Math, Object,
    window: {}, document: { addEventListener() {}, removeEventListener() {}, elementFromPoint: () => target }, Overlay,
    MouseEvent: class { constructor(type, init) { this.type = type; Object.assign(this, init); } } };
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(path.join(dir, 'pointer.js'), 'utf8'), ctx);
const Pointer = vm.runInContext('Pointer', ctx);
const p = new Pointer(instrument, 'K');
p.updateState('live', 'master');
overlayStates = []; // ignore the boot lock
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
(async () => {
    // 1. A lone tap: synchronous, no overlay.
    p.replay({k: 'press', nx: 0.1, ny: 0.1, hold: 0, gap: 0});
    console.log('1 lone tap events:', events.join(' '), '| overlay:', overlayStates.join(' ') || '(none)');
    events.length = 0; overlayStates = [];

    // 2. A burst: hold 100, then a tap with gap 300, then a tap with gap 0. Order and
    // pacing: the second waits ~300 after the first finishes, the third runs at once.
    const t0 = Date.now();
    const stamps = [];
    const origFire = p._fire.bind(p);
    p._fire = (t, type, x, y, b, btn) => { stamps.push(type + '@' + x + '+' + (Date.now() - t0)); return origFire(t, type, x, y, b, btn); };
    p.replay({k: 'press', nx: 0.1, ny: 0.1, hold: 100, gap: 0});
    p.replay({k: 'press', nx: 0.2, ny: 0.1, hold: 0, gap: 300});
    p.replay({k: 'press', nx: 0.3, ny: 0.1, hold: 0, gap: 0});
    console.log('2 burst overlay after enqueue:', overlayStates.join(' '), '| queued:', p.stats().queued, 'busy:', p._busy);
    await sleep(700);
    console.log('2 burst timeline (ms from enqueue):', stamps.join(' '));
    console.log('2 burst overlay trail:', overlayStates.join(' '), '| stats:', JSON.stringify(p.stats()));
    overlayStates = []; stamps.length = 0;

    // 3. A live gesture: on the sender the gap before it was 1000 (capped) and at least
    // that long has passed here since the previous replay, so it waits nothing.
    await sleep(1100);
    events.length = 0;
    p.replay({k: 'press', nx: 0.4, ny: 0.1, hold: 0, gap: 1000});
    console.log('3 live tap dispatched synchronously:', events.length === 3, '| overlay:', overlayStates.join(' ') || '(none)');
    overlayStates = [];

    // 4. A stalled drag: pretend a 50ms drag whose timers never complete. The deadman
    // must release the queue and the tap behind it must then run.
    p._replayDrag = (msg, done) => 50;
    events.length = 0;
    p.replay({k: 'drag', path: [[0, 0.1, 0.1], [50, 0.2, 0.2]], gap: 0});
    p.replay({k: 'press', nx: 0.5, ny: 0.1, hold: 0, gap: 0});
    console.log('4 stalled: busy', p._busy, 'queued', p.stats().queued, '| overlay:', overlayStates.join(' '));
    await sleep(50 + Pointer.REPLAY_DEADMAN_MS + 300);
    console.log('4 after deadman: busy', p._busy, 'queued', p.stats().queued, 'stalled', p.stats().stalled,
        '| tap behind it ran:', events.join(' '), '| overlay trail:', overlayStates.join(' '));
    overlayStates = [];

    // 5. Capture side: isTrusted false is an echo; a real press is captured with its gap;
    // a press on a target outside the instrument is rejected as outside.
    const out = []; p.onCapture(m => out.push(m));
    p._onDown({isTrusted: false, clientX: 10, clientY: 10, button: 0, target: {nodeType: 1}});
    p._onDown({isTrusted: true, clientX: 100, clientY: 50, button: 0, target: {nodeType: 1}});
    p._onUp({isTrusted: true, clientX: 100, clientY: 50, button: 0, target: {nodeType: 1}});
    await sleep(120);
    p._onDown({isTrusted: true, clientX: 200, clientY: 50, button: 0, target: {nodeType: 1}});
    p._onUp({isTrusted: true, clientX: 200, clientY: 50, button: 0, target: {nodeType: 1}});
    p._onDown({isTrusted: true, clientX: 300, clientY: 50, button: 0, target: {nodeType: 1, outside: true}});
    p._onUp({isTrusted: true, clientX: 300, clientY: 50, button: 0, target: {nodeType: 1, outside: true}});
    console.log('5 captured:', out.map(m => m.k + ' gap=' + m.gap + ' nx=' + m.nx).join('; '), '| stats:', JSON.stringify(p.stats()));

    // 6. Fail-open: a replay finishing after the app link closed must not re-apply a
    // session lock. Session says degraded/slave, but the state is stale.
    p.updateState('degraded', 'slave');
    overlayStates = [];
    p.linkClosed(true);
    p.replay({k: 'press', nx: 0.1, ny: 0.1, hold: 60, gap: 0});
    await sleep(200);
    console.log('6 after link closed, overlay trail:', overlayStates.join(' ') || '(none)', '| showing:', p._overlay.showing());
    p.stop();
})();
