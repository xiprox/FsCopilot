# Runbook: standing up the relay

    Purpose:  End-to-end setup, from an empty Cloudflare zone to two clients linked.
    Depends:  docs/01-service-inventory.md (ports, DNS trap), docs/02-hosting-choice.md (which provider, why grey cloud).
    Target:   fscrelay.ihsan.dev, DigitalOcean Basic $4 in NYC1 (167.99.157.219), Ubuntu 24.04 LTS.

Client-side configuration — teaching the desktop app to use this host instead of
`p2p.fscopilot.com` — is deliberately out of scope here and tracked separately. This runbook
gets the server up and proves it is reachable.

## 1. Provision the box

**DigitalOcean, Basic / Regular SSD, $4/month** — 512 MB, 1 vCPU, 10 GB, 500 GB transfer.
Ubuntu 24.04 (LTS) x64.

512 MB is genuinely enough: the process is two `PollEvents` loops over plain dictionaries,
`ForwardToLinkedPeers` rents from `ArrayPool` instead of allocating per packet, nothing is
persisted. **Measured in production: 27 MB resident**, on a box that idles at 188 MB total —
so 512 MB is generous rather than merely adequate (`docs/05`). Step 2 adds swap anyway, but as
insurance against `apt`, not against the service. Transfer is a non-question — see the KB/s
figures in `docs/02`.

**Region: New York (NYC1).** The group spans Europe and the US, and relay latency is the *sum* of both
legs, so this is a real trade rather than an optimisation. Approximate added round-trip:

| Pair | via NYC | via London |
| --- | --- | --- |
| EU <-> EU | ~160 ms | ~40 ms |
| EU <-> US East | ~100 ms | ~100 ms |
| US East <-> US East | ~40 ms | ~160 ms |
| US West <-> US West | ~140 ms | ~300 ms |

Mixed transatlantic pairs cost the same either way - that hop is unavoidable - so the decision is
which same-continent pairs to penalise, and the tiebreak is the worst case. London's US-West
figure is far worse than NYC's EU figure, because NYC is well connected to the US West Coast and
London is not. New York takes the better worst case. NYC1 and NYC3 are the same metro on the same transit, so which of the two you land in makes no measurable difference.

Two things make this less weighty than the table suggests: it only applies once P2P has already
failed (`docs/networking.md` in the profiles repo has the fallback order), and the 400 ms
interpolation buffer in the WASM layer absorbs a good deal of it. **If sessions turn out to be
mostly EU-internal, move to AMS3 or LON1** - that is one new droplet and one DNS edit.

Add your SSH key at creation time. Everything below runs as root over SSH.

**Leave the Cloud Firewall off.** It is opt-in, and off is what you want: the box's own `ufw` in
step 5 becomes the single place the UDP ports are controlled. Turning it on makes it a second
place those ports must also be opened, and two firewalls silently disagreeing is the most common
way this setup appears to work and does not. It is the same trap that makes Oracle's free tier
awkward (`docs/04`), and declining it here is most of why DigitalOcean is the easy option.

On billing, since it comes up: droplets bill hourly against a monthly cap, but **a powered-off
droplet still bills** — only destroying it stops the meter. There is no useful "run it just for
the session" mode; destroy-and-recreate would mean a new IP and a DNS edit every time. The hourly
rate is worth knowing for a throwaway trial run, not as an operating model.

## 2. Swap and base packages

512 MB with no swap will survive the service comfortably but can OOM during an `apt` upgrade.
One gigabyte of swap on the 10 GB disk removes that whole class of surprise:

```bash
fallocate -l 1G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile && echo '/swapfile none swap sw 0 0' >> /etc/fstab && free -h
```

The tools used later in this runbook:

```bash
apt update && apt upgrade -y && apt install -y ufw
```

## 3. Cloudflare DNS

One record, in the `ihsan.dev` zone:

| Type | Name | Content | Proxy status | TTL |
| --- | --- | --- | --- | --- |
| A | `fscrelay` | the droplet's IPv4 | **DNS only (grey cloud)** | Auto |

**Do not orange-cloud it.** Cloudflare's proxy carries neither UDP nor arbitrary ports, so a
proxied record resolves to Cloudflare anycast IPs and every client fails. `docs/02` has the full
reasoning, including why Tunnel is not an alternative.

