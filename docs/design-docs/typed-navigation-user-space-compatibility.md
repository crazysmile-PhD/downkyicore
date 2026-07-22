# Typed Navigation And User-Space Compatibility

Status: implemented and verified by PR #82
Last reviewed: 2026-07-22
Supersedes behavior proposed by: PR #75 and PR #77

## Decision

PR #75 and PR #77 target the removed Prism navigation stack and cannot be merged or rebased. Their valid behavior is reimplemented through `IAppNavigationService`, `AppRoute`, `AppNavigationRequest`, Microsoft DI, and CommunityToolkit commands.

## Navigation Contract

Main-region back commands perform page-specific cancellation or cleanup, then call `TryNavigateBack()`. A successful history operation restores the original previous instance. `ParentRoute` is used only when there is no previous history entry.

```text
A -> B -> C
Back: dispose C, restore original B
Back: dispose B, restore original A
Result: no forward record and empty back history
```

`BackNavigationTests`, `BackNavigationArchitectureTests`, and the Host/XAML smoke test protect command behavior, structural coverage, instance identity, lifecycle callback counts, disposal, and history shrinkage.

## User-Space Contract

`IUserSpaceLoadCoordinator` owns profile and public-favorite API loading. `ViewUserSpaceViewModel` receives a `UserSpaceSnapshot`, projects non-empty folders into a typed `UserSpaceFavorites` tab, and preserves the existing projection when navigation returns to the same MID.

```text
UserSpace(mid)
  -> UserSpaceFavorites(folders)
  -> PublicFavorites(folderId)
  -> Back restores UserSpaceFavorites
  -> Back restores the original UserSpace(mid) instance
```

Public favorite API failure is optional profile metadata: known HTTP or schema failures are logged without URL, account, or path data and produce no folder tab. Caller cancellation remains cancellation and is never downgraded to an empty result.

## Unavailable Favorites

The Bilibili `attr` field and exact masked title preserve unavailable status. Such rows remain visible, keep their available metadata, and cannot be selected, opened, selected by Select All, or converted to download items. `FavoritesSelectionPolicy` is the single selection/download filter.

## Theme Isolation

Navigation icons are factories, not mutable singletons. Each ViewModel receives an independent arrow geometry and the public-favorites arrow resolves its fill from a dynamic theme resource.

## Compatibility And Rollback

This gate does not change JSON property names, settings, SQLite schema, download records, aria2 state, or continuation files. Roll back by reverting the Gate 1 commits together; no data migration is required.

## Verification

- Strict Release build with all analyzers and warnings as errors.
- Full solution tests, including deterministic navigation, mapping, selection, theme-isolation, architecture, and Host/XAML smoke tests.
- `dotnet format --verify-no-changes`.
- `script/audit-module-boundaries.ps1` with no ratchet growth.
- `git diff --check`.
