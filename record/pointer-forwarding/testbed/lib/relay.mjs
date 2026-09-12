/*
 * The rendezvous, run locally.
 *
 * FsCopilot.Discovery is in the repository, so the bench does not have to reach
 * fscrelay.ihsan.dev to put two instances in touch. That matters for more than
 * politeness: a local rendezvous can be stopped and started mid-scenario, and a
 * run with no internet is the same run.
 *
 * STUN on 3480, relay on 3600, both UDP and both fixed in the service. The HTTP
 * side is only there to say "I'm fine" and is pinned to a high port so it cannot
 * collide with anything the developer is running.
 */

import { spawn } from "node:child_process"
import { once } from "node:events"

export class Relay {
  constructor(proc) {
    this.proc = proc
    this.lines = []
  }

  static async start({ project, timeout = 120000 }) {
    // The project is pinned to linux-x64 self-contained because that is what the
    // server runs. Overridden here rather than in the csproj: the deployed artifact
    // is not the bench's to change.
    const args = ["run", "--project", project, "-c", "Debug", "--no-launch-profile",
      "-r", "win-x64", "--no-self-contained"]
    const proc = spawn("dotnet", args, {
      windowsHide: true,
      env: { ...process.env, ASPNETCORE_URLS: "http://127.0.0.1:5399", DOTNET_NOLOGO: "1" },
      stdio: ["ignore", "pipe", "pipe"]
    })

    const relay = new Relay(proc)
    const collect = (chunk) => {
      for (const line of String(chunk).split(/\r?\n/)) if (line.trim()) relay.lines.push(line)
    }
    proc.stdout.on("data", collect)
    proc.stderr.on("data", collect)

    // Both hosted services announce their port. Waiting for the relay one means
    // waiting for the slower of the two, since Stun is registered first.
    const deadline = Date.now() + timeout
    while (!relay.lines.some((l) => /Server started on UDP port: 3600/.test(l))) {
      if (proc.exitCode !== null) {
        throw new Error(`relay exited (${proc.exitCode}):\n${relay.lines.slice(-15).join("\n")}`)
      }
      if (Date.now() > deadline) {
        throw new Error(`relay did not start in ${timeout}ms:\n${relay.lines.slice(-15).join("\n")}`)
      }
      await new Promise((r) => setTimeout(r, 200))
    }
    return relay
  }

  async stop() {
    if (this.proc.exitCode !== null) return
    // dotnet run holds the app as a child, so the tree goes rather than the shim.
    const { execFile } = await import("node:child_process")
    await new Promise((r) => execFile("taskkill", ["/F", "/T", "/PID", String(this.proc.pid)], r))
    await once(this.proc, "exit").catch(() => {})
  }
}
