# Traffic and ATC sharing

Two things are missing from a shared cockpit today: the **AI traffic** the ATC add-on injected
on one pilot's machine exists only there, and the **ATC audio** reaches the other pilots only
if someone pipes it into the voice call by hand. This record is about carrying both over the
session that already exists.

## How this record works

Same split as `record/pointer-forwarding`, and the same rules.

**The design record** — `01-02` — is what the work *is*. It is stable and is amended only by
a pointer line at the top naming the log entry that overturned something, never by silent
rewriting. `02-open-questions` is the exception: it is a register and is updated in place.

**The build record** — `build/` — is what is actually being *run* and what the running found.
It is live and it moves.

> **Picking this up fresh?** Read [build/plan.md](build/plan.md) for where each stage
> stands, then the top of [build/log.md](build/log.md), then [01-design](01-design.md).
> Treat the design as amended by the log wherever the two disagree.

## Files

| File | Contents |
| --- | --- |
| [01-design](01-design.md) | The proposal: what is read, what is sent, what is injected, who is allowed to share, and why each choice. |
| [02-open-questions](02-open-questions.md) | Every assumption the design rests on that has not been tested, numbered so the log can settle them. |
| [03-implementation-plan](03-implementation-plan.md) | How the prototype becomes FS Copilot code: decisions locked with the user, packets, file-by-file design, order of work, verification tiers, risks. |
| [build/plan](build/plan.md) | The prototype stages, each with the questions it exists to answer and an exit criterion. |
| [build/log](build/log.md) | Findings, newest first. |
