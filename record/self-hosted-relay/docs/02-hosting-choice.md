# Where to put it

    Purpose:  Pick a host, given a Cloudflare-managed domain and no server yet.
    Depends:  docs/01-service-inventory.md for the port and DNS constraints.

## The constraint that decides everything: Cloudflare cannot proxy this

The two listeners that matter are **raw UDP on non-standard ports**. Cloudflare's proxy — the
orange cloud — terminates HTTP/HTTPS on a fixed set of TCP ports. It does not carry UDP, and it
does not carry arbitrary ports. The product that does carry UDP is **Spectrum**, which is
Enterprise-tier.

So, concretely:

- The relay hostname's A record must be set to **DNS only (grey cloud)**. Orange-clouding it
  makes the name resolve to Cloudflare's anycast IPs, and every client silently fails —
  the UDP never reaches your box.
- **Cloudflare Tunnel is not an option** either. It is oriented at TCP/HTTP origins and does
  not give you inbound UDP on 3480/3600.
- Your server's real IP is therefore public. For this service that is fine — it is what
  `p2p.fscopilot.com` does today — but it does mean the VPS firewall and your provider's own
  filtering are the only things in front of it. There is no Cloudflare DDoS layer here.

Cloudflare still earns its keep as DNS: fast propagation, free, and the record is a one-line
change when you move or resize the box.

If you later want the stats dashboard on a public HTTPS URL, that is a **second hostname**
(e.g. `stats.yourdomain.com`, orange-clouded, in front of a local reverse proxy on 443). One
hostname cannot be both proxied and not. But per `docs/01`, nothing in the client needs it —
start without it.

## What the workload actually needs

Modest, and worth stating because it rules out over-buying:

- **A public, unfiltered IPv4.** Not behind provider NAT, not a shared IP. Some budget hosts
  sell "shared IPv4" — those are unusable here.
- **No UDP filtering or aggressive UDP rate limiting.** This is the one thing to check before
  paying. A number of hosts throttle sustained UDP as DDoS mitigation, which presents as
  intermittent stutter rather than a clean failure.
- **1 vCPU / 1 GB is ample.** Both services are a `PollEvents` loop on a 10–15 ms tick over a
  couple of dictionaries. `Relay.ForwardToLinkedPeers` rents from `ArrayPool` and copies; there
  is no per-packet allocation churn and no persistence.
- **Bandwidth is negligible.** A `Physics` packet is 116 bytes at ~20 Hz ≈ 2.3 KB/s per
  direction per link, and the relay forwards to each linked peer. A four-person session is tens
  of KB/s. Any provider's included transfer covers this by orders of magnitude.

**Latency is the only spec that really matters.** A relay inserts a hop, so the box wants to sit
near the geographic centroid of the people who actually fly together — not near you
specifically, and not "wherever is cheapest". `docs/networking.md` in the profiles repo puts
relay overhead at ~40–80 ms; a badly placed relay turns that into 200 ms and the 400 ms WASM
interpolation buffer stops hiding it.

## Recommendation

Weighted for **least friction first**, because the resource requirements are so far below every
plan on offer that specs are not a differentiator — only ease, region coverage and billing
clarity are.

**DigitalOcean Basic droplet, $4–6/month.** The easiest of the three by a clear margin. Public
IPv4 is included in the bundled price rather than billed as a separate line; the cloud firewall
is opt-in, so a fresh droplet is permissive at the network layer and `ufw` on the box is the only
thing to configure — one firewall, not two; and there are twelve regions (San Francisco,
Richmond, NYC, Kansas City, Atlanta, Toronto, Amsterdam, London, Frankfurt, Bangalore, Singapore,
Sydney), which is the widest coverage here and matters more than any spec. The $4 tier is
512 MB — enough, since the process idles around 100–150 MB — but take the $6 / 1 GB tier and stop
thinking about it. Best documentation and community of any provider in this class, which is what
you are actually buying.

**Hetzner, CX23 (2 vCPU / 4 GB / 40 GB NVMe) or CAX11 (same on Ampere ARM).** Better value if
your group is EU-centric: IPv4 included, at least 20 TB of traffic, and roughly the same money
for several times the machine. Note the line was renamed — CX22 is now **CX23**. Locations on the
cost-optimized line are EU-Central, Falkenstein, Nuremberg and Helsinki. `CAX11` is ARM and needs
`-r linux-arm64`, which is verified working (`docs/04`). Slightly less hand-holding than
DigitalOcean, still genuinely easy.

**Scaleway — skip it.** It looks cheapest and is not, and it is the least easy of the three:
the flexible IPv4 is billed separately from the instance **and keeps billing while the instance
is powered off**, and prices rose on 2026-06-01 with IPv4 scarcity cited as a driver. Widely
quoted STARDUST figures (€1.80/month) date from 2020 and no longer hold. Its regions are
EU-only, so it buys no coverage Hetzner does not already give you. A separate metered IPv4 with a
powered-off billing gotcha is exactly the wrong shape for a set-and-forget box.

Also reasonable: **Vultr**, if your group is spread across regions the two above do not serve.
- **Oracle Cloud Always Free.** Genuinely free, indefinitely, and a real contender rather than a
  footnote — its AMD micro shape is x86_64, so it needs no recompile at all. It trades money for
  signup friction, a reclamation policy and a second firewall to configure.
  **`docs/04-free-options.md` covers this and every other zero-cost route in full**, including
  home hosting and the mesh-VPN approach that skips public hosting entirely.

**Do not** try these — none of them can carry arbitrary inbound UDP: Fly.io, Railway, Render,
Heroku, Vercel, Cloudflare Workers, or any container platform that only exposes HTTP routes.

## Sizing over time

One box handles this until you are well past the point where you would have noticed. If it ever
does need to scale, the shape of the code decides the shape of the answer: `Relay` keeps its
peer tables in plain `Dictionary` fields mutated only on the poll thread, so there is no shared
state to coordinate — you would run a second independent box on a second hostname rather than
cluster this one.
