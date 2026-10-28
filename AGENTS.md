# Agent instructions

## Commits

Commit only when the user asks for it. Never commit automatically: not after finishing a change, not as a checkpoint,
not as part of another task. Leave changes uncommitted until the user requests a commit.

## Releases

Release only when the user asks, with `scripts/release.ps1 -Tag <tag>` (run `-DryRun` first). The build needs the
game's assemblies, so there is no GitHub Actions workflow. Packages go to `dist/`, which is git-ignored; never commit
zips or build output.

## Installed build

The game's `Mods/PerfeBT/PerfeBT.dll` always holds the build of the branch being worked on, so every play session
tests current work. After each successful build that changes behavior, copy `bin/Release/PerfeBT.dll` there; keep
the previous DLL as `PerfeBT.dll.<version>.bak` once per release. Never overwrite the user's `mod.json` settings.
Install anything else (another branch, `main`) only on the user's direct order.

## Commit messages

A commit message states **why** the change was made: the problem it solves, the evidence behind it (measurements,
log lines, the mod or game behavior that forced it) and any constraint that shaped it. It does not restate **what**
changed; the diff already shows that.

- Good: `Urban deaths froze 2.5–18 s: 94% of the sight lines walked during one death repeated earlier ones`
- Bad: `Add LineOfSightTrace.cs, new settings in Settings.cs and mod.json`
