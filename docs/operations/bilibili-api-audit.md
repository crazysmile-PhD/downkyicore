# Bilibili API Contract Audit

Audit date: 2026-07-29
Last targeted contract update: 2026-08-09 (PR #120 danmaku sequence bound)
Code baseline: v1.1.0 integration candidate `355ef7cb773b3dff67cf5adc56fba942ba77fcf5`
Scope: every fixed Bilibili HTTP endpoint under `DownKyi.Core/BiliApi`, plus the authenticated read-only subset used by account workflows

This is a runtime contract inventory, not an assertion that undocumented Bilibili APIs are stable. Live authentication is optional, explicit, read-only, and operator supplied. No credential, request header, raw response, query identifier, or account value is persisted.

## Evidence And Status

- **LIVE**: anonymous controlled request on 2026-07-22. Only HTTP status, API code/message, top-level keys, content type and byte count were inspected.
- **AUTH-LIVE**: authenticated read-only request most recently repeated on 2026-07-29. The hard gate required `/nav` code 0 and `data.isLogin=true`; only the allowlisted sanitized contract fields in [`bilibili-authenticated-api-audit.json`](bilibili-authenticated-api-audit.json) were persisted.
- **YT**: current [yt-dlp Bilibili extractor](https://github.com/yt-dlp/yt-dlp/blob/master/yt_dlp/extractor/bilibili.py).
- **NEMO**: maintained bilibili-api endpoint maps for [users](https://github.com/Nemo2011/bilibili-api/blob/main/bilibili_api/data/api/user.json), [favorites](https://github.com/Nemo2011/bilibili-api/blob/main/bilibili_api/data/api/favorite-list.json), [videos](https://github.com/Nemo2011/bilibili-api/blob/main/bilibili_api/data/api/video.json), [bangumi](https://github.com/Nemo2011/bilibili-api/blob/main/bilibili_api/data/api/bangumi.json), and [login](https://github.com/Nemo2011/bilibili-api/blob/main/bilibili_api/data/api/login.json).
- **IMPL**: pinned maintained implementations used as protocol corroboration, including [yutto danmaku metadata enumeration](https://github.com/yutto-dev/yutto/blob/30eb998c8c711a92c3a74ac24d54da8cfef510e7/src/yutto/api/danmaku.py) and [bilix danmaku metadata enumeration](https://github.com/HFrost0/bilix/blob/bb5b234cdfe3fafc4db9d992b91091f2edf791e5/bilix/sites/bilibili/api.py).
- **DOC**: community protocol documentation retained as historical evidence, including [history/watch-later](https://github.com/SocialSisterYi/bilibili-API-collect/tree/master/docs/historytoview). The former protobuf-danmaku link is no longer treated as maintained evidence because that repository's active documentation tree was retired.
- **FIXTURE**: deterministic local JSON/protobuf/loopback contract test. Tests never call production Bilibili.
- **Active** means a current application workflow calls the endpoint. **Compatibility** means the public facade remains but no current workflow calls it.
- **Auth-deferred** means a personal Cookie would be required for a conclusive live payload check. No maintainer Cookie was read, copied, logged, or sent by this audit.

Status values:

- `current`: current sources and/or live response agree with the implemented contract.
- `fixed`: a confirmed runtime contract defect was corrected in this gate.
- `auth-deferred`: current sources agree, but payload validation requires a user session.
- `legacy-working`: live today, but a newer contract exists and migration needs more ownership or fixture work.
- `retired-unused`: live request confirms retirement and no production caller remains.
- `invalid-unused`: the request contract is wrong, but no production caller exists and the replacement cannot be safely validated anonymously.

## Bootstrap, Login And Account

| Endpoint | Owner and purpose | Contract/auth | Status and evidence | Decision and regression coverage |
|---|---|---|---|---|
| `api.bilibili.com/x/frontend/finger/spi` | `BilibiliBuvidProvider`; single-flight public buvid bootstrap for injected API requests | GET, `data.b_3/b_4`, anonymous | `fixed/current`; LIVE returned HTTP 200/code 0 | Keep. Exact `code`/`data` wire names, missing values, failed-load retry, concurrent sharing and per-waiter cancellation have deterministic Infrastructure tests. |
| `api.bilibili.com/x/web-interface/nav` | `UserInfo.GetUserInfoForNavigation`; login snapshot and WBI image keys | GET, `data`; anonymous response is code `-101` but still carries public `data.wbi_img` | `fixed/current`; LIVE reproduced anonymous `-101`; AUTH-LIVE returned HTTP 200/code 0 with `isLogin=true`; YT also obtains WBI keys here | Only this endpoint may deserialize code `-101`. `UserNavigationContractTests` proves keys survive and all other endpoints still reject `-101`. |
| `api.bilibili.com/x/space/myinfo` | `UserInfo.GetMyInfo`; current account details | GET, `data`, Cookie required | `current`; AUTH-LIVE returned HTTP 200/code 0 and the required account envelope without persisting any values; NEMO marks authenticated | Keep. Generic nonzero-code rejection and required-payload behavior apply. |
| `passport.bilibili.com/x/passport-login/web/qrcode/generate` | `LoginQr.GetLoginUrl`; create an isolated QR login session | GET, `data.url/qrcode_key`, anonymous | `current`; LIVE code 0; NEMO login map agrees | Keep. Probe never emits the generated key or URL. Deterministic tests keep login cookies synthetic. |
| `passport.bilibili.com/x/passport-login/web/qrcode/poll` | `LoginQr.GetLoginStatus`; poll QR state and capture response cookies | GET, `data`, generated key required; successful response may set session cookies | `fixed/current`; NEMO login map agrees; full live poll intentionally not persisted | The same isolated session owns generate, poll and HTTPS Bilibili callback hops. The exact `passport.biligame.com/x/passport-login/web/crossDomain` landing terminates traversal without receiving a request. Parent-domain poll/callback cookies override legacy landing-query values, are atomically persisted with encoding provenance, reloaded and accepted only after `/nav` returns `isLogin=true`; failed or canceled validation restores the previous file. Deterministic coverage is in `BilibiliLoginSessionTests`, `LoginHelperTests` and `LoginCoordinatorTests`. |

## Ordinary Video, Playback And Media Metadata

| Endpoint | Owner and purpose | Contract/auth | Status and evidence | Decision and regression coverage |
|---|---|---|---|---|
| `api.bilibili.com/x/web-interface/wbi/view` | `VideoInfo.VideoViewInfo`; ordinary video metadata | GET, WBI, `data` | `current`; NEMO and YT agree; `BV1U7V66FEiK` fixture covers identity/pages | Keep. `VideoView.Data` is nullable and missing payload throws. |
| `api.bilibili.com/x/web-interface/archive/desc` | `VideoInfo.VideoDescription`; description | GET, `data` string, public | `current`; LIVE code 0; NEMO agrees | Keep. Empty/missing required payload remains distinguishable. |
| `api.bilibili.com/x/player/pagelist` | `VideoInfo.VideoPagelist`; page/CID list | GET, `data[]`, public | `current`; LIVE code 0; NEMO/YT agree | Keep. `BV1U7V66FEiK` page fixture covers CID parsing. |
| `api.bilibili.com/x/web-interface/view/detail/tag` | `VideoInfo.GetBiliTagInfo`; optional movie tags | GET, `data[]`, public/partly restricted | `current`; LIVE code 0 | Keep. Tag failures are optional metadata; cancellation still propagates. |
| `api.bilibili.com/x/player/wbi/v2` | `VideoStreamApi.PlayerV2`; subtitles and player metadata | GET, WBI, `data` | `current`; NEMO and YT agree | Keep. Player payload is required; subtitle tests use deterministic responses. |
| `api.bilibili.com/x/player/wbi/playurl` | `VideoStreamApi.GetVideoPlayUrl`; ordinary video streams | GET, WBI, `data` | `current`; NEMO/YT agree; fixed playback fixtures | Keep. `PlayUrlEnvelopeContractTests` rejects missing or empty `data`. |
| `api.bilibili.com/x/v2/dm/web/view` | `DanmakuProtobuf`; obtains the finite segment bound before requested ASS artifact enumeration | GET, binary `DmWebViewReply`, semi-anonymous | `fixed/current-production`; checked-in protobuf plus pinned yutto/bilix implementations use `dm_sge.total` | Keep. Missing or invalid `dm_sge.total` is a protocol failure; the client must not guess a bound from segment contents. `DanmakuSegmentContractTests` covers zero, missing/malformed metadata and an empty interior bucket. |
| `api.bilibili.com/x/v2/dm/web/seg.so` | `DanmakuProtobuf` via `BilibiliDanmakuConverter` and `DownloadArtifactWriter`; requested ASS artifact segments | GET, binary `DmSegMobileReply`, semi-anonymous | `fixed/current-production`; LIVE returned HTTP 200 protobuf; IMPL enumerates exactly the metadata-advertised range | Keep for the active artifact pipeline. Request exactly `segment_index=1..dm_sge.total`; an empty segment is a valid quiet bucket and is not EOF. HTTP, IO and malformed metadata/segment protobuf failures remain typed artifact failures. The WBI alternative requires a separate ownership migration. |

Dynamic media dependencies are not fixed API endpoints: subtitle JSON addresses come from `PlayerV2`, media addresses come from `PlayUrl`, and the ordinary-video web fallback reads `www.bilibili.com/video/...`. They must remain opaque response values and must never be copied into diagnostic logs.

## Bangumi And Cheese

| Endpoint | Owner and purpose | Contract/auth | Status and evidence | Decision and regression coverage |
|---|---|---|---|---|
| `api.bilibili.com/pgc/review/user` | `BangumiInfo.BangumiMediaInfo`; media-to-season resolution | GET, `result` | `current`; YT/community implementations retain the media/season route | Keep. Optional `result` remains nullable and required by the caller. |
| `api.bilibili.com/pgc/view/web/season` | `BangumiInfo.BangumiSeasonInfo`; season/episode metadata | GET, `result` | `current`; LIVE code 0 for multiple public episodes; YT agrees | Keep. Public episode fixtures cover field selection. |
| `api.bilibili.com/pgc/player/web/v2/playurl` | `VideoStreamApi.GetBangumiPlayUrlAsync`; bangumi streams | GET, positive `ep_id` required, `result.video_info`; auth/region rules vary | `fixed`; LIVE code 0 for ep 21495, 50188 and 678060; YT and NEMO use v2 | Replaced legacy v1 runtime URL. Page parsing and persisted download playback both preserve the episode identity. The contract rejects missing `result.video_info`, explicit null playback fields and an empty DURL/DASH result; DURL and DASH remain alternative valid formats. |
| `api.bilibili.com/pugv/view/web/season` | `CheeseInfo.CheeseViewInfo`; course metadata | GET, `data`; access varies | `current`; LIVE endpoint responds; NEMO/fixtures agree | Keep. Required `data` failure is typed. |
| `api.bilibili.com/pugv/view/web/ep/list` | `CheeseInfo.CheeseEpisodeList`; course pages | GET, `data` | `current`; LIVE endpoint responds; NEMO agrees | Keep. Pagination remains explicit. |
| `api.bilibili.com/pugv/player/web/playurl` | `VideoStreamApi.GetCheesePlayUrl`; course streams | GET, `data`, `ep_id` required | `current`; LIVE endpoint responds; YT uses the same route | Keep. `PlayUrlEnvelopeContractTests.CheeseEndpointUsesDataEnvelope` fixes the endpoint contract. |
| `api.bilibili.com/pugv/app/web/season/page` | `UserSpace.GetCheese`; courses published by a user | GET, `data.items` | `current`; NEMO user map agrees | Keep. Active user-space flow; live payload may vary by account/catalog. |
| `api.bilibili.com/x/space/bangumi/follow/list` | `UserSpace.GetBangumiFollow`; followed shows | GET, `data`, visibility/login dependent | `current`; NEMO user map agrees | Keep. Active paging coordinator preserves nonzero API failures. |

## Favorites, History And Personal Lists

| Endpoint | Owner and purpose | Contract/auth | Status and evidence | Decision and regression coverage |
|---|---|---|---|---|
| `api.bilibili.com/x/v3/fav/folder/info` | `FavoritesInfo.GetFavoritesInfo`; folder metadata | GET, `data` | `current`; NEMO and YT agree | Keep. Required payload. |
| `api.bilibili.com/x/v3/fav/folder/created/list` | `FavoritesInfo.GetCreatedFavorites`; paged folders | GET, `data.list` | `current`; LIVE and AUTH-LIVE returned HTTP 200/code 0 with the expected list envelope | Keep paged API. NEMO favors `created/list-all`, but anonymous `list-all` returned null for public probes, so replacement evidence is insufficient. |
| `api.bilibili.com/x/v3/fav/folder/collected/list` | `FavoritesInfo.GetCollectedFavorites`; subscribed folders | GET, `data.list`, Cookie/visibility dependent | `current`; AUTH-LIVE returned HTTP 200/code 0 with the expected list envelope; NEMO agrees | Keep. |
| `api.bilibili.com/x/v3/fav/resource/list` | `FavoritesResource.GetFavoritesMediaResource`; folder content and keyword search | GET, `data.medias/has_more` | `current`; AUTH-LIVE, NEMO and YT agree; search fixtures cover `has_more` | Keep. Search pagination does not trust the unfiltered folder total. |
| `api.bilibili.com/x/v3/fav/resource/ids` | `FavoritesResource.GetFavoritesMediaId`; resource identities | GET, `data[]` | `current`; AUTH-LIVE, NEMO and YT agree | Keep. Required payload semantics apply. |
| `api.bilibili.com/x/web-interface/history/cursor` | `HistoryApi.GetHistory`; watch history | GET, `data.cursor/list`, Cookie required | `current`; AUTH-LIVE returned HTTP 200/code 0 with both required fields; anonymous LIVE returned `-101`; NEMO and DOC agree | Keep. Cancellation and typed API errors are preserved. |
| `api.bilibili.com/x/v2/history/toview` | `ToView.GetToView`; watch later | GET, `data.count/list`, Cookie required | `current`; AUTH-LIVE returned HTTP 200/code 0 with both required fields; DOC and maintained UI implementations still use it | Keep `/x/v2/history/toview`. The `/web` alternative has no demonstrated contract advantage. |

## User Space, Collections And Relations

| Endpoint | Owner and purpose | Contract/auth | Status and evidence | Decision and regression coverage |
|---|---|---|---|---|
| `space.bilibili.com/ajax/settings/getSettings` | `UserSpace.GetSpaceSettings`; space banner settings | GET, legacy `{status,data}` | `legacy-working`; LIVE returned HTTP 200 with expected envelope | Keep while active. No maintained replacement with equivalent banner semantics was confirmed. |
| `api.bilibili.com/x/space/wbi/acc/info` | `UserInfo.GetUserInfoForSpace`; public profile | GET, WBI, `data` | `current`; NEMO agrees; unsigned LIVE control was risk-rejected | Keep. WBI provider owns refresh/retry; schema failure is typed. |
| `api.bilibili.com/x/space/wbi/arc/search` | `UserSpace.GetPublicationPage`; publications and search | GET, WBI, `data` | `current`; NEMO/YT agree; deterministic publication fixtures | Keep. Gate 2 tests cover query/page retention and exact totals. |
| `api.bilibili.com/x/polymer/web-space/seasons_series_list` | `UserSpace.GetSeasonsSeries`; collection index | GET, `data` | `current`; LIVE code 0; NEMO/YT agree | Keep. This is the replacement family for retired channels. |
| `api.bilibili.com/x/polymer/web-space/seasons_archives_list` | `UserSpace.GetSeasonsDetail`; season collection pages | GET, `data` | `current`; NEMO/YT agree | Keep. Typed `SeasonsSeriesKind` selects this route. |
| `api.bilibili.com/x/series/series` | `UserSpace.GetSeriesMeta`; series metadata | GET, `data` | `current`; LIVE endpoint responds; NEMO/YT agree | Keep. |
| `api.bilibili.com/x/series/archives` | `UserSpace.GetSeriesDetail`; series pages and `/list/<mid>?sid=...` family | GET, `data` | `current`; NEMO/YT agree | Keep. Bare `/list/<mid>` remains publication navigation; `sid` must use typed series input before enabling. |
| `api.bilibili.com/x/space/channel/list` | compatibility `UserSpace.GetChannelList` | GET, former `data.list` | `retired-unused`; LIVE HTTP 404; no production caller | Do not invoke or silently redirect because channel IDs do not map one-to-one to seasons/series. Retain public compatibility surface until the planned legacy-removal gate. |
| `api.bilibili.com/x/space/channel/video` | compatibility `UserSpace.GetChannelVideoList` | GET, former `data.list` | `retired-unused`; endpoint family is retired; no production caller | Same decision as channel list. Use polymer/series APIs in active flows. |
| `api.bilibili.com/x/relation/stat` | `UserStatus.GetUserRelationStat`; following/follower counts | GET `vmid`, `data` | `current`; LIVE code 0; NEMO agrees | Keep active numeric-ID contract. The unused `Nickname.CheckNickname` query against this route returned `-400` and is classified `invalid-unused`; no anonymous name lookup replacement was proven. |
| `api.bilibili.com/x/space/upstat` | `UserStatus.GetUpStat`; view/like totals | GET, `data` | `current`; LIVE code 0; NEMO agrees | Keep. Cancellation ownership remains Gate 7 HTTP work. |
| `api.bilibili.com/x/relation/followers` | `UserRelation.GetFollowers`; followers | GET, `data.list/total`, visibility/login limits | `current`; AUTH-LIVE returned HTTP 200/code 0 with the expected envelope; NEMO agrees | Keep compatibility and coordinator path. API-imposed page limits remain visible. |
| `api.bilibili.com/x/relation/followings` | `UserRelation.GetFollowings`; following list | GET, `data.list/total`, visibility/login limits | `current`; AUTH-LIVE returned HTTP 200/code 0 with the expected envelope; NEMO agrees | Keep. |
| `api.bilibili.com/x/relation/whispers` | `UserRelation.GetWhispers`; private follows | GET, `data.list`, Cookie required | `current`; AUTH-LIVE returned HTTP 200/code 0 with the expected envelope; NEMO agrees | Keep. |
| `api.bilibili.com/x/relation/blacks` | `UserRelation.GetBlacks`; block list | GET, `data`, Cookie required | `current`; AUTH-LIVE returned HTTP 200/code 0 with the expected envelope; NEMO agrees | Keep. |
| `api.bilibili.com/x/relation/tags` | `UserRelation.GetFollowingGroup`; own groups | GET, `data`, Cookie required | `current`; AUTH-LIVE returned HTTP 200/code 0 with the expected envelope; NEMO agrees | Keep. |
| `api.bilibili.com/x/relation/tag` | `UserRelation.GetFollowingGroupContent`; group members | GET, `data`, Cookie required | `current`; AUTH-LIVE returned HTTP 200/code 0 with the expected envelope; NEMO agrees | Keep. |

## Compatibility Discovery APIs

| Endpoint | Owner and purpose | Contract/auth | Status and evidence | Decision and regression coverage |
|---|---|---|---|---|
| `api.bilibili.com/x/web-interface/ranking/region` | `Ranking.RegionRankingList`; regional ranking | GET, `data[]` | `legacy-working`; LIVE code 0; no production caller | Retain compatibility only. Current product has no ranking workflow, so replacing it would add untested behavior. |
| `api.bilibili.com/x/web-interface/dynamic/region` | `DynamicApi.RegionDynamicList`; regional dynamic list | GET, `data` | `legacy-working/risk`; LIVE returned API `-404` for an empty sample; no production caller | Retain compatibility and typed failure. Do not interpret an empty region as endpoint retirement. |

## Authenticated Read-Only Snapshot

The latest 2026-07-29 operator run completed at `2026-07-29T14:54:07.3957098+08:00` against v1.1.0 integration candidate `8aa4382024aa0af15b472956bbb3ee51de73622a`. It reloaded `BILIBILI_TEST_COOKIE` from `~/.codex/.env` inside an isolated PowerShell process. The value was never printed, persisted, hashed, copied into a fixture, or passed through command-line arguments.

- Navigation hard gate: HTTP 200, Bilibili code 0, `data.isLogin=true`.
- Contract probes: 14 passed, 0 failed, 0 blocked, 0 indeterminate.
- Covered workflows: current-account envelope, history, watch later, created and collected favorites, favorite resources and IDs, followers, followings, private follows, block list, following groups and group content.
- Persisted evidence: only API name/path, HTTP status, Bilibili code, login requirement, structure/field/drift booleans, outcome and sanitized error type.
- Raw response bodies, request headers, query values and account values were discarded in-process.
- Secret scan: Gitleaks 8.30.1 inspected 986 tracked and non-ignored untracked candidate files and reported zero findings after the sanitized artifact was updated.

The machine-readable artifact is [`bilibili-authenticated-api-audit.json`](bilibili-authenticated-api-audit.json). It is a sanitized evidence snapshot, not a test fixture and not an authorization token.

## Confirmed Changes

1. Anonymous navigation now accepts only API code `-101` at the `/nav` contract, then requires `data`. This repairs WBI bootstrap for public videos without weakening global API error handling.
2. Bangumi playback uses `/pgc/player/web/v2/playurl`, requires a positive `ep_id`, and explicitly selects `result.video_info`. Both page playback and persisted download playback carry the episode identity. The v2 DTO does not create a default envelope payload; explicit null playback fields and an all-empty DURL/DASH result fail with typed diagnostics while either non-empty format remains valid by itself.
3. The audit records the retired channel family, invalid nickname query, legacy danmaku path, and watch-later ambiguity without speculative remapping.
4. `BilibiliApiInventoryArchitectureTests` fails when a fixed Core endpoint is not present in this document or when the `/nav` nonzero-code exception spreads to another source file.
5. `script/audit-bilibili-api.ps1 -ConfirmLive` reproduces the anonymous subset and outputs only sanitized diagnostics. It is an operator tool, not a CI test.
6. `script/audit-bilibili-authenticated-api.ps1 -ConfirmAuthenticatedLive` gates authenticated probes on `/nav`, emits an allowlisted schema, and never persists raw account responses.
7. Danmaku artifact enumeration obtains its finite bound from `/x/v2/dm/web/view`; empty segment payloads no longer truncate later advertised buckets.

## Deterministic Tests

- `UserNavigationContractTests.AnonymousNavigationResponsePreservesPublicWbiMetadata`
- `UserNavigationContractTests.AnonymousCodeRemainsRejectedOutsideTheNavigationContract`
- `PlayUrlEnvelopeContractTests.BangumiEndpointUsesResultVideoInfoEnvelope`
- `PlayUrlEnvelopeContractTests.BangumiEndpointRejectsInvalidEpisodeIdBeforeRequest`
- `PlayUrlEnvelopeContractTests.BangumiV2MissingVideoInfoThrowsTypedContractFailure`
- `PlayUrlEnvelopeContractTests.BangumiV2NullPlaybackFieldThrowsTypedMalformedFailure`
- `PlayUrlEnvelopeContractTests.BangumiV2EmptyPlaybackCollectionsThrowTypedEmptyFailure`
- `PlayUrlEnvelopeContractTests.BangumiV2DashOnlyPayloadRemainsValid`
- `BangumiEpisodeIdentityTests.InfoServicePassesPageEpisodeIdToPlaybackRequest`
- `BangumiEpisodeIdentityTests.DownloadResolverPassesPersistedEpisodeIdToPlaybackRequest`
- existing `PlayUrlEnvelopeContractTests` for ordinary video and cheese
- existing `BvFixtureContractTests` for `BV1U7V66FEiK`
- `DanmakuSegmentContractTests.AdvertisedSegmentCountIncludesEmptyInteriorSegments`
- `DanmakuSegmentContractTests.ZeroAdvertisedSegmentsDoesNotGuessAnExtraRequest`
- `DanmakuSegmentContractTests.MissingSegmentMetadataFailsInsteadOfGuessingTermination`
- `DanmakuSegmentContractTests.MalformedSegmentMetadataFailsInsteadOfGuessingTermination`
- `BilibiliApiInventoryArchitectureTests.EveryHardCodedBilibiliApiEndpointIsRecordedInTheAudit`
- `BilibiliApiInventoryArchitectureTests.AnonymousNonSuccessCodeExceptionIsScopedToNavigation`
- `BilibiliApiInventoryArchitectureTests.OptionalJsonEnvelopeFieldsCannotInventPayloads`
- `BilibiliApiInventoryArchitectureTests.LiveProbeIsExplicitAndDoesNotLoadCookies`
- `BilibiliApiInventoryArchitectureTests.AuthenticatedLiveProbeArtifactIsExplicitAndSanitized`

## Maintenance Procedure

1. Update the endpoint row and deterministic fixture in the same PR as any endpoint or envelope change.
2. Run `pwsh ./script/audit-bilibili-api.ps1 -ConfirmLive` only when an anonymous live audit is intended. It must never load a browser profile, login file, or Cookie.
3. Run `pwsh ./script/audit-bilibili-authenticated-api.ps1 -ConfirmAuthenticatedLive -OutputPath ./docs/operations/bilibili-authenticated-api-audit.json` only with explicit operator approval. The script reads `BILIBILI_TEST_COOKIE` from `~/.codex/.env`; never pass the value on the command line.
4. Treat HTTP success and JSON parse success as insufficient: check API code, required envelope and usable payload.
5. Authenticated live evidence remains optional and must never run in CI or become a release prerequisite. Deterministic tests use synthetic/redacted fixtures.
6. If sources conflict, keep the working contract and record the alternative until equivalent payload behavior is proven.

## Gate 3 Local Verification

- Strict .NET 10 Release build with `AnalysisMode=All`: 0 warnings, 0 errors.
- Full solution tests: 543 passed, 0 failed, 0 skipped.
- Format verification: 0 of 742 files changed.
- NuGet vulnerable and deprecated package reports: empty for every solution project.
- Anonymous live probe: 27 results, no transport failures, no local authentication loaded.
- Module boundary audit and `git diff --check`: passed with no new baseline finding.
