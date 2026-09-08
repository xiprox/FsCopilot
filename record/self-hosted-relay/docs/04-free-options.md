# Free options

    Purpose:  What it takes to run this at zero cost, and what each option charges instead.
    Depends:  docs/01-service-inventory.md for the port and DNS constraints.
    Note:     Cloud free tiers move. Verify current terms before committing.

The baseline to beat is a DigitalOcean $6 droplet or Hetzner CX23 at roughly €45–70/year (`docs/02`). Everything below trades
that for some combination of signup friction, reliability risk, or work.

One constraint applies to all of them: **the ports are not negotiable.** `StunPort = 3480` and
`RelayPort = 3600` are `const` on the client (`P2PNetwork.cs:12`, `RelayNetwork.cs:21`) and
matched by `const Port` on the server (`Stun.cs:10`, `Relay.cs:9`). Whatever you host on must
accept inbound UDP on exactly those two ports.

## 1. Oracle Cloud Always Free — the real answer

Genuinely free, indefinitely, with a public IPv4 and 10 TB/month egress. Two shapes qualify,
and the less obvious one is the better fit:

| Shape | Arch | Spec | Free allowance | Needs |
| --- | --- | --- | --- | --- |
| `VM.Standard.E2.1.Micro` | x86_64 | 1/8 OCPU, 1 GB | **2 instances** | nothing — current `linux-x64` publish drops straight on |
| `VM.Standard.A1.Flex` | ARM | up to 4 OCPU, 24 GB | 1 allocation | `-r linux-arm64` |

**Prefer the AMD micro.** It is wildly under-specced on paper and still fine here: both services
are a `PollEvents` loop on a 10–15 ms tick over plain dictionaries, `ForwardToLinkedPeers` rents
from `ArrayPool` rather than allocating per packet, and nothing is persisted. 1 GB is several
times what the process needs. It also sidesteps the two things that make the ARM shape annoying
— see capacity, below.

If you do take the ARM shape, the recompile is one flag and it is verified: publishing
`-r linux-arm64` at c04610c succeeds, producing a 51 MB binary with the same two
`Serilog`/`Serilog.Expressions` trim warnings as x64 and nothing architecture-specific.
`deploy.sh` would need its hardcoded `linux-x64` changed to match.

**The caveats, in the order they actually bite:**

- **Two firewalls, again.** You must open the UDP ports in the VCN Security List (or an NSG)
  *and* in the instance's local firewall. Oracle's Ubuntu images ship restrictive `iptables`
  rules that persist across reboots and are not managed by `ufw` — installing `ufw` and adding
  rules there while the stock iptables rules still sit in front is the single most common way an
  Oracle box appears configured and drops everything.
- **A1 capacity.** "Out of host capacity" is routine for the ARM shape in popular regions and can
  persist for weeks. The AMD micros are generally available. Another reason to prefer them.
- **Idle reclamation.** Oracle reclaims idle Always Free compute. A relay that sees traffic a few
  evenings a week is exactly the profile that gets flagged. The practical exemption is upgrading
  the account to Pay As You Go — you still pay nothing while inside the free limits, but the
  reclamation policy no longer applies. Do this before you rely on the box.
- **Signup friction.** Card required, and their fraud checks reject a fair share of signups for
  no stated reason.

## 2. Host it yourself — free if you are not behind CGNAT

The bandwidth case is easy: tens of KB/s per session (`docs/02`). Any home connection carries
this without noticing. What matters is reachability.

**The gate is CGNAT.** Compare what the internet sees against what your router thinks it has:

```bash
curl -4 ifconfig.me
```

If that does not match the WAN address in your router's status page, you are behind
carrier-grade NAT, port forwarding cannot work, and this option is closed. Stop here.

Otherwise: forward UDP 3480 and 3600 to the machine, external ports identical to internal (the
consts above leave no room). If your ISP rotates your IP, a cron job against the Cloudflare API
keeps the A record current — free, and you already have the zone.

**What it costs you instead of money:**

- **Your home IP becomes public** to anyone who resolves the hostname. Worth being precise about
  the delta: in direct P2P mode peers already exchange real IPs with each other, so this is not
  a new exposure *to the people you fly with* — but the relay is reachable by anyone who learns
  the name, which the P2P path is not.
- **Availability is now yours.** A relay that is down when someone needs it is worse than
  upstream's, which is the thing you are trying to improve on.
- **Your uplink and its latency are the relay's.** If you are flying in the same session, relay
  traffic shares your connection with your own sim traffic. Trivial at these volumes, not zero.
- **Windows means more work.** The csproj targets `linux-x64`; a Windows host needs the RID
  changed to `win-x64` plus a service wrapper, and WSL2 adds its own port-forwarding layer in
  front of the one you already configured. **A spare Pi or mini-PC on Linux is much cleaner** —
  and per §1 the `linux-arm64` publish is verified working, so a Pi 4/5 is a first-class target.

## 3. Small closed group — skip public hosting entirely

If the people who fly together are a fixed set rather than an open community, put them on a mesh
VPN and run `p2p_serv` on any machine at all, including your desktop. Point the client host at
its mesh address. No public IP, no port forwarding, no CGNAT question, no exposure.

**ZeroTier** fits better than Tailscale here: its free tier is 25 devices with no user cap,
where Tailscale's free plan caps at 3 users — and a flying group is separate accounts, not
shared devices.

Cost: everyone installs a VPN client and joins your network. Fine among friends, unworkable for
a public community. Side effect worth having: with everyone on one flat mesh, direct P2P
generally succeeds, so the relay path rarely fires at all.

## 4. Free for twelve months, then not

- **AWS** `t3.micro` and **Azure** `B1s` free tiers both expire after a year, leaving a paid VPS
  with extra steps. AWS additionally bills public IPv4 (since Feb 2024); the free tier covers
  750 h/month of it for the first 12 months only.
- **GCP** `e2-micro` is limited to a few US regions and bills the external IPv4 separately, which
  eats most of the point.

Reasonable for a trial, wrong as a destination.

## 5. Not viable

- **Fly.io, Railway, Render, Cloudflare Workers** — no arbitrary inbound UDP, no meaningful free
  tier, or both.
- **"Free VPS" providers** — unreliable where not outright fraudulent. Not worth the hostname.

## Recommendation

**Oracle Cloud, AMD micro shape**, upgraded to Pay As You Go to dodge idle reclamation, with the
VCN security list *and* stock iptables both opened. It is free indefinitely, needs no code
change, and is the only free option that is genuinely hands-off once it is up.

**If you own a Pi or mini-PC and are not behind CGNAT**, home hosting beats it — no signup, no
reclamation policy, and you control the box. Verify CGNAT first, because it decides the question
outright.

**If the group is small and fixed**, ZeroTier is less work than either and removes the public
attack surface completely.
