# Exerciser

A desktop app that sits beside a running FS Copilot and plays the other pilot. One machine,
one simulator, and the feature under test is visible on screen from both ends.

It is general on purpose. Pointer forwarding is its first page; the next feature that needs
testing adds a page instead of a second tool.

| File | What it is |
| --- | --- |
| [docs/01-design.md](docs/01-design.md) | The design, and the decisions still open |
| [docs/log.md](docs/log.md) | What the probes found |
| `probes/` | One directory per question |
| `results/` | Raw probe output |
