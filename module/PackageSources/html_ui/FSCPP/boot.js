/*
 * FSCPP boot — the twenty lines that join the agent to the transport.
 *
 * This is the file that exists so agent.js does not have to know anything about
 * how messages travel. Everything here is plumbing; nothing here is mechanism.
 * Porting to a different transport means rewriting this file and link.js and
 * leaving agent.js alone.
 *
 * VCockpit.js calls FSCPP_boot(template) once per instrument, either directly or
 * from its pending queue if the imports had not resolved yet.
 *
 * Chrome 49. No optional chaining, no ??, no class fields.
 */

window.FSCPP_HOST = window.FSCPP_HOST || "ws://127.0.0.1:9020/";

window.FSCPP_boot = function (instrument) {
  // One agent per document. A panel with several instruments — the A350 has one —
  // would otherwise install several, each capturing the same clicks, and one press
  // would be sent once per instrument.
  if (window.FSCPP_booted) {
    console.log("[FSCPP] already booted in this document, ignoring " +
      (instrument && instrument.instrumentIdentifier))
    return window.FSCPP
  }
  window.FSCPP_booted = true;

  var agent = window.FSCPP_Agent(instrument);
  var link = window.FSCPP_Link(window.FSCPP_HOST, { name: agent.key });
  window.FSCPP_link = link;

  // Outbound: whatever the pilot did.
  agent.onCapture(function (msg) {
    link.send({ t: "interact", from: agent.key, msg: msg });
  });

  // Inbound: whatever the peer did. The host is responsible for not echoing a
  // message back to the panel it came from; the agent's own loop breaking is the
  // second line of defence rather than the first.
  link.onMessage(function (m) {
    if (!m || m.t !== "interact" || !m.msg) return;
    agent.replay(m.msg);
  });

  console.log("[FSCPP] booted " + agent.key + " -> " + window.FSCPP_HOST);
  return agent;
};
