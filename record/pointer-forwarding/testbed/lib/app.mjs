/*
 * One FS Copilot instance, started and driven by the bench.
 *
 * Two instances run on one machine: PanelServer already scans 9020-9024 and takes
 * the first free port, and the peer socket takes an ephemeral one, so nothing
 * collides. Each gets its own working directory because Serilog writes `log`
 * relative to the current directory and two instances sharing one log file is a
 * transcript nobody can read. Definitions resolve against AppContext.BaseDirectory,
 * so moving the working directory costs nothing.
 *
 * Actions the pilot takes with a mouse — join, leave, quit — go over the control
 * channel that --bench opens (see FsCopilot/Connection/BenchControl.cs). Actions
 * the world takes go to the process:
 *
 *   quit()      the clean exit path. Panels are told {t:"bye"} and the peer is told
 *               the app left, so a peer that quit is distinguishable from one lost.
 *   crash()     taskkill /F. No goodbye, no disconnect: what a power cut looks like.
 *   suspend()   the process stops answering keepalives while its sockets stay open.
 *               The peer link times out after DisconnectTimeout with nothing closed
 *               at either end, which is the shape of a network that went away, and
 *               resume() brings the same process back rather than a new one.
 */

import { spawn, execFile } from "node:child_process"
import { once } from "node:events"
import { createConnection } from "node:net"
import { mkdirSync, openSync, readSync, closeSync, statSync, existsSync } from "node:fs"
import { join } from "node:path"

import { now } from "./panel.mjs"

export class App {
  constructor({ name, exe, dir, peerId, relay, benchPort }) {
    this.name = name
    this.exe = exe
    this.dir = dir
    this.peerId = peerId
    this.relay = relay
    this.benchPort = benchPort
    this.lines = []
    this.port = null          // the panel port this instance won
    this.proc = null
    this.suspended = false
    this._sock = null
    this._nextId = 1
    this._pending = new Map()
    this._tail = null
  }

  async launch() {
    mkdirSync(this.dir, { recursive: true })
    const args = ["--skip-install", "--bench", String(this.benchPort), "--peer-id", this.peerId]
    if (this.relay) args.push("--relay", this.relay)

    this.proc = spawn(this.exe, args, { cwd: this.dir, windowsHide: true, stdio: "ignore" })
    this.proc.on("exit", (code) => {
      this.exited = { at: now(), code }
    })

    this._tailLog()
    // The port line is the first thing worth waiting for: until PanelServer is
    // listening there is nothing for a panel to attach to.
    const m = await this.waitForLog(/\[PanelServer\] Listening on 127\.0\.0\.1:(\d+)/, { timeout: 30000 })
    this.port = Number(m[1])
    await this._connectControl()
    return this
  }

  /* ---- control channel ---- */

  async _connectControl() {
    const deadline = Date.now() + 15000
    for (;;) {
      try {
        this._sock = await new Promise((resolve, reject) => {
          const s = createConnection({ host: "127.0.0.1", port: this.benchPort })
          s.once("connect", () => resolve(s))
          s.once("error", reject)
        })
        break
      } catch (e) {
        if (Date.now() > deadline) throw new Error(`${this.name}: no control channel on ${this.benchPort}: ${e.message}`)
        await sleep(200)
      }
    }

    let buffer = ""
    this._sock.setEncoding("utf8")
    this._sock.on("data", (chunk) => {
      buffer += chunk
      let i
      while ((i = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, i).trim()
        buffer = buffer.slice(i + 1)
        if (!line) continue
        let reply
        try { reply = JSON.parse(line) } catch { continue }
        const waiter = this._pending.get(reply.id)
        if (!waiter) continue
        this._pending.delete(reply.id)
        reply.ok ? waiter.resolve(reply) : waiter.reject(new Error(`${this.name}: ${reply.error}`))
      }
    })
    this._sock.on("close", () => {
      for (const w of this._pending.values()) w.reject(new Error(`${this.name}: control channel closed`))
      this._pending.clear()
    })
  }