**Do not add an AAAA record.** The client picks whichever address DNS returns first and never
falls back to the other family (`docs/01`), so an AAAA that is anything less than perfectly
reachable produces clients that fail while your own `dig` looks healthy.

Verify from your machine — the second command must return nothing, and the first must return
your server's IP rather than a `104.` or `172.67.` Cloudflare address:

```bash
dig +short A fscrelay.ihsan.dev; dig +short AAAA fscrelay.ihsan.dev
```

## 4. Service account and layout

```bash
adduser --system --group --no-create-home --home /opt/fscopilot fscopilot && mkdir -p /opt/fscopilot && chown fscopilot:fscopilot /opt/fscopilot
```

## 5. Firewall

Per `docs/01`, the client needs UDP 3480 and 3600 and nothing else. The stats endpoint stays on
loopback and is reached over an SSH tunnel.

```bash
ufw default deny incoming && ufw default allow outgoing && ufw allow OpenSSH && ufw allow 3480/udp && ufw allow 3600/udp && ufw enable && ufw status verbose
```

## 6. Install the unit

Copy `deploy/fscopilot-relay.service` from this directory to the server:

```bash
scp deploy/fscopilot-relay.service root@fscrelay.ihsan.dev:/etc/systemd/system/
```

```bash
ssh root@fscrelay.ihsan.dev "systemctl daemon-reload && systemctl enable fscopilot-relay"
```

Read the comments in that file before enabling it — particularly the note that
`MemoryDenyWriteExecute` must stay off (the .NET JIT needs W^X) and that `AF_NETLINK` must stay
in `RestrictAddressFamilies` (.NET enumerates interfaces through it, and dropping it surfaces as
a bind failure rather than anything netlink-shaped).

Enabling without a binary present is fine — the first start happens in the next step.

## 7. Build and deploy

From your workstation, with the .NET 9 SDK installed. Git Bash on Windows is fine; the publish
is a cross-compile to `linux-x64` and needs no Linux locally.

```bash
cd record/self-hosted-relay && ./deploy/deploy.sh root@fscrelay.ihsan.dev
```

The script publishes from the `ahead` worktree, uploads the ~45 MB single-file binary as
`p2p_serv.new`, renames it into place and restarts the unit. The rename is not decoration: a
running binary cannot be overwritten in place, and the atomic swap means a failed upload never
leaves a truncated file where systemd will try to exec it.

It finishes by printing the bound sockets and the last few log lines.

**Mind what you build from.** `deploy.sh` defaults to the `ahead` worktree, which is the *built*
integration branch - it gets reset and re-merged by `ahead-rebuild.sh`, and it may be mid-rebuild
or in use by other work when you run this. The publish only reads the source, but it writes
`bin/` and `obj/` into that tree. If the relay code is being changed on an implementation branch,
point the script at that branch's worktree instead:

```bash
FSC_SOURCE=/c/Users/wayne/dev/fsc/ahead-<branch> ./deploy/deploy.sh root@fscrelay.ihsan.dev
```

**What the branch does and does not affect.** The server is *schema-agnostic*: it never computes
a schema of its own, it parses `schema=` out of each client's connection token and compares the
two **clients to each other** (`Relay.cs:200`). So a `SCHEMA_MISMATCH` is always a statement about
mismatched clients, never about the server being built from the wrong branch, and you are free to
build the server from any branch on that count.

What must agree is the **relay protocol version**, which the server does enforce about itself:
the token is `v=2;pid=...;schema=...`, and a client claiming a version newer than the server's
`ProtocolVersion` is rejected outright with `VERSION_UNSUPPORTED`. The fork is on v2 and
upstream's `p2p.fscopilot.com` is not, which is the whole reason this host exists
(`FsCopilot/Program.cs:19-23`). So: **build the server from the same tree as the clients**, and
redeploy it whenever the protocol version advances.

## 8. Verify

**On the server** — the two startup lines are emitted by `Stun.StartAsync` and
`Relay.StartAsync` and are the unambiguous signal that both listeners bound:

```bash
systemctl status fscopilot-relay; ss -lunp | grep -E "3480|3600"
```

Expect `Server started on UDP port: 3480` and `Server started on UDP port: 3600`.

**From outside** — a UDP scan distinguishes a firewall problem from a working listener:

```bash
nmap -sU -p 3480,3600 fscrelay.ihsan.dev
```

