# Maintainability Ratchets

## Purpose

Maintainability is a separate quality dimension from functional correctness. A change can preserve every known runtime invariant while making future changes harder and increasing the number of states a maintainer must reason about.

The repository therefore treats maintainability as an executable CI contract rather than a review preference.

## Metric gates

The strict build explicitly enables the .NET maintainability code-metric rules that are not enabled by `AnalysisMode=All` in .NET 10:

- `CA1501` — excessive inheritance depth;
- `CA1502` — excessive cyclomatic complexity;
- `CA1505` — low maintainability index;
- `CA1506` — excessive class coupling;
- `CA1509` — invalid code-metrics configuration.

`CodeMetricsConfig.txt` is the single repository configuration for these thresholds. `MaintainabilityArchitectureTests` fails if the rules, configuration file, analyzer registration, strict analyzer mode, or warning-as-error contract disappears.

## Initial thresholds

The first inventory uses the .NET defaults:

- inheritance depth: `5`;
- method cyclomatic complexity: `25`;
- maintainability index: `10`;
- type coupling: `95`;
- method coupling: `40`.

A failing first inventory is evidence of existing debt, not permission to raise a threshold until CI becomes green.

## Existing debt

When the first inventory finds existing violations:

1. inspect the violated symbol and its ownership before changing production code;
2. fix findings that can be reduced without destabilizing external protocol, persistence, UI binding, lifecycle, or public compatibility contracts;
3. if a legacy finding cannot safely be removed in the current PR, preserve the exact measured debt with a narrow ratchet rather than weakening the global metric threshold;
4. a ratchet may stay equal or decrease, never increase;
5. stale ratchet entries fail closed and must be removed;
6. do not suppress a metric with project-wide `NoWarn`, `.editorconfig` `none`/`silent`, broad pragma regions, generated-code relabeling, file moves, helper extraction, or partial-class splitting whose only purpose is to hide unchanged complexity.

## What these metrics do not prove

Passing code metrics does not prove that state ownership is simple. A one-line call can still hide a transition that depends on many independent state dimensions. Retry, cleanup, persistence, source selection, cancellation, backend identity, finalization, and other lifecycle state machines still require invariant-derived transition matrices and adversarial tests.

Likewise, file size is not a substitute for method complexity or coupling. `ModuleBoundaryBaselineTests` continues to guard oversized files and module ownership independently.

## Review rule

A maintainability fix must reduce the reasoning burden, not merely move syntax. Splitting one complex method into several private methods, partial files, delegates, local functions, or helper classes is insufficient when the same owner still requires the same tightly coupled state set to make one transition.

For high-risk orchestration code, reviewers should ask both:

- did structural complexity/coupling decrease or remain within the ratchet; and
- did the number of independent state facts required to decide one transition decrease or remain explicit in a typed transition model?
