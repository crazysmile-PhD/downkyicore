# Module Boundary Ratchets

The module-boundary baseline is a decreasing upper bound, not a suppression or
target architecture. Existing debt may disappear or shrink; a new violation or
growth of a bounded oversized owner fails.

## Why The Baseline Is Not Permanently Red

Merging an architecture test that always fails would hide later regressions
behind known debt. A ratchet keeps main green while preventing the accepted
maximum from increasing. This rejected-alternative rationale is the information
owned by this document.

## Executable Authority

- Current rules and baseline entries:
  [ModuleBoundaryBaselineTests.cs](../../tests/DownKyi.Architecture.Tests/ModuleBoundaryBaselineTests.cs).
- Reproducible inventory:
  [audit-module-boundaries.ps1](../../script/audit-module-boundaries.ps1).
- Architecture intent: [ARCHITECTURE.md](../../ARCHITECTURE.md).

When a reported violation is new, fix source rather than extending the
baseline. A necessary external-protocol or generated-code exception requires an
existing design owner and deterministic guard. When debt disappears, remove its
baseline entry.

Do not maintain a second list of zeroed boundaries, file sizes or current
violations in Markdown; query the executable authority and generated audit.
