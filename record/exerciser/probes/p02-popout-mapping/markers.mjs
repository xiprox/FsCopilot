/*
 * node markers.mjs on|off [page]
 *
 * Places five 40 px coloured squares at known fractions of the instrument's bounding rect,
 * the same rect pointer.js normalises against, or removes them. capture.ps1 finds them in a
 * pop-out capture. The squares are pointer-events:none and live in #fscProbeMarkers.
 */

import { listPages, selectPage, Inspector } from "../../../pointer-forwarding/probes/lib/inspector.mjs"

// Order and colours match Locate() in capture.ps1.
const MARKS = [[0.02, 0.05, "#ff00ff"], [0.10, 0.20, "#00ff00"], [0.50, 0.50, "#ff0000"], [0.90, 0.80, "#0000ff"], [0.98, 0.95, "#ffff00"]]

const on = process.argv[2] === "on"
const insp = await new Inspector(selectPage(await listPages(), process.argv[3] || "DisplayUnits").id).open()
const out = await insp.evaluate(`(function(){
  var old = document.getElementById('fscProbeMarkers'); if (old) old.parentNode.removeChild(old);
  if (!${on}) return 'removed';
  var inst = document.querySelector('vcockpit-panel > *'); var r = inst.getBoundingClientRect();
  var box = document.createElement('div'); box.id = 'fscProbeMarkers';
  box.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;pointer-events:none';
  ${JSON.stringify(MARKS)}.forEach(function(m){
    var d = document.createElement('div'); var s = 40;
    d.style.cssText = 'position:fixed;width:'+s+'px;height:'+s+'px;background:'+m[2]+';left:'+(r.left + m[0]*r.width - s/2)+'px;top:'+(r.top + m[1]*r.height - s/2)+'px';
    box.appendChild(d);
  });
  document.body.appendChild(box);
  return 'placed in rect ' + [r.left, r.top, r.width, r.height].join(',');
})()`)
console.log(out)
insp.close()
