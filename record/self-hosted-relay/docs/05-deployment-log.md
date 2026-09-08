# Deployment log

    Purpose:  What was actually done and actually observed, as opposed to what the runbook plans.
    Date:     2026-09-08
    Result:   Deployed and reachable. End-to-end client verification still outstanding.

## What is running

| | |
| --- | --- |
| Host | `fscrelay.ihsan.dev` → `167.99.157.219` |
| Provider | DigitalOcean Basic $4/mo, **NYC1**, Ubuntu 24.04.4 LTS, x86_64 |
| Memory | 458 MB total, 1 GB swapfile added |
| Built from | `ahead` at `af557a1` ("Merge branch 'ahead-updater' into ahead"), `linux-x64`, trimmed |
| Service | `fscopilot-relay.service`, enabled and active |
| Listeners | UDP 3480 + 3600 on `0.0.0.0` and `[::]`; Kestrel on `127.0.0.1:2320` |

## Verified

- **Both UDP sockets bound**, owned by `p2p_serv`, on IPv4 and IPv6 (`ss -lunp`).
- **Reachable from outside.** Six UDP datagrams sent from a Windows workstation to
  `fscrelay.ihsan.dev` on 3480 and 3600 all arrived, confirmed by `tcpdump` on the box. This
  exercises the whole path — DNS, the public internet, DigitalOcean's network, `ufw`, the process
  sockets — which `ss` on its own cannot tell you.
- **DNS resolves the way the client will resolve it.** `System.Net.Dns.GetHostAddresses` — the
  exact call the client makes — returns `167.99.157.219` and nothing else. No AAAA, so the
  single-address trap in `docs/01` is avoided.
- **Survives a reboot.** Rebooted after the kernel update; swap remounted from `fstab`, `ufw` came
  back active, service restarted from its `enable`.
- `systemd-analyze security` scores the unit **3.5 (OK)**.

## Findings from the deployment itself

### The trim risk did not materialise

`docs/01` flagged `PublishTrimmed=true` against non-trim-safe `Serilog`/`Serilog.Expressions` as
the likeliest first-boot failure, since `Program.cs` formats console output through
`ExpressionTemplate`. **It starts and logs correctly.** The journal shows the custom template
rendering as intended:

    [15:11:10 INF] Stun            Server started on UDP port: 3480

The IL2104 warnings still appear at publish and are still worth knowing about, but the risk is
now closed by observation rather than merely inherited from upstream's apparent success. The
`--no-trim` fallback in `03` step 9 stays as insurance, not as an expected step.

### Memory use is far below the estimate

`docs/01` and `03` estimated 100–150 MB idle. Actual is **27 MB** (`MemoryCurrent=28385280`).
The $4 / 512 MB tier is not merely adequate, it is generous — the box sits at 188 MB used in
total, most of which is the base system. Two contributors: the unit selects workstation GC
(`DOTNET_gcServer=0`), and trimming removed a great deal of unused framework.

The swapfile is still worth having, but as insurance against `apt` rather than against the
service.

### Git Bash cannot reach the 1Password SSH agent

The workstation's key lives in 1Password's SSH agent, which on Windows is exposed over the
OpenSSH **named pipe** (`\\.\pipe\openssh-ssh-agent`). Git Bash's MSYS2 build of OpenSSH does not
speak named pipes — it expects a Unix socket at `$SSH_AUTH_SOCK` — so `ssh` and `scp` from Git
Bash fail with `Permission denied (publickey)` while Windows' own
`C:\Windows\System32\OpenSSH\ssh.exe` authenticates fine.

This matters because `deploy/deploy.sh` is a bash script calling `ssh`/`scp`. It has been given
`SSH_BIN` / `SCP_BIN` overrides and now defaults to the Windows binaries when it detects
Windows. See the header of that script.

Related: 1Password prompts for authorisation per connection and returns
`sign_and_send_pubkey: ... agent refused operation` if the prompt is not answered in time. A
multi-file deploy is several connections; use its "authorise for N minutes" option.

## Not yet done

- **End-to-end client verification.** No client has connected. `docs/03` step 8 has the
  procedure, and it is now unblocked — see the protocol note below.
- The `RelayHost` constant in `FsCopilot/Program.cs` still points at `p2p.fscopilot.com`. Changing
  it is a source change on an implementation branch and is not this record's business, but it is
  the obvious next step: the comment there already says
  `// TODO: the fork's own relay, once deployed`.

## The protocol-v2 context, discovered during deployment

The `ahead-updater` merge changed the relay materially, and it reframes why this box matters:

- **The fork speaks relay protocol v2 and upstream's `p2p.fscopilot.com` does not**
  (`FsCopilot/Program.cs:19-23`). The fork's relay path is therefore *broken against upstream's
  relay* until a relay built from this tree is deployed. This host is that relay.
- `ProtocolVersion = 2` on both sides (`Relay.cs:25`, `RelayNetwork.cs:31`). The client's
  connection token is now `v=2;pid=...;schema=...`, and the server rejects any client claiming a
  version **newer** than its own with `VERSION_UNSUPPORTED` (`Relay.cs:99`). Older clients are
  accepted. So the server must be rebuilt whenever the client's protocol version advances.
- `ChannelsCount` is now 4, up from 2.
- The server remains **schema-agnostic** — it compares the two clients' `schema=` values to each
  other and never computes one of its own (`Relay.cs:243`). Version is the only thing it enforces
  about itself.
- `--relay <host>` and `--no-direct` client flags now exist (`Program.cs:80-84`). Together they
  make end-to-end testing possible without touching the `RelayHost` constant, and `--no-direct`
  is what forces traffic through the relay rather than letting P2P succeed and bypass it.
