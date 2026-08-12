# Platform-Scoped CI Invariants

Status: deferred until after v1.1.1; use the existing review-invariant system

## Goal

Before promoting a review finding or regression into a permanent CI invariant,
classify its contract scope as universal, Windows-specific, Linux-specific,
macOS-specific, backend-specific, or environment/runtime-specific.

A failure observed on one OS is not automatically an OS-specific contract, and
a platform-specific failure must not become a universal repository rule. A
genuinely universal invariant must cover every selectable implementation rather
than one convenient backend or platform.

## Integration Boundary

Determine how scope classification fits the existing
`docs/testing/review-invariant-corpus.json`, policy and runner. Do not create a
second corpus, runner, gate framework or platform registry.

Classification must be derived from root cause and external/product contract
evidence, not simply the runner where a bug first appeared. Missing or uncertain
evidence remains unresolved rather than being labeled to escape coverage.

## Adversarial Proof

The eventual gate must prove that:

- a Windows-only behavior is not imposed on Linux or macOS;
- a universal invariant cannot be mislabeled platform-specific to avoid
  coverage;
- backend-specific behavior remains isolated from unrelated backends;
- OS of observation alone does not determine scope;
- a universal invariant exercises all relevant selectable backends or has
  explicit evidence for equivalent coverage.

The adversarial fixtures must test the classification behavior itself, not only
the presence of metadata strings.

## Acceptance

- every new scope kind has a precise evidence requirement;
- the existing corpus remains the single invariant owner;
- deterministic PR checks stay light, while real-binary/platform stress stays
  in the existing Main or rehearsal profiles;
- architecture tests fail when scope metadata is missing, stale, contradictory
  or used to evade required coverage;
- documentation and executable behavior agree.

## Rollback

Revert the classification extension and its adversarial fixtures together. Do
not leave metadata that the existing runner cannot enforce.
