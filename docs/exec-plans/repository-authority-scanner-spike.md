# Repository Knowledge IR And Authority Scanner Technology Spike

Status: deferred until after v1.1.1; research and report only

## Goal And Boundary

Evaluate a searchable repository knowledge representation and a report-only
authority/ownership scanner so an Agent can search, locate, expand and fetch a
task-relevant subgraph instead of reading the complete knowledge graph. The
spike must distinguish structural truth from authority truth and cluster many
symptoms into likely root causes.

The spike may add experiment tooling, benchmark harnesses, reports, test-only
fixtures and prototype CI artifacts. It must not fix production findings,
replace runtime owners, add a mutable ownership registry, or begin a knowledge
graph migration. SQLite `ClearPool` is one benchmark/ground-truth fixture, not
a detector-specific rule.

## Candidates

Benchmark all viable candidates on the same DownKyi commit:

1. **RPG/CoderMind candidate**: investigate Microsoft Research RPG/RPG-Encoder,
   CoderMind repository workspaces, SearchNode/Explore/Fetch-style operations,
   functional hierarchy, semantic navigation and incremental graph evolution.
   Prove actual C#/.NET support, path/symbol/concept lookup, search-then-zoom,
   incremental cost and extensibility for ownership semantics.
2. **SCIP-dotnet/Roslyn candidate**: use compiler-grade symbol identity,
   definitions, references, implementations, inheritance and call/use
   relationships. Measure cross-project precision, reachability, helper chains
   and incremental indexing. Do not confuse "who calls" with "who may own".
3. **Hybrid candidate**: test the hypothesis that Roslyn/SCIP supplies
   structural truth, an RPG-like layer supplies functional grouping and
   navigation, and a minimal authority layer supplies lifecycle, destructive
   capability and state-mutation semantics. Adopt it only if measured gains
   justify its maintenance and runtime cost.
4. **Custom candidate only if evidence requires it**: do not build a complete
   custom framework in the first round. A proposed custom layer must identify a
   concrete capability missing from A, B and the hybrid, not merely claim that
   custom code is easier to control.

## Reproducible Benchmark Discipline

Every candidate uses the same:

- repository commit;
- ground-truth and holdout questions;
- model and prompt shape;
- maximum context budget;
- source-access policy;
- execution environment where practical.

The primary variable is the repository representation/retrieval mechanism.
Record the Runtime, OS, architecture, tool versions and commit with every run.

## Benchmark Areas And Ground Truth

The first round covers three areas with different architecture characteristics:

### SQLite And Persistence

Measure provider/external lifecycle ownership, transaction ownership and
persistence ownership. The known `SqliteDownloadTaskStore.Dispose ->
SqliteConnection.ClearPool -> sqlite3_close_v2` path is ground truth for asking
whether a logical store has assumed provider-global lifecycle authority. The
expected model distinguishes store-owned resources and logical operations from
Microsoft.Data.Sqlite pool ownership and native SQLite close machinery.

### Download State

Measure exclusive durable mutation authority, admission/reservation ownership,
Domain state versus UI projection, retry/cleanup boundaries and competing
writers.

### Navigation Identity

Measure canonical typed identity, duplicated numeric/route mappings,
cross-owner completeness and competing identity authorities.

Additional ground-truth questions may cover settings, aria2, FFmpeg/external
processes, filesystem ownership, UI projections and Bilibili/API contracts,
but the first round must not expand merely to make every candidate look complete.

## Holdout And Adversarial Mutations

Known answers already documented in the repository are insufficient. Add
test-only holdout mutations including:

- an indirect provider-lifecycle call through multiple helpers;
- a second direct SQLite writer for durable download state;
- a second numeric-ID-to-route mapping beside the canonical typed identity;
- external process start/kill/dispose from a non-lifecycle owner.

The fixtures must test transitive access and semantic ownership. They must not
reduce to grepping `ClearPool`, names, receivers or a known file path.

## Metrics

For each candidate report:

1. retrieval recall for relevant files, symbols, nodes, contracts, owners and
   call paths;
2. retrieval precision of context delivered to the model;
3. final-answer correctness;
4. authority correctness for owner, non-owner, legitimate shared authority,
   external/provider authority and destructive capability;
5. localization accuracy for the earliest boundary/owner requiring
   investigation;
6. context cost in tokens or equivalent retrieved size;
7. retrieval/tool operation count;
8. indexing and query latency;
9. full and incremental update cost after the same small commit;
10. maintenance burden and required manual synchronization;
11. root-cause compression ratio: raw findings, cluster count, cluster purity,
    incorrect merges and incorrect splits.

## Report-Only Authority Scanner

Prototype a repository-wide scanner that reports strong violation candidates
and ambiguity signals without changing production code.

