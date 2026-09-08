# Self-hosted relay

    Purpose:  Running our own FsCopilot.Discovery instance instead of p2p.fscopilot.com.
    Status:   DEPLOYED 2026-09-08 at fscrelay.ihsan.dev. Reachable and verified; no client has
              connected yet.
    Scope:    Server only. Client-side host configuration is tracked separately.

## Documents

| Doc | What it settles |
| --- | --- |
| `01-service-inventory.md` | What the binary actually listens on, and three findings that constrain the setup. |
| `02-hosting-choice.md` | Why Cloudflare cannot proxy this, what the workload needs, which paid provider. |
| `03-runbook.md` | Step-by-step: provision, DNS, firewall, unit, deploy, verify, troubleshoot. |
| `04-free-options.md` | Zero-cost routes: Oracle Always Free, home hosting, mesh VPN. What each charges instead. |
| `05-deployment-log.md` | What was actually done and observed, versus what `03` plans. Read this first. |

## Artifacts

| File | What it is |
| --- | --- |
| `deploy/fscopilot-relay.service` | systemd unit, hardened, stats endpoint on loopback. |
| `deploy/deploy.sh` | Cross-compiles from the `ahead` worktree, uploads, swaps atomically, restarts. |

## The three findings, in one place

Each is verified against `ahead` at c04610c and each changes what you would otherwise do:

1. **`:2320` is two services upstream.** The profile updater's `/api/profiles/*` routes exist in
   no checkout here — only `/` and `/ws/stats` do. The updater must stay pointed at
   `p2p.fscopilot.com`; only the network host moves. Failures there are silent.
2. **Only UDP 3480 and 3600 need exposing.** Nothing in the desktop client consumes the stats
   WebSocket. This removes TLS, reverse proxies and a second hostname from the initial setup.
3. **The client resolves one address and never falls back.** Publish an A record only. An AAAA
   that is anything less than perfectly reachable produces clients that fail while `dig` looks
   healthy.

## Decided

- **`fscrelay.ihsan.dev` -> 167.99.157.219**, a single A record in the `ihsan.dev` Cloudflare
  zone, **grey cloud**. No AAAA. Verified resolving correctly on 2026-09-08.
- **DigitalOcean Basic, $4/month, Ubuntu 24.04.** Chosen for least friction rather than price:
  IPv4 bundled rather than metered, an opt-in cloud firewall we decline so `ufw` is the only one,
  and the widest region list of the candidates. `02` has the comparison and why Scaleway lost;
  `04` records the free routes and what each charges instead of money, should this want
  revisiting. Nothing here is on-demand in a useful sense — a powered-off droplet still bills.
- **New York (NYC1)**, for a group split across Europe and the US. Relay latency is the sum of both legs, so
  no placement avoids the transatlantic hop; NYC is chosen on worst case, London↔US-West being
  much worse than NYC↔EU. NYC1 vs NYC3 is immaterial — same metro, same transit. Revisit if
  sessions turn out mostly EU-internal. Table in `03` step 1.

## Why this host matters more than it first appeared

Found when `ahead` was rebuilt onto `af557a1`: **the fork speaks relay protocol v2 and upstream's
`p2p.fscopilot.com` does not** (`FsCopilot/Program.cs:19-23`). The fork's relay fallback is
broken against upstream's relay, so this is not a latency or independence play — it is what makes
the relay path work at all. `01` and `05` have the specifics.

## Open

- **No client has connected yet.** This is the one real gap. It is unblocked though: `--relay
  fscrelay.ihsan.dev --no-direct` points a client at this host and forces traffic through the
  relay, with no source change needed. `03` step 8 has the journal lines to expect.
- **`RelayHost` in `FsCopilot/Program.cs` still points at `p2p.fscopilot.com`**, carrying a
  `// TODO: the fork's own relay, once deployed`. Changing it is a source edit on an
  implementation branch, not this record's business, but it is the obvious next move.
- Closed since first draft: the `PublishTrimmed` risk (runs fine trimmed) and the memory question
  (27 MB actual against a 100–150 MB estimate). Both in `05`.
