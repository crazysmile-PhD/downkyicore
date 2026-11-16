# Desktop Feature Locality

Status: accepted

## Decision

Do not solve routed-feature completeness with a global `FeatureRegistry`,
feature-module framework or second router. Extend the existing typed-navigation,
Microsoft DI and Avalonia presentation owners, and verify their relationship
with executable tests.

Shell order, title, icon and selection state are local presentation facts.
Parent route and payload are caller/workflow facts. Neither belongs in a global
descriptor beside Application route identity, Desktop composition and Avalonia
templates.

## Why

A global registry would centralize facts that change for different reasons and
already have different authoritative owners. It would become a parallel owner
for navigation, composition and presentation while still needing the existing
router, container and templates. That adds synchronization rather than removing
it.

Cross-owner completeness is instead an executable contract: a typed route maps
to a ViewModel, production DI resolves it, and Avalonia can present it. A Shell
may keep a small local descriptor when that removes a local duplicate, but it
must not become a repository-wide feature manifest.

## Registration And Presentation

The existing navigation and dialog factories own their enum-to-type mappings.
Local composition enumerates those enums and registers the mapped types as
transient services. A second list of concrete types in DI would repeat the same
decision and could omit a page or dialog that the factory already supports.
An unmapped enum value fails during composition instead of waiting for a user
to navigate to it. This derivation does not inspect assemblies or infer types
from filenames.

Avalonia XAML templates remain the presentation owner. The real Host smoke
test resolves every typed route and dialog, checks distinct transient
instances, and builds the single production template matching each page.
Shell order and selection still require their focused behavior tests.

## Design Basis

[Parnas (1972)](https://www.cs.lafayette.edu/~gexia/cs301/resources/parnas.html)
argues for hiding changeable design decisions within modules. Here that
supports keeping route identity, type selection and presentation with their
existing owners while deriving redundant registration facts.
[Murphy, Notkin and Sullivan (1995)](https://www.cs.ubc.ca/~murphy/papers/rm/reflexion_model_fse95.pdf)
compare intended structure with relations extracted from source. We apply
that idea through executable composition and presentation checks; their paper
does not prescribe this DI implementation or prove a measured maintenance gain.

[Avalonia's official sample](https://github.com/AvaloniaUI/Avalonia.Samples/blob/main/src/Avalonia.Samples/DataTemplates/BasicDataTemplateSample/Views/MainWindow.axaml)
also uses explicit XAML data templates for presentation. The
[official documentation](https://docs.avaloniaui.net/docs/data-templates/view-locator)
confirms that a custom ViewLocator is optional. Keeping the existing templates
avoids adding reflection-based name resolution to simplify a registration list.

Current owners, invariants and maintenance proof are documented in
`../../ARCHITECTURE.md` and `../maintenance.md#desktop-host`.
