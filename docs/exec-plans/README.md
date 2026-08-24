# Execution Plans

Execution plans preserve task-specific scope, unknowns, accepted design
constraints, verification and rollback only when that information cannot be
reconstructed from the final diff and tests.

Current owner-assigned work and interruption checkpoints live in GitHub Issue
#137 and the linked PR. This index does not copy the current item, branch, SHA,
CI state, completed-work history or a hand-maintained list of plan files.

Each active plan states:

- goal and explicit scope boundary;
- affected owner and contracts that must not change;
- executable validation and completion condition;
- rollback or forward-repair procedure;
- disposition when completed, superseded or abandoned.

Design rationale belongs in [design-docs](../design-docs/). Stable architecture
intent belongs in [ARCHITECTURE.md](../../ARCHITECTURE.md). Canonical
verification and rollback commands belong in
[verification-and-rollback.md](../operations/verification-and-rollback.md).

Do not copy plan inventory into this file; query the directory and Git history
on demand.