`open|filtered` is the correct result. `closed` means the packets reached the host and got an
ICMP port-unreachable — the service is not bound, or `ufw`/the cloud firewall is rejecting
rather than passing.

Note what you *cannot* usefully do: `nc -u` will not draw a reply. Both listeners speak
LiteNetLib framing, and the STUN service's `CALL|` handler sits behind an unconnected-message
header that a raw text write does not produce. Absence of a reply from `nc` means nothing.

**End to end** — the real proof is two clients pointed at the host. They do not need a source
change: `--relay` overrides the host, and `--no-direct` forces every link through the relay
instead of letting P2P succeed and quietly bypass the thing you are trying to test
(`FsCopilot/Program.cs:80-84`).

```
FsCopilot.exe --relay fscrelay.ihsan.dev --no-direct
```

Run that on two machines, then watch the journal:

```bash
journalctl -u fscopilot-relay -f
```

- `INTRODUCE <peerId> => <external>, <internal>` — a client reached the STUN service.
- `CALL <a> -> <b> => Introduce` — hole-punch attempt brokered; if P2P succeeds you will see
  nothing further, which is the good outcome.
- `CONNECT pid=<id> v=<n> schema=<hash>` — a client connected to the relay. With `--no-direct`
  this is the expected path; without it, it means P2P failed and the fallback engaged.
- `LINK UP <a> <-> <b>` — the relay link is established and traffic is being forwarded.

`LINK UP` for a pair that also shows `CONNECT` for both sides is the success condition.

## 9. If it does not work

**The service starts, then dies within a second, with a stack trace on the first log write.**
The trim warnings from `docs/01`: `PublishTrimmed=true` is set in the csproj and
`Serilog.Expressions`, which `Program.cs` uses for its console template, is not trim-safe.
**This did not happen on the 2026-09-08 deployment** — the trimmed build logs correctly, see
`docs/05` — so treat it as insurance rather than an expected step. If it ever does bite,
republish untrimmed:

```bash
./deploy/deploy.sh --no-trim root@fscrelay.ihsan.dev
```

The binary grows by roughly 20 MB; nothing else changes.

**`Failed to start the server on UDP port '3480'`** (or 3600). Something already holds the port,
or a previous instance has not exited. `ss -lunp | grep 348` names the holder.

**Ports scan as expected but no client ever appears in the log.** Check DNS first — this is the
AAAA trap from `docs/01` almost every time. `dig +short AAAA fscrelay.ihsan.dev` must be empty.
Then confirm the record is grey-clouded: a proxied A record returns a Cloudflare IP.

**`CONNECT` appears for both clients but `LINK UP` never does.** Schema mismatch — the two
clients have different registered packet sets. `Relay.HandleConnectIntent` rejects this with
`SCHEMA_MISMATCH` but **does not log it**; it only sends the error to the client. Compare the
`schema=` hash on the two `CONNECT` lines. Different hashes mean mismatched client builds, and
the fix is on the client side, not here.

**A client is rejected with `VERSION_UNSUPPORTED`.** The client speaks a newer relay protocol
than the server was built with. `ProtocolVersion` is `2` on both sides today (`Relay.cs:25`,
`RelayNetwork.cs:31`) and the server rejects anything claiming *newer* than its own
(`Relay.cs:99`); older clients are accepted. Rebuild and redeploy the server from the same tree
as the client. This is the one respect in which the server's own build actually matters — see
below.

**A client is rejected with `PEER_ID_TAKEN`.** Peer IDs are `Random.String(8)` generated fresh
per launch (`FsCopilot/Program.cs`), so this is a collision or a stale connection that has not
yet hit the 15 s `DisconnectTimeout`. It clears on its own.

## 10. Keeping it up

The unit sets `Restart=always` with a 5 s backoff, so process crashes recover themselves.
For the host:

```bash
apt install -y unattended-upgrades && dpkg-reconfigure -plow unattended-upgrades
```

Redeploys are just `./deploy/deploy.sh` again — it is idempotent and takes a few seconds of
downtime at the restart, which clients absorb as a reconnect.

To look at the stats WebSocket, which is bound to loopback:

```bash
ssh -L 2320:127.0.0.1:2320 root@fscrelay.ihsan.dev
```

Then locally, `http://127.0.0.1:2320/` returns the health string and
`ws://127.0.0.1:2320/ws/stats` streams `{"users":N,"relay_sessions":N}` once per second
whenever the numbers change.