Strong candidates include destructive/global capability invoked outside its
owner, a second durable writer, forbidden lifecycle/dependency access,
application ownership of provider-global lifecycle, bypass of an exclusive
owner and transitive helper paths to forbidden authority.

Ambiguity signals include multiple writers for mutable state, competing
identity mappings, multiple lifecycle owners, manually duplicated policy,
second global registries/caches, distributed destructive operations, high
fan-in components holding unrelated authority, persistent mutations crossing
module boundaries and disagreement between source topology and documented
ownership. A signal is evidence, not automatically a defect.

## Criticality And External Authority

Combine declared critical areas with inferred criticality.

Declared areas include durable state, SQLite/persistence, download lifecycle,
task transitions, reservation/admission, global/process lifecycle, destructive
filesystem operations, settings persistence, migration and external process
ownership.

Inference considers fan-in/out, persistent mutation, destructive capability,
cross-module reach, lifecycle/global state, external-resource ownership,
dependent-component count and blast radius.

The model must represent `owner-kind: external/provider`, including
Microsoft.Data.Sqlite pools, SQLite native connections, OS processes,
filesystem locks, aria2, FFmpeg, HTTP/client lifetime, Avalonia lifecycle and
external APIs. It must express may-use, may-borrow, may-dispose-own-resource
and may-not-own-global-lifecycle.

## Two-Level Output

Produce:

1. a concise summary of root-cause clusters with cluster ID, severity,
   authority/resource, likely root cause, core status, representative evidence,
   affected modules, raw count, confidence and next investigation entry point;
2. a detailed artifact containing every raw finding, symbol, caller/callee,
   call path, path/member identity, evidence, detector, confidence and cluster.

The summary is the default Agent input. Detailed evidence is fetched only when
investigating a cluster.

## Finding Dispositions

Every investigated ambiguity eventually receives one explicit disposition:

- `CONFIRMED_VIOLATION`;
- `LEGITIMATE_SHARED_AUTHORITY` with rationale/contract;
- `FALSE_POSITIVE` with detector improvement or bounded suppression rationale;
- `SUPERSEDED_BY_ROOT_CAUSE` linking the higher-level explanation.

Do not turn raw findings into one ticket each or use informal "probably fine"
dispositions.

## Knowledge Representation Migration Research

Do not split `docs/ai-knowledge-graph.md` into another manually synchronized
format during this spike. A selected representation must support:

```text
search -> locate node -> explore neighbors -> inspect authority -> fetch source
```

A visualization may be a view, never a second source of truth. A future
migration may not permanently maintain the old graph, a new graph and an
ownership registry describing the same facts.

Before removing the old knowledge graph, an explicit removal gate must prove:

- complete inventory and destination mapping for every stable node,
  responsibility, contract, hazard, owner and test relation;
- path-to-node, symbol-to-node, node-neighbor and authority-owner lookup;
- updated AGENTS/CI/script consumers and no live old-path references;
- adversarial validation of migration completeness.

If completeness is not proven, retain the current representation.

## Selection Rule

Use measured evidence to answer correctness, authority correctness, context
cost, incremental maintenance, manual synchronization, search-zoom-fetch fit,
CI integration and whether any gain justifies added complexity. The only valid
recommendations are:

- `ADOPT_A`;
- `ADOPT_B`;
- `ADOPT_HYBRID`;
- `INSUFFICIENT_EVIDENCE`.

A marginal accuracy gain does not justify a substantially more complex hybrid.
No winner is a valid outcome.

## Deliverables

- reproducible A/B/hybrid technology comparison;
- benchmark harness and dataset with known ground truth plus holdouts;
- full metric report;
- report-only authority scanner prototype;
- summary plus detailed CI artifact prototype;
- root-cause clustering evidence;
- generalized external/provider authority model;
- evidence-based recommendation;
- migration plan only after selection, without replacing the knowledge graph
  in this spike.

## Observed Seeds, Not Confirmed Defects

- SQLite provider pool teardown from a logical task store is a benchmark seed.
- Download durable mutation, reservation ownership and Domain/projection
  boundaries are benchmark seeds.
- Typed versus duplicated navigation identity is a benchmark seed.

Additional resource-authority observations discovered during v1.1.1 may be
added here as bounded evidence seeds. They remain unconfirmed until the spike
classifies them and must not be fixed as part of this research PR.

## Acceptance And Rollback

The spike succeeds when another developer can reproduce the comparison and
the recommendation follows from recorded evidence rather than preference. It
does not succeed merely by finding many warnings or drawing a useful graph.

Rollback removes the experiment tooling and generated reports as one bounded
change. It must not require reverting production behavior because production
findings are outside this spike.
