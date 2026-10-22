# Agent instructions

## Commits

Commit only when the user asks for it. Never commit automatically: not after finishing a change, not as a checkpoint,
not as part of another task. Leave changes uncommitted until the user requests a commit.

## Commit messages

A commit message states **why** the change was made: the problem it solves, the evidence behind it (measurements,
log lines, the mod or game behavior that forced it) and any constraint that shaped it. It does not restate **what**
changed; the diff already shows that.

- Good: `Urban deaths froze 2.5–18 s: 94% of the sight lines walked during one death repeated earlier ones`
- Bad: `Add LineOfSightTrace.cs, new settings in Settings.cs and mod.json`
