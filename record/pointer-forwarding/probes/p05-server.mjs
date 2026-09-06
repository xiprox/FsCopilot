/*
 * P05 server — a loopback endpoint for the panel to try to reach.
 *
 * Half of Q08. Serves both plain HTTP and WebSocket on one port so the console
 * probe can tell the difference between "loopback is blocked entirely" and
 * "HTTP works but WebSocket does not" — which are different answers with
 * different consequences for docs/04-transport.md.
 *
 *   npm run probe:05-server
 *   npm run probe:05-server -- --port 9002
 *
 * Then paste probes/p05-websocket.js into the Coherent GT debugger console.
 *
 * Deliberately dependency-free. Node has no built-in WebSocket *server*, and the
 * point of this probe is partly to find out whether FS Copilot could host one
 * without taking a new package into a PublishTrimmed single-file build — so
 * proving it takes ~60 lines of RFC 6455 is itself part of the answer.
 */

import { createServer } from "node:net"
import { createHash } from "node:crypto"

const GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
const portArg = process.argv.indexOf("--port")
const PORT = portArg >= 0 ? Number(process.argv[portArg + 1]) : 9002

const stamp = () => new Date().toISOString().slice(11, 23)
const log = (...a) => console.log(stamp(), ...a)

/** Server-to-client text frame. Never masked, and short enough here that the
 *  64-bit length case cannot arise. */
function frame(text) {
  const body = Buffer.from(text, "utf8")
  const head = body.length < 126
    ? Buffer.from([0x81, body.length])
    : Buffer.concat([Buffer.from([0x81, 126]), (() => { const b = Buffer.alloc(2); b.writeUInt16BE(body.length); return b })()])
  return Buffer.concat([head, body])
}

/** Pull as many complete client frames as `buf` holds. Returns the leftover. */
function drain(buf, onText, onClose) {
  for (;;) {
    if (buf.length < 2) return buf
    const opcode = buf[0] & 0x0f
    const masked = (buf[1] & 0x80) !== 0
    let len = buf[1] & 0x7f
    let off = 2
    if (len === 126) { if (buf.length < 4) return buf; len = buf.readUInt16BE(2); off = 4 }
    else if (len === 127) { if (buf.length < 10) return buf; len = Number(buf.readBigUInt64BE(2)); off = 10 }
    const need = off + (masked ? 4 : 0) + len
    if (buf.length < need) return buf

    let key = null
    if (masked) { key = buf.subarray(off, off + 4); off += 4 }
    const body = Buffer.from(buf.subarray(off, off + len))
    if (key) for (let i = 0; i < body.length; i++) body[i] ^= key[i % 4]
    buf = buf.subarray(need)

    if (opcode === 0x8) { onClose(); return buf }
    if (opcode === 0x1) onText(body.toString("utf8"))
    // Ping/pong and continuation are not exercised by this probe.
  }
}

let sockets = 0

const server = createServer((sock) => {
  const id = ++sockets
  const peer = `${sock.remoteAddress}:${sock.remotePort}`
  log(`#${id} connect from ${peer}`)

  let head = Buffer.alloc(0)
  let upgraded = false
  let buf = Buffer.alloc(0)

  sock.on("data", (chunk) => {
    if (upgraded) {
      buf = drain(Buffer.concat([buf, chunk]),
        (text) => {
          log(`#${id} recv  ${text}`)
          sock.write(frame(JSON.stringify({ echo: text, at: Date.now() })))
        },
        () => { log(`#${id} close frame`); sock.end() })
      return
    }

    head = Buffer.concat([head, chunk])
    const end = head.indexOf("\r\n\r\n")
    if (end === -1) return

    const request = head.subarray(0, end).toString("latin1")
    const line = request.split("\r\n")[0]
    const headers = {}
    request.split("\r\n").slice(1).forEach((h) => {
      const i = h.indexOf(":")
      if (i > 0) headers[h.slice(0, i).trim().toLowerCase()] = h.slice(i + 1).trim()
    })
    log(`#${id} ${line}`)

    const key = headers["sec-websocket-key"]
    const wantsWs = String(headers["upgrade"] || "").toLowerCase() === "websocket" && key

    if (wantsWs) {
      const accept = createHash("sha1").update(key + GUID).digest("base64")
      sock.write(
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        `Sec-WebSocket-Accept: ${accept}\r\n\r\n`
      )
      upgraded = true
      buf = head.subarray(end + 4)
      log(`#${id} UPGRADED — a cockpit document opened a WebSocket to loopback`)
      sock.write(frame(JSON.stringify({ hello: "fsc-pointer-playground", probe: "p05" })))
      return
    }

    // Plain HTTP, with permissive CORS so a coui:// origin is not refused for a
    // reason unrelated to what we are testing.
    const body = JSON.stringify({ ok: true, probe: "p05", at: Date.now() })
    sock.end(
      "HTTP/1.1 200 OK\r\n" +
      "Content-Type: application/json\r\n" +
      `Content-Length: ${Buffer.byteLength(body)}\r\n` +
      "Access-Control-Allow-Origin: *\r\n" +
      "Connection: close\r\n\r\n" + body
    )
    log(`#${id} served plain HTTP`)
  })

  sock.on("error", (e) => log(`#${id} error ${e.code || e.message}`))
  sock.on("close", () => log(`#${id} closed`))
})

server.on("error", (e) => {
  if (e.code === "EADDRINUSE") {
    console.error(`\nPort ${PORT} is already in use. Pick another with --port, and check it is`)
    console.error(`not an orphaned socket — that has already cost this project one probe.\n`)
    process.exit(1)
  }
  throw e
})

server.listen(PORT, "127.0.0.1", () => {
  log(`listening on 127.0.0.1:${PORT}  (HTTP and WebSocket on the same port)`)
  console.log()
  console.log("Now paste probes/p05-websocket.js into the Coherent GT debugger console.")
  console.log("Watch this window: an UPGRADED line is Q08 answered yes.")
  console.log("Ctrl-C to stop.")
})
