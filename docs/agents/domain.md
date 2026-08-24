# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Before exploring, read these

- **`CONTEXT.md`** at the repo root: the glossary of domain terms.
- **`docs/adr/`**: read ADRs that touch the area you're about to work in.

Neither exists today. If they don't exist, **proceed silently**. Don't flag their absence; don't suggest creating them upfront. The `/domain-modeling` skill creates them lazily when terms or decisions actually get resolved.

## File structure

This is a single-context repo — one solution, one domain, no workspaces:

```
/
├── CLAUDE.md
├── CONTEXT.md                     ← the glossary
├── docs/
│   ├── adr/                       ← one decision per file, dated, immutable
│   ├── SYNC-PLAN.md               ← planning docs (see below)
│   ├── REMOTE-TRANSPORT-PLAN.md
│   └── …
├── Flower/                        ← all meaningful code
├── Flower.Core/
├── Flower.Server/
└── …
```

A `CONTEXT-MAP.md` at the root would signal a multi-context repo with one `CONTEXT.md` per context. That is not this repo, and splitting it that way would need a real reason — `CLAUDE.md`'s "Project Layout" is the honest map of where code lives.

## `docs/adr/` is not `docs/`

`docs/` already holds long-lived planning notes, one file per initiative, indexed in `CLAUDE.md` — `SYNC-PLAN.md`, `REMOTE-TRANSPORT-PLAN.md`, `OPEN-INTERNET-REVIEW.md` and the rest. Those are *ongoing* records: each carries its own status and what's left, and gets edited as the work proceeds.

An ADR is the opposite shape: one decision, dated, and immutable once made. If a decision changes, a new ADR supersedes the old one rather than editing it.

When a planning doc records a decision worth pinning, the ADR is the pin and the planning doc keeps the narrative. Don't move the planning docs into `docs/adr/`, and don't turn an ADR into a status page.

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in `CONTEXT.md`. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal: either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-0007 (event-sourced orders), but worth reopening because…_
