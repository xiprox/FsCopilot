#!/usr/bin/env bash
#
# Rebuild the `ahead` integration branch: reset it to `main`, then merge every
# `ahead-*` topic branch into it, one at a time, in name order.
#
# `ahead` holds no commits of its own. It is derived from (main + the topics),
# so it is safe to throw away and remake -- every commit lives on a topic
# branch. Anything that must ship in the fork but would never be a PR upstream
# still gets its own `ahead-*` branch; nothing is committed to `ahead` directly.
#
# `ahead` has its own worktree (fscopilot-ahead) so that rebuilding never yanks
# the branch out from under wherever you are actually editing. That worktree is
# also the place to build and run from: it is the only checkout carrying every
# topic at once.
#
# This script cannot live inside the fork: resetting `ahead` to `main` would
# delete it from the working tree mid-run.
#
# Usage:  ahead-rebuild.sh            rebuild, stop on first real conflict
#         AHEAD_REPO=/path/to/repo ahead-rebuild.sh
#
set -euo pipefail

REPO="${AHEAD_REPO:-/c/Users/wayne/dev/fscopilot-ahead}"
BASE="${AHEAD_BASE:-main}"
INTEGRATION="${AHEAD_BRANCH:-ahead}"
PREFIX="${AHEAD_PREFIX:-ahead-}"

export GIT_EDITOR=true   # never open an editor for a merge message

die() { printf '\nerror: %s\n' "$*" >&2; exit 1; }
say() { printf '%s\n' "$*"; }

cd "$REPO" 2>/dev/null || die "not a directory: $REPO"
git rev-parse --git-dir >/dev/null 2>&1 || die "not a git repository: $REPO"
git show-ref --verify --quiet "refs/heads/$BASE" || die "no '$BASE' branch in $REPO"

GITDIR=$(git rev-parse --git-dir)

# --- guards -----------------------------------------------------------------

# Untracked files are tolerated: they do not affect the reset or the merges, and
# the ignore rules that would hide them only exist on ahead-devex, which is not
# in effect while the rebuild is sitting on `main`. Git still refuses on its own
# if a merge would overwrite one, and that is reported below.
if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    git status --short --untracked-files=no
    die "working tree is dirty -- commit or stash before rebuilding"
fi

if [ -e "$GITDIR/MERGE_HEAD" ]; then
    die "a merge is already in progress -- finish it or 'git merge --abort'"
fi

# `git checkout -B` refuses a branch checked out in another worktree.
other=$(git worktree list --porcelain | awk -v b="refs/heads/$INTEGRATION" '
    /^worktree /{w=$2} /^branch /{if ($2==b) print w}')
if [ -n "$other" ] && [ "$(git rev-parse --show-toplevel)" != "$(cd "$other" && git rev-parse --show-toplevel)" ]; then
    die "'$INTEGRATION' is checked out in another worktree: $other"
fi

# rerere replays conflict resolutions you have already made, which is what
# makes repeated rebuilds cheap. autoupdate is required for it to be
# scriptable: without it the index stays conflicted even when the tree is fixed.
for c in rerere.enabled rerere.autoupdate; do
    if [ "$(git config --get "$c" || echo false)" != "true" ]; then
        git config "$c" true
        say "note: enabled $c (local to this repo)"
    fi
done

# --- collect topics ---------------------------------------------------------

mapfile -t topics < <(git for-each-ref --format='%(refname:short)' --sort=refname "refs/heads/${PREFIX}*")
[ ${#topics[@]} -gt 0 ] || die "no branches matching '${PREFIX}*'"

previous=$(git rev-parse --verify --quiet "$INTEGRATION" || true)

say "rebuilding '$INTEGRATION' from '$BASE' ($(git rev-parse --short "$BASE"))"
[ -n "$previous" ] && say "previous '$INTEGRATION' was ${previous:0:9} -- recoverable via reflog"

if git show-ref --verify --quiet refs/remotes/upstream/main; then
    behind=$(git rev-list --count "$BASE..upstream/main" 2>/dev/null || echo 0)
    [ "$behind" != "0" ] && say "note: '$BASE' is $behind commit(s) behind upstream/main (not fetching)"
fi
say ""

# --- rebuild ----------------------------------------------------------------

git checkout -q -B "$INTEGRATION" "$BASE"

merged=() skipped=()
for topic in "${topics[@]}"; do
    if [ "$(git rev-list --count "$BASE..$topic")" = "0" ]; then
        say "  skip   $topic (no commits beyond $BASE)"
        skipped+=("$topic")
        continue
    fi

    if out=$(git merge --no-ff --no-edit "$topic" 2>&1); then
        say "  merge  $topic"
        merged+=("$topic")
        continue
    fi

    # A merge git refused to even start -- an untracked file standing where a
    # tracked one would land, most often -- leaves no MERGE_HEAD and no unmerged
    # paths, which is indistinguishable from a clean rerere replay unless it is
    # checked for explicitly. Report git's own words rather than guessing.
    if [ ! -e "$GITDIR/MERGE_HEAD" ]; then
        say ""
        say "MERGE REFUSED for $topic -- git would not start it:"
        printf '%s\n' "$out" | sed 's/^/    /'
        cat <<EOF

'$INTEGRATION' is left part-built and nothing is lost: every commit is still on
its topic branch. Clear the cause and run this again.
EOF
        exit 1
    fi

    # Nonzero, merge in progress, nothing unmerged: rerere resolved every
    # conflict from its cache and the merge just needs committing.
    if [ -z "$(git diff --name-only --diff-filter=U)" ]; then
        git commit --no-edit -q
        say "  merge  $topic (conflict resolved from rerere cache)"
        merged+=("$topic")
        continue
    fi

    say ""
    say "CONFLICT merging $topic -- files left for you to resolve:"
    git diff --name-only --diff-filter=U | sed 's/^/    /'
    cat <<EOF

The merge is in progress in $REPO. Resolve, 'git add' the files, then:

    git commit --no-edit        # rerere remembers this resolution
    $0                          # rebuild again from the top

Or 'git merge --abort' to back out. '$INTEGRATION' is derived, so nothing is
lost either way -- every commit is still on its topic branch.
EOF
    exit 1
done

# --- report -----------------------------------------------------------------

say ""
say "'$INTEGRATION' is now $(git rev-parse --short HEAD)  (${#merged[@]} merged, ${#skipped[@]} skipped)"
say ""
git diff --stat "$BASE..$INTEGRATION" | tail -n 20
say ""
for topic in "${merged[@]}"; do
    git merge-base --is-ancestor "$topic" HEAD \
        && say "  ok       $topic  $(git rev-parse --short "$topic")" \
        || say "  MISSING  $topic  -- not an ancestor of $INTEGRATION"
done
say ""
say "not pushed. to publish:  git push --force-with-lease fork $INTEGRATION"