  control(command, extra = {}, { timeout = 20000 } = {}) {
    if (!this._sock || this._sock.destroyed) return Promise.reject(new Error(`${this.name}: no control channel`))
    const id = this._nextId++
    const line = JSON.stringify({ id, c: command, ...extra })
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this._pending.delete(id)
        reject(new Error(`${this.name}: '${command}' did not answer in ${timeout}ms`))
      }, timeout)
      this._pending.set(id, {
        resolve: (r) => { clearTimeout(timer); resolve(r) },
        reject: (e) => { clearTimeout(timer); reject(e) }
      })
      this._sock.write(line + "\n")
    })
  }

  /** The pointer: opt-in list, which normally arrives with an aircraft profile. */
  configure(keys) { return this.control("configure", { keys }) }

  /** What pressing Join does, awaited to the end of the attempt. */
  join(code) { return this.control("join", { code }) }

  /** What pressing Leave does: not an outage, so history is dropped and panels unlock. */
  leave() { return this.control("leave") }

  peers() { return this.control("peers").then((r) => r) }

  /** LiteNetLib's own loss and latency simulation, on both networks. */
  netsim({ loss = 0, minLat = 0, maxLat = 0 } = {}) { return this.control("netsim", { loss, minLat, maxLat }) }

  /* ---- process ---- */

  /** The clean exit: goodbye to panels, disconnect to the peer. */
  async quit({ timeout = 15000 } = {}) {
    if (!this.proc || this.exited) return
    try { await this.control("quit", {}, { timeout: 5000 }) } catch { /* it may go before replying */ }
    await this._awaitExit(timeout)
  }

  /** No goodbye and no disconnect. */
  async crash({ timeout = 15000 } = {}) {
    if (!this.proc || this.exited) return
    await run("taskkill", ["/F", "/T", "/PID", String(this.proc.pid)])
    await this._awaitExit(timeout)
  }

  /** Sockets stay open, nothing answers. The peer sees a link that stopped, not one
   *  that closed, which no kill and no Leave produces. */
  async suspend() {
    await ntProcess("NtSuspendProcess", this.proc.pid)
    this.suspended = true
  }

  async resume() {
    await ntProcess("NtResumeProcess", this.proc.pid)
    this.suspended = false
  }

  async _awaitExit(timeout) {
    if (this.exited) return
    const timer = setTimeout(() => {
      try { this.proc.kill() } catch { /* already gone */ }
    }, timeout)
    await once(this.proc, "exit")
    clearTimeout(timer)
  }

  async dispose() {
    try { this._sock && this._sock.destroy() } catch { /* fine */ }
    if (this._tail) clearInterval(this._tail)
    if (this.proc && !this.exited) {
      if (this.suspended) await this.resume().catch(() => {})
      await run("taskkill", ["/F", "/T", "/PID", String(this.proc.pid)]).catch(() => {})
    }
  }

  /* ---- log ---- */

  /*
   * The app is a WinExe, so its console sink has nowhere to write when the bench
   * starts it; the file sink does. Polled rather than watched: fs.watch on Windows
   * misses appends to a file another process holds open, and 50 ms of latency on a
   * log line costs nothing here.
   */
  _tailLog() {
    const path = join(this.dir, "log")
    let offset = 0
    let partial = ""
    this._tail = setInterval(() => {
      if (!existsSync(path)) return
      let fd
      try {
        const size = statSync(path).size
        if (size <= offset) return
        fd = openSync(path, "r")
        const buf = Buffer.alloc(size - offset)
        readSync(fd, buf, 0, buf.length, offset)
        offset = size
        partial += buf.toString("utf8")
        const parts = partial.split(/\r?\n/)
        partial = parts.pop()
        for (const line of parts) if (line.trim()) this.lines.push({ at: now(), line })
      } catch { /* the app is writing; next tick */ }
      finally { if (fd !== undefined) closeSync(fd) }
    }, 50)
  }

  /** Resolves with the regex match for the first log line matching, past or future. */
  waitForLog(re, { timeout = 15000, from = 0 } = {}) {
    const search = () => {
      for (let i = from; i < this.lines.length; i++) {
        const m = re.exec(this.lines[i].line)
        if (m) return m
      }
      return null
    }
    const hit = search()
    if (hit) return Promise.resolve(hit)
    return new Promise((resolve, reject) => {
      const started = Date.now()
      const timer = setInterval(() => {
        const m = search()
        if (m) { clearInterval(timer); return resolve(m) }
        if (Date.now() - started > timeout) {
          clearInterval(timer)
          reject(new Error(`${this.name}: no log line matching ${re} in ${timeout}ms`))
        }
      }, 50)
    })
  }

  /** Every log line matching, for counting rather than waiting. */
  logMatches(re) {
    return this.lines.filter((l) => re.test(l.line))
  }
}

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

function run(cmd, args) {
  return new Promise((resolve, reject) => {
    execFile(cmd, args, { windowsHide: true }, (err, stdout) => (err ? reject(err) : resolve(stdout)))
  })
}

/* Suspending a process needs ntdll; PowerShell is the shortest way to reach it that
 * does not add a dependency. Get-Process gives a handle with the access these need. */
function ntProcess(fn, pid) {
  const script = [
    `$p = Get-Process -Id ${pid}`,
    `Add-Type -Name Nt -Namespace Bench -MemberDefinition '`,
    `[DllImport("ntdll.dll")] public static extern uint NtSuspendProcess(IntPtr h);`,
    `[DllImport("ntdll.dll")] public static extern uint NtResumeProcess(IntPtr h);'`,
    `[void][Bench.Nt]::${fn}($p.Handle)`
  ].join("\n")
  return run("powershell", ["-NoProfile", "-NonInteractive", "-Command", script])
}
