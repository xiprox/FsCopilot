# Findings

Newest first. Each entry names the question it moves, what was actually observed, and what
it changes. Raw output lives in `results/`.

---

## 2026-08-30 · Q00 — the debugger port answers nothing

**Status: still open, one hypothesis left to test.**

Ran `probe:00` against `127.0.0.1:19999` with MSFS 2024 in a flight and the Coherent GT
Debugger window open. Raw output: `results/p00-debugger-2026-08-30-09-44-57.txt`.

- Something *is* listening: `0.0.0.0:19999`, pid 33168. The process name could not be read
  back through either `Get-Process` or `Get-CimInstance`, which usually means elevation.
- TCP connects succeed.
- It answers nothing. Not to `GET /`, not to any of eleven paths including the CDP
  (`/json`, `/json/list`) and WebKit Inspector shapes, not to a WebSocket upgrade on six
  paths, not to a bare CRLF, a JSON-RPC line, or a `COHERENT` token.
- It does not close the connection either. It holds the socket open and idle — verified out
  to 12 seconds, well past any plausible response latency.
- MSFS's own listeners at the time were `49266`, `127.0.0.1:50153` and `::1:50154`. 49266
  had gone by the follow-up run; 50153/50154 look like SimConnect's auto-assigned pair.

**Reading it.** Accepting a connection and then holding it silent is not the behaviour of an
HTTP server that dislikes your path — that returns 400 or 404. Two explanations survive:

1. **One client at a time.** The debugger UI was connected throughout. If the backend serves
   a single session, our connections were accepted into a queue and starved. This is the
   cheap one to eliminate: **close the Coherent GT Debugger window entirely and re-run
   `npm run probe:00`.** A response of any kind means automation is on the table.
2. **A framed protocol we have not guessed** — length-prefixed rather than newline- or
   HTTP-delimited, so the server is still waiting for a complete frame that never arrives.

Worth exactly one more attempt. If closing the debugger changes nothing, take the manual
console loop and stop paying attention to this — every probe still works pasted by hand, it
just costs a round trip each.
