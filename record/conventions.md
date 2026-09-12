# Conventions

    Purpose:  The rules that bind work in this fork, and the model the branches follow.
    Scope:    Cross-cutting. Feature-specific material lives in record/<feature>/.
    Status:   A note, not a loaded instruction file — see the caveat below.

> **This file is not picked up automatically.** Working in a worktree on an `ahead-*` branch,
> nothing loads it, so nothing will remind you that a stray `?.` takes out a whole panel file.
> Read it deliberately when starting work in this fork. Making it automatic was considered and
> rejected as not worth the cost — the alternatives were a copy on every branch (duplicated and
> conflict-prone), a file on `main` (which is a pristine upstream mirror and stays that way), or
> relocating every worktree under `fscopilot/` so an ancestor file could reach them, which is
> arranging the repository around a tooling detail. If that changes, this is the reason it was
> left as it is.

## The repository model

`main` is a pristine mirror of `upstream/main`. Nothing is ever committed to it. It exists so
that `main..ahead` answers "what have we changed", and so `git push fork main` is always a
fast-forward.

`ahead-*` branches hold **all our work on one feature**, implementation and record alike. They
are *not* PR branches. When a change is worth proposing upstream, cut a fresh branch from
`main` and take a subset across — deferring that cost is deliberate, because upstream has not
moved since 2026-04-16 and may never take anything.

`ahead` is **built**, never edited: reset to `main`, then merge every `ahead-*` branch in name
order. It holds no commits of its own, so it can be thrown away and remade — every commit
lives on a branch. Rebuild with `tools/ahead-rebuild.sh` in the **fscopilot-profiles** repo
(it cannot live in this one: resetting `ahead` to `main` would delete it mid-run).

Rebuilding is only cheap because of `rerere`, which needs **both** `rerere.enabled` and
`rerere.autoupdate`. It records a conflict resolution at **commit** time — `git add` alone
writes only the preimage and teaches it nothing — and replays it on later rebuilds, so each
distinct conflict is resolved by hand once, ever.

### Lifetimes are the reason the record is separate

An `ahead-*` implementation branch is **disposable**: when its change lands upstream, the
branch is dropped so it stops conflicting with `main`. `ahead-record` is **permanent**.

> Anything you would be sad to lose goes in `record/<feature>/`, never on the implementation
> branch.

`ahead-record` is also the only branch that touches `record/`, which is what makes it
conflict-free by construction rather than merely by luck.

## Code that runs in a panel

**It is Chrome 49.** Coherent GT reports `Chrome/49.0.2623`. The traps are syntax-level, so
they take out the whole file rather than one line: no optional chaining, no `??`, no class
fields, no `Object.fromEntries`, no `Array.at`, no `String.replaceAll`, no
`Promise.allSettled`. `const`, arrow functions, template literals, destructuring, spread,
`async`/`await` and `class` without fields are all fine.
See `record/pointer-forwarding/docs/09-environment.md` for the full audit.

**`node --check` every file under `PackageSources/HTML_UI` before building a package.** This is
not belt-and-braces: `VCockpit.js` shipped unparseable for two commits because an eighth
`Include.addImports(` got seven closing parens, and that file is the one loaded into *every*
panel — the failure is every instrument on the machine, not one.

**Anything injected into the pilot's own input path must fail open.** A wrapper left
suppressing once disabled an aircraft's clicks, and the handle needed to undo it had already
been deleted. Clear the suppressing state unconditionally, expose restore at a fixed global
that survives losing every other handle, and add a deadman that un-installs on its own.

**Prefix with `fsc` — the rule inverted on graduation.** While this work was a separate
Community package it had to avoid FS Copilot's namespace, and the playground's CLAUDE.md says
so: *"Prefix anything that runs in the sim with `FSCPP_`. Never `FSC_`, which is FS Copilot's."*
Inside the fork that is backwards. This **is** FS Copilot, and the code correctly uses
`window.fscPointer`, `fscChannel`, `fscOverlay`, `fscUnlock`, `fscListeners`. There is no
`FSCPP_` anywhere in the fork and there should not be. The old rule still applies to anything
shipped as a *separate* package alongside FS Copilot.

**Bridge JS needs no build.** Files under the Community package's `html_ui/` are loaded as-is:
edit, reload the panel, done. Keep as much logic as possible in JS for that reason. The .NET
SDK **is** installed (9.0.313, 9.0.317), so `dotnet build FsCopilot/FsCopilot.csproj` runs
locally and desktop-app errors no longer come back from Visual Studio by hand.

**The inspector is for inspecting, not for carrying data.** Its console channel silently drops
a message identical to the one before it, which cost half a recording before it was found.

## Writing

Applies to documentation, code comments and commit messages alike. Nothing in this section is
specific to this project — it is written to be lifted whole into another repository.

**The test: can it be read once, at speed, and understood?** A sentence that needs a second pass
to parse has failed, however accurate it is. Prose that is merely *correct* still wastes the
reader, and a reader who has to work is a reader who stops.

**Show the mechanism. Do not describe a property of it.** Naming a property makes the reader
reconstruct the concrete case to check the claim. Showing the case carries the conclusion for
free and costs fewer words.

    ✗  The receiver's own hit-testing decides what got pressed — which is what a second
       pilot reaching for the same button would do.
    ✓  Nothing depends on how the display is built. Event at position x sent, event at
       position x replayed.

    ✗  It blocks input by being there, it says why on its face, and removing it is the
       complete restore.
    ✓  Clicks hit the overlay instead of the display, the overlay says why it is there,
       and deleting it puts the panel back.

    ✗  Nothing at capture time separates a useful name from a useless container name.
    ✓  At capture time a container and a real button are both just a successful lookup.

**Cut the trailing clause that restates the sentence.** The pattern is an em-dash followed by a
clause that sounds like it explains the sentence and only repeats it in a more pleased-with-itself
register. If the clause has no second term — "which is what X would do", where X does no such
thing — it is decoration, not explanation. High em-dash density is the symptom; the clauses are
the disease.

**Say it once.** A restatement three paragraphs after the original, dressed up to feel like it is
adding something, is the same failure at paragraph scale. If the point is already made, the
sentence goes.

**No rhetoric against a doubt nobody raised.** "Not a matter of trying harder" answers an
accusation the reader has not made. Say what is true instead: what would have to change for the
thing to become possible.

**Headings name the content; they do not praise it.** "Known limits", not "Limits, stated rather
than discovered".

**Use first person where you are admitting something.** "I haven't chased it down" is a person
talking. An impersonal register applied to an admission reads as evasion, and applied to
everything reads as a machine.

**In code comments the same rule is sharper**, because the code already states the *what*. A
comment earns its place by showing the case the code cannot: the input that breaks, the ordering
that matters, the thing that was tried and did not work. "Replaying 'next page' three times into
a panel that reset to page one is three random inputs" is a comment worth having. "Handles the
edge case correctly" is not.

## What the imported record says that no longer holds

`record/pointer-forwarding/CLAUDE.md` was written for the standalone playground and is kept as
an artifact rather than rewritten. Two parts of it are now wrong from inside this fork:

- **The related-repositories table.** It describes `../fscopilot` as a separate project to read
  and not modify. That repository is the one this file is in.
- **`module/`, `npm run module:build`, `npm run module:derive`, and the `fscopilot-bridge`
  conflict.** All of that concerned the standalone prototype package, which the production
  implementation replaced. It is history, described properly in
  `record/pointer-forwarding/docs/10-module.md`.

Its record-keeping rules — read `plan.md` and the log first, record findings as they happen,
amend design docs by pointer and never silently, a negative result is a result — all still
hold for work on the record.
