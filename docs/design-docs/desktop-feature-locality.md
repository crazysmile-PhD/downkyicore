# Desktop Feature Locality

Status: accepted target design; implementation deferred until v1.1.1 completes
Last reviewed: 2026-08-11
Evidence baseline: `912949735733c986bcfeefaa4300a5fdb25c907e`

## Scope

This decision covers Desktop routed-feature identity, Shell menu metadata and
route-manifest completeness. It does not redesign the router, DI container,
Avalonia presentation ownership or unrelated download/runtime boundaries.

The baseline counts, current numeric mappings, reachability observations and
file estimates are snapshots owned by
`../exec-plans/desktop-feature-locality.md`, not permanent facts in this
document.

## Current Ownership

- `AppRoute` in `DownKyi.Application` is the sole navigation identity authority.
- `AvaloniaNavigationService.GetViewModelType` owns route-to-ViewModel mapping.
- `DesktopComposition` owns production DI registrations.
- `App.axaml` owns Avalonia ViewModel-to-View presentation templates.
- Shell ViewModels own local display order, titles, icons and selection state.

These are different responsibilities. Their separation is intentional. The
architecture problem is not that they live in different files; it is that
their completeness is not yet protected as one executable cross-owner
contract.

## Stable Invariants

1. `AppRoute` is the only routed-feature identity. Visual position, integer ID,
   title, resource key and legacy `Tag` cannot define navigation identity.
2. Every route maps to exactly one ViewModel; that ViewModel is resolvable from
   production DI and has exactly one presentable View mapping.
3. Shell display metadata may remain locally owned, but each navigable entry
   carries its typed route directly. A second integer-to-route switch is not an
   acceptable authority.
4. Parent route and request payload remain caller/workflow facts. They are not
   forced into a global feature descriptor when they vary by entry path.
5. Existing `IAppNavigationService`, Microsoft DI and Avalonia DataTemplates
   remain the execution owners. No second router, DI container or global
   feature registry is introduced.
6. Legacy `Tag` values may be removed only after symbol-complete proof shows no
   remaining diagnostic, binding or compatibility consumer.

## Target Design

Simple Shells use a minimal local descriptor whose route and presentation
metadata are declared together:

```csharp
internal sealed record ShellNavigationEntry(
    AppRoute Route,
    string TitleKey,
    object? Icon = null);
```

The exact shape may remain Shell-specific. Stateful Shells may add typed
payload factories or typed state, but they must not create a second router or
move DI/DataTemplate ownership into the descriptor.

```text
Shell entry
  -> AppNavigationRequest(AppRoute, typed payload where available)
  -> IAppNavigationService
  -> route-to-ViewModel adapter
  -> Microsoft DI
  -> Avalonia DataTemplate
```

## Rejected Alternatives

### Global FeatureRegistry

Rejected for the current architecture. It would combine Application identity,
Desktop composition, presentation templates, local display metadata and
caller-specific parent/payload facts. The central completeness problem belongs
in an invariant Gate, not a mega descriptor.

### Feature Modules Or A Second Router

Rejected as disproportionate to the remaining debt and incompatible with the
existing typed-navigation ownership. The migration must extend the current
owners rather than create parallel feature ownership.

### Numeric Mapping With Synchronization Tests

Rejected as a target. Tests that compare a menu ID list with a separate switch
would preserve duplicated authority. The duplicate mapping should disappear.

## Migration Dependency

The implementation is deliberately split and ordered:

```text
PR A: route/VM/DI/View completeness and reachability evidence
  -> PR B: simple Shell descriptors
  -> PR C: stateful Shell identity and payload migration
  -> PR D: proven legacy cleanup and architecture ratchets
```

PR A does not delete routes or change product behavior. PR D cannot remove a
legacy route or `Tag` merely because it looks unused; it requires the evidence
produced by earlier PRs.

## Compatibility And Rollback

The migration does not change settings JSON, SQLite data, download records,
resume state, Bilibili contracts or route numeric values. Each PR remains
independently revertible. If a Shell migration regresses selection or back
navigation, revert that Shell's commits without replacing the typed router.
