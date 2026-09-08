# What you are actually hosting

    Purpose:  Establish the surface of the self-hosted server before choosing where to put it.
    Source:   Read from ahead/ at af557a1 (post 'ahead-updater' merge). Line numbers are that tree.
    Note:     Originally written against c04610c; re-verified after the rebuild, which changed
              Relay.cs substantially. See docs/05 for what moved.

## One process, three listeners

`FsCopilot.Discovery` is a single ASP.NET Core host (`Microsoft.NET.Sdk.Web`) that registers
two `BackgroundService`s alongside the web app. There is no way to run "just the relay" without
also running the STUN service — they are `AddHostedService` calls in the same
`Program.cs`, and neither is gated by configuration.

| Listener | Port | Protocol | Source | Needed by the client? |
| --- | --- | --- | --- | --- |
| STUN / NAT-punch rendezvous | 3480 | UDP | `Stun.cs:10` | **Yes** |
| Relay | 3600 | UDP | `Relay.cs:9` | **Yes** |
| `/` health + `/ws/stats` | Kestrel default | TCP | `Program.cs`, `StatsWebSocketEndpoint.cs` | **No** — see below |

Build output is a single self-contained `linux-x64` file named `p2p_serv`, ~45 MB
(`AssemblyName`, `PublishSingleFile`, `SelfContained`, `RuntimeIdentifier` in the csproj).
Verified by publishing at c04610c; it builds clean apart from the trim warnings noted below.

## Finding: `:2320` is two different services upstream

The client points its profile updater at `http://p2p.fscopilot.com:2320`
(`FsCopilot/Program.cs:108`) and `Updater.cs` calls:

    GET /api/profiles/{key}
    GET /api/profiles/{key}/download
    GET /api/profiles/download

**None of those routes exist in this repository.** The ASP.NET app in `FsCopilot.Discovery`
maps exactly two things — `GET /` returning `"I'm fine"`, and the `/ws/stats` WebSocket.
Upstream is running a separate profiles API on the same host and port.

Consequence: the relay host and the updater host are **not the same setting** and must not be
collapsed into one. Repointing everything at a self-hosted box would leave profile downloads
hitting a server with no `/api/profiles` routes. `Updater` swallows every failure and logs
`"Profile server unavailable"` at warning level, so this fails quietly rather than loudly —
the app keeps running with whatever profiles are already on disk.

**The updater stays on `p2p.fscopilot.com:2320`.** Only the network host moves.

## Finding: nothing in the client consumes `/ws/stats`

Grepping the client and bridge for `ws/stats`, `ServerStats`, `2320` and `websocket` returns
one hit, and it is the `Updater` line above. The stats WebSocket exists for a dashboard, not
for the desktop app.

Consequence for the firewall: **expose UDP 3480 and 3600 only.** The HTTP listener can be bound
to loopback and reached over an SSH tunnel when you want to look at it. That removes the whole
question of TLS, reverse proxies and a second hostname from the initial setup.

## Finding: the client resolves one address and does not fall back

Both resolvers are the same shape:

    // P2PNetwork.cs:382, RelayNetwork.cs:263
    var ips = await Dns.GetHostAddressesAsync(_host, ct);
    var ip = ips.FirstOrDefault(x =>
        x.AddressFamily is AddressFamily.InterNetworkV6 or AddressFamily.InterNetwork);

`FirstOrDefault` over the unordered result set, accepting either family, with **no preference
and no retry against the remaining addresses**. If DNS hands back an AAAA first, that is the
only address the client will ever try for that session.

Consequence for DNS: **publish an A record and nothing else** unless IPv6 has been verified
end to end. A stale or half-working AAAA does not degrade to IPv4 — it produces clients that
fail to connect while `dig` looks fine to you. `RelayNetwork` at least logs
`[Relay] DNS {Host} FAILED` on an exception, but a resolvable-yet-unreachable address is not an
exception; it times out with nothing distinctive in the log.

Both server-side `NetManager`s do set `IPv6Enabled = true`, so adding IPv6 later is a server
config question rather than a code change.

## Trim warnings

`PublishTrimmed=true` is set in the csproj, and the publish emits:

    warning IL2104: Assembly 'Serilog.Expressions' produced trim warnings
    warning IL2104: Assembly 'Serilog' produced trim warnings

`Program.cs` uses `ExpressionTemplate` for console output, which is exactly the
reflection-driven path trimming is liable to break — and it breaks at *runtime*, on the first
log write, not at publish. **Resolved by observation on 2026-09-08:** the trimmed build runs and logs correctly in
production, with the `ExpressionTemplate` rendering as intended — see `docs/05`. The warnings
still appear at publish and the failure mode remains theoretically live, so if the service ever
starts and immediately dies on its first log line, set `PublishTrimmed=false` and republish; the
binary grows by roughly 20 MB and nothing else changes. See `docs/03-runbook.md` step 9.

## Protocol version: the reason this host has to exist

Discovered when `ahead` was rebuilt onto `af557a1`, and it changes the framing of the whole
exercise. **The fork speaks relay protocol v2; upstream's `p2p.fscopilot.com` does not**
(`FsCopilot/Program.cs:19-23`). The fork's relay fallback is therefore broken against upstream's
relay, and a self-hosted relay is not an optimisation but the thing that makes the feature work.

- `ProtocolVersion = 2` on both sides (`Relay.cs:25`, `RelayNetwork.cs:31`).
- The connection token gained a version field: `v=2;pid=...;schema=...` (`RelayNetwork.cs:256`).
- The server rejects a client claiming a version *newer* than its own with `VERSION_UNSUPPORTED`
  (`Relay.cs:99`); older clients are still accepted. So the server must be rebuilt when the
  client's protocol version advances, but not merely when packet schemas change.
- `ChannelsCount` is now 4, up from 2.
- The schema comparison moved to `Relay.cs:243` and is still client-to-client — the server never
  computes a schema of its own.
- `--relay <host>` and `--no-direct` client flags exist (`Program.cs:80-84`), which is what makes
  end-to-end testing possible without editing the `RelayHost` constant.
