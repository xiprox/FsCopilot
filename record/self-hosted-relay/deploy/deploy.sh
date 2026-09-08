#!/usr/bin/env bash
#
# Publish FsCopilot.Discovery and roll it out to the self-hosted relay box.
#
# Run from any machine with the .NET 9 SDK - including Windows under Git Bash,
# since the publish is a cross-compile to linux-x64 and needs no Linux locally.
#
#   ./deploy.sh root@fscrelay.ihsan.dev
#   FSC_RELAY_SSH=root@fscrelay.ihsan.dev ./deploy.sh
#   ./deploy.sh --no-trim root@fscrelay.ihsan.dev   # see docs/01, trim warnings
#
# See ../docs/03-runbook.md. First-time setup (user, unit, firewall) is not done
# here; this script only builds and replaces the binary.

set -euo pipefail

# --- SSH transport -----------------------------------------------------------
# Git Bash's MSYS2 OpenSSH cannot reach the Windows/1Password SSH agent, which is
# exposed over a named pipe rather than a Unix socket: it fails with
# "Permission denied (publickey)" while Windows' own ssh.exe authenticates fine.
# So on Windows, default to the native binaries and disable MSYS path mangling
# (which would otherwise rewrite "user@host:/path" as if it were a path list).
SSH_BIN="${SSH_BIN:-}"
SCP_BIN="${SCP_BIN:-}"
NATIVE_WIN_SSH=false

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
        if [ -z "$SSH_BIN" ] && [ -x "/c/Windows/System32/OpenSSH/ssh.exe" ]; then
            SSH_BIN="/c/Windows/System32/OpenSSH/ssh.exe"
            SCP_BIN="/c/Windows/System32/OpenSSH/scp.exe"
            NATIVE_WIN_SSH=true
            export MSYS2_ARG_CONV_EXCL='*'
        fi
        ;;
esac

SSH_BIN="${SSH_BIN:-ssh}"
SCP_BIN="${SCP_BIN:-scp}"

# 1Password prompts per connection; this script opens several.
# Use its "authorise for N minutes" option or expect repeated prompts.

TRIMMED=true
SSH_TARGET="${FSC_RELAY_SSH:-}"

while [ $# -gt 0 ]; do
    case "$1" in
        --no-trim) TRIMMED=false; shift ;;
        -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
        -*) echo "unknown flag: $1" >&2; exit 2 ;;
        *)  SSH_TARGET="$1"; shift ;;
    esac
done

if [ -z "$SSH_TARGET" ]; then
    echo "usage: $0 [--no-trim] <user@host>   (or set FSC_RELAY_SSH)" >&2
    exit 2
fi

# The record lives on ahead-record; the code is built from the ahead worktree,
# which is its sibling. Override with FSC_SOURCE if your layout differs.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE="${FSC_SOURCE:-$(cd "$HERE/../../../.." && pwd)/ahead}"
PROJECT="$SOURCE/FsCopilot.Discovery/FsCopilot.Discovery.csproj"

if [ ! -f "$PROJECT" ]; then
    echo "no project at $PROJECT" >&2
    echo "set FSC_SOURCE to the checkout to build from" >&2
    exit 1
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "==> publishing from $SOURCE (trimmed=$TRIMMED)"
dotnet publish "$PROJECT" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishTrimmed="$TRIMMED" \
    -o "$STAGE"

BINARY="$STAGE/p2p_serv"
[ -f "$BINARY" ] || { echo "publish produced no p2p_serv" >&2; exit 1; }
echo "==> built $(du -h "$BINARY" | cut -f1)"

# A running binary cannot be overwritten in place (ETXTBSY), so upload beside it
# and rename - which is also atomic, so a failed upload never leaves a partial
# binary where systemd will try to exec it.
echo "==> uploading to $SSH_TARGET (via $SSH_BIN)"
UPLOAD_SRC="$BINARY"
if [ "$NATIVE_WIN_SSH" = true ]; then UPLOAD_SRC="$(cygpath -w "$BINARY")"; fi
"$SCP_BIN" "$UPLOAD_SRC" "$SSH_TARGET:/opt/fscopilot/p2p_serv.new"

echo "==> installing and restarting"
"$SSH_BIN" "$SSH_TARGET" bash -s <<'REMOTE'
set -euo pipefail
chown fscopilot:fscopilot /opt/fscopilot/p2p_serv.new
chmod 755 /opt/fscopilot/p2p_serv.new
mv /opt/fscopilot/p2p_serv.new /opt/fscopilot/p2p_serv
systemctl restart fscopilot-relay
sleep 2
systemctl is-active --quiet fscopilot-relay || {
    echo "--- service is not active ---" >&2
    journalctl -u fscopilot-relay -n 40 --no-pager >&2
    exit 1
}
REMOTE

echo "==> verifying listeners"
"$SSH_BIN" "$SSH_TARGET" 'ss -lunp | grep -E "3480|3600" || echo "WARNING: no UDP sockets bound on 3480/3600"'

echo "==> recent log"
"$SSH_BIN" "$SSH_TARGET" 'journalctl -u fscopilot-relay -n 15 --no-pager'

echo "==> done"
