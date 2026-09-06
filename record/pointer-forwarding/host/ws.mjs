/*
 * A minimal WebSocket server, dependency-free.
 *
 * Extracted from probes/p05-server.mjs, which proved the mechanism. It stays
 * dependency-free deliberately: FS Copilot publishes as a trimmed single file, so
 * the fact that hosting a socket costs ~120 lines of node:net and node:crypto —
 * rather than a package — is part of the argument for this transport.
 *
 * Text frames only. No ping/pong, no continuation, no permessage-deflate. That is
 * enough for JSON messages a few hundred bytes long, which is all this carries.
 *
 *   const server = wsServer({ port, onConnection })
 *   onConnection(sock)   sock.send(obj) / sock.on(fn) / sock.close() / sock.id
 */

import { createServer } from "node:net"
import { createHash } from "node:crypto"

const GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

function frame(text) {
  const body = Buffer.from(text, "utf8")
  let head
  if (body.length < 126) {
    head = Buffer.from([0x81, body.length])
  } else if (body.length < 65536) {
    head = Buffer.alloc(4)
    head[0] = 0x81; head[1] = 126; head.writeUInt16BE(body.length, 2)
  } else {
    head = Buffer.alloc(10)
    head[0] = 0x81; head[1] = 127; head.writeBigUInt64BE(BigInt(body.length), 2)
  }
  return Buffer.concat([head, body])
}

/** Pull complete client frames out of `buf`; returns the unconsumed remainder. */
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
  }
}

export function wsServer({ port, host = "127.0.0.1", onConnection, onHttp }) {
  let nextId = 0

  const server = createServer((sock) => {
    const id = ++nextId
    let head = Buffer.alloc(0)
    let upgraded = false
    let buf = Buffer.alloc(0)
    const handlers = []
    let closeHandler = null

    const api = {
      id,
      name: null,
      send(obj) {
        if (!upgraded || sock.destroyed) return false
        try { sock.write(frame(typeof obj === "string" ? obj : JSON.stringify(obj))); return true }
        catch { return false }
      },
      on(fn) { handlers.push(fn) },
      onClose(fn) { closeHandler = fn },
      close() { try { sock.end() } catch { /* already gone */ } }
    }

    sock.on("data", (chunk) => {
      if (upgraded) {
        buf = drain(Buffer.concat([buf, chunk]),
          (text) => {
            let msg
            try { msg = JSON.parse(text) } catch { return }
            for (const fn of handlers) {
              try { fn(msg) } catch (e) { console.error("handler threw", e) }
            }
          },
          () => sock.end())
        return
      }

      head = Buffer.concat([head, chunk])
      const end = head.indexOf("\r\n\r\n")
      if (end === -1) return

      const request = head.subarray(0, end).toString("latin1")
      const headers = {}
      request.split("\r\n").slice(1).forEach((h) => {
        const i = h.indexOf(":")
        if (i > 0) headers[h.slice(0, i).trim().toLowerCase()] = h.slice(i + 1).trim()
      })

      const key = headers["sec-websocket-key"]
      if (String(headers["upgrade"] || "").toLowerCase() === "websocket" && key) {
        const accept = createHash("sha1").update(key + GUID).digest("base64")
        sock.write(
          "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n" +
          `Connection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`)
        upgraded = true
        buf = head.subarray(end + 4)
        if (onConnection) onConnection(api)
        return
      }

      const body = onHttp ? onHttp(request.split("\r\n")[0]) : JSON.stringify({ ok: true })
      sock.end(
        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
        `Content-Length: ${Buffer.byteLength(body)}\r\n` +
        "Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n" + body)
    })

    sock.on("error", () => { /* a panel reload closes rudely; not worth reporting */ })
    sock.on("close", () => { if (closeHandler) closeHandler() })
  })

  server.on("error", (e) => {
    if (e.code === "EADDRINUSE") {
      console.error(`\nPort ${port} is in use. Check the owning process is alive — an orphaned`)
      console.error(`socket has cost this project a probe before.\n`)
      process.exit(1)
    }
    throw e
  })

  server.listen(port, host)
  return server
}
