# aria2 RPC Client Ownership

Status: accepted
Last verified: 2026-07-29

## Classification

`DownKyi.Core/Aria2cNet/Client/AriaClient*` is a hand-maintained in-repository
JSON-RPC adapter. It is not generated during restore/build and is not a copied
NuGet package.

The earliest traceable source is DownKyi commit
[`587fcfb`](https://github.com/tzwlhack/downkyi/commit/587fcfb77744fca9476b13f2e97e46fb78f3d9fb)
from 2020-12-26, whose commit message is `add the aria2cNet sources`. The source
was later renamed under `DownKyi.Core` and entered this repository through the
archived .NET 10 import. Repository-wide code search found the same distinctive
type names only in DownKyi forks; no separate generator or upstream sync source
was found.

The wire contract follows the
[official aria2 JSON-RPC method reference](https://aria2.github.io/manual/en/html/aria2c.html#methods).
Because this is project-owned compatibility code, it is split by protocol
responsibility and protected by deterministic request-contract tests.

## Owners

| File | Responsibility |
| --- | --- |
| `AriaClient.cs` | Immutable endpoint/token, JSON serialization, response decoding, one-attempt HTTP transport |
| `AriaClient.Downloads.cs` | Add/remove and pause/resume methods |
| `AriaClient.Status.cs` | Status/resource queries, queue position, URI replacement |
| `AriaClient.Options.cs` | Per-download and global option methods |
| `AriaClient.Lifecycle.cs` | Statistics, result cleanup, version/session and process lifecycle |
| `AriaClient.System.cs` | JSON-RPC system multicall and method/notification discovery |

All files are partial declarations of the same public `AriaClient`; the split
does not change its public API.

## Stable Contract

- The endpoint is an immutable absolute HTTP/HTTPS URI ending in `/jsonrpc`.
- Each `AriaClient` instance owns its endpoint and secret. No mutable static
  host, port or token state is allowed.
- `aria2.*` calls place `token:<secret>` first in `params`.
- `system.*` calls do not automatically add the aria2 token.
- Request `jsonrpc` remains `2.0`, request IDs remain non-empty and each public
  call emits exactly one physical request.
- Retry decisions belong to `DownloadTransferCoordinator`; this adapter cannot
  add a second retry budget.
- Public method names, parameters and response DTOs are compatibility surface.
  Changing them requires an explicit API migration.
- `ChangeUriAsync` maps to `aria2.changeUri`; it must never regress to
  `aria2.changePosition`.

`AriaClientRpcContractTests` invokes every public RPC method against an injected
capture transport. It verifies that the public method inventory and wire method
inventory are identical, and checks JSON-RPC version, ID, method name and token
placement without contacting aria2.

## Change Procedure

1. Confirm the method contract against the official aria2 manual.
2. Change only the responsibility partial that owns the method.
3. Add or update the corresponding case in `AriaClientRpcContractTests`.
4. Run `AriaClientIsolationTests`, the RPC contract tests, architecture tests,
   strict Release build and the full solution test gate.
5. Update this document and `docs/ai-knowledge-graph.md` if ownership, retry,
   authentication or transport behavior changes.

There is no generated-source refresh command. A future move to generated code
must first introduce a checked-in schema/source version, deterministic generator
command and output-drift CI check.
