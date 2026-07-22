# Bilibili API Contract Audit

Audit date: 2026-07-22  
Code baseline: `3fbd31be95bf6e62287bb393b139b853260d36cf` plus Gate 3 changes  
Scope: every fixed Bilibili HTTP endpoint under `DownKyi.Core/BiliApi`

This is a runtime contract inventory, not an assertion that undocumented Bilibili APIs are stable. It records the best evidence available without copying or using a maintainer's personal login state.

## Evidence And Status

- **LIVE**: anonymous controlled request on 2026-07-22. Only HTTP status, API code/message, top-level keys, content type and byte count were inspected.
- **YT**: current [yt-dlp Bilibili extractor](https://github.com/yt-dlp/yt-dlp/blob/master/yt_dlp/extractor/bilibili.py).
- **NEMO**: maintained bilibili-api endpoint maps for [users](https://github.com/Nemo2011/bilibili-api/blob/main/bilibili_api/data/api/user.json), [favorites](https://github.com/Nemo2011/bilibili-api/blob/main/bilibili_api/data/api/favorite-list.json), [videos](https://github.com/Nemo2011/bilibili-api/blob/main/bilibili_api/data/api/video.json), [bangumi](https://github.com/Nemo2011/bilibili-api/blob/main/bilibili_api/data/api/bangumi.json), and [login](https://github.com/Nemo2011/bilibili-api/blob/main/bilibili_api/data/api/login.json).
- **DOC**: maintained community protocol documentation, including [protobuf danmaku](https://github.com/SocialSisterYi/bilibili-API-collect/blob/master/docs/danmaku/danmaku_proto.md) and [history/watch-later](https://github.com/SocialSisterYi/bilibili-API-collect/tree/master/docs/historytoview).
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
| `api.bilibili.com/x/frontend/finger/spi` | `WebClient.GetBuvid`; obtain public buvid values before API requests | GET, `data.b_3/b_4`, anonymous | `current`; LIVE returned HTTP 200/code 0 | Keep. Missing `data` is a typed failure; HTTP retry tests cover the request path. |
| `api.bilibili.com/x/web-interface/nav` | `UserInfo.GetUserInfoForNavigation`; login snapshot and WBI image keys | GET, `data`; anonymous response is code `-101` but still carries public `data.wbi_img` | `fixed`; LIVE reproduced `-101` with valid 32-character WBI keys; YT also obtains WBI keys here | Only this endpoint may deserialize code `-101`. `UserNavigationContractTests` proves keys survive and all other endpoints still reject `-101`. |
| `api.bilibili.com/x/space/myinfo` | `UserInfo.GetMyInfo`; current account details | GET, `data`, Cookie required | `auth-deferred`; NEMO marks authenticated | Keep. Generic nonzero-code rejection and required-payload behavior apply. |
| `passport.bilibili.com/x/passport-login/web/qrcode/generate` | `LoginQR.GetLoginUrl`; create login QR session | GET, `data.url/qrcode_key`, anonymous | `current`; LIVE code 0; NEMO login map agrees | Keep. Probe never emits the generated key or URL. Existing login coordinator tests use stubs. |
| `passport.bilibili.com/x/passport-login/web/qrcode/poll` | `LoginQR.GetLoginStatus`; poll QR state | GET, `data`, generated key required | `current`; NEMO login map agrees; full live poll intentionally not persisted | Keep. The generated key is treated as sensitive transient state. |

## Ordinary Video, Playback And Media Metadata

| Endpoint | Owner and purpose | Contract/auth | Status and evidence | Decision and regression coverage |
|---|---|---|---|---|
| `api.bilibili.com/x/web-interface/wbi/view` | `VideoInfo.VideoViewInfo`; ordinary video metadata | GET, WBI, `data` | `current`; NEMO and YT agree; `BV1U7V66FEiK` fixture covers identity/pages | Keep. `VideoView.Data` is nullable and missing payload throws. |
| `api.bilibili.com/x/web-interface/archive/desc` | `VideoInfo.VideoDescription`; description | GET, `data` string, public | `current`; LIVE code 0; NEMO agrees | Keep. Empty/missing required payload remains distinguishable. |
| `api.bilibili.com/x/player/pagelist` | `VideoInfo.VideoPagelist`; page/CID list | GET, `data[]`, public | `current`; LIVE code 0; NEMO/YT agree | Keep. `BV1U7V66FEiK` page fixture covers CID parsing. |
| `api.bilibili.com/x/web-interface/view/detail/tag` | `VideoInfo.GetBiliTagInfo`; optional movie tags | GET, `data[]`, public/partly restricted | `current`; LIVE code 0 | Keep. Tag failures are optional metadata; cancellation still propagates. |
| `api.bilibili.com/x/player/wbi/v2` | `VideoStreamApi.PlayerV2`; subtitles and player metadata | GET, WBI, `data` | `current`; NEMO and YT agree | Keep. Player payload is required; subtitle tests use deterministic responses. |
| `api.bilibili.com/x/player/wbi/playurl` | `VideoStreamApi.GetVideoPlayUrl`; ordinary video streams | GET, WBI, `data` | `current`; NEMO/YT agree; fixed playback fixtures | Keep. `PlayUrlEnvelopeContractTests` rejects missing or empty `data`. |
| `api.bilibili.com/x/v2/dm/web/seg.so` | `DanmakuProtobuf`; protobuf segments | GET, binary protobuf, semi-anonymous | `legacy-working`; LIVE returned HTTP 200 protobuf; DOC identifies newer WBI variant | Keep for compatibility in this gate. The WBI alternative also worked anonymously, but moving it requires injecting WBI ownership into the danmaku boundary. No current production caller was found. |

Dynamic media dependencies are not fixed API endpoints: subtitle JSON addresses come from `PlayerV2`, media addresses come from `PlayUrl`, and the ordinary-video web fallback reads `www.bilibili.com/video/...`. They must remain opaque response values and must never be copied into diagnostic logs.

## Bangumi And Cheese

| Endpoint | Owner and purpose | Contract/auth | Status and evidence | Decision and regression coverage |
|---|---|---|---|---|
| `api.bilibili.com/pgc/review/user` | `BangumiInfo.BangumiMediaInfo`; media-to-season resolution | GET, `result` | `current`; YT/community implementations retain the media/season route | Keep. Optional `result` remains nullable and required by the caller. |
| `api.bilibili.com/pgc/view/web/season` | `BangumiInfo.BangumiSeasonInfo`; season/episode metadata | GET, `result` | `current`; LIVE code 0 for multiple public episodes; YT agrees | Keep. Public episode fixtures cover field selection. |
| `api.bilibili.com/pgc/player/web/v2/playurl` | `VideoStreamApi.GetBangumiPlayUrl`; bangumi streams | GET with `ep_id`, `cid`, and `bvid`/`aid`; `result.video_info`; auth/region rules vary | `fixed`; LIVE code 0 for ep 21495, 50188 and 678060; YT and NEMO use v2 | Replaced legacy v1 runtime URL. Production page and resumed-download callers preserve the episode ID. The nullable `BangumiPlayUrlV2Origin` contract rejects missing, null, malformed, or empty `result.video_info` with a typed failure; v1 fixture remains only as wire compatibility coverage. |
| `api.bilibili.com/pugv/view/web/season` | `CheeseInfo.CheeseViewInfo`; course metadata | GET, `data`; access varies | `current`; LIVE endpoint responds; NEMO/fixtures agree | Keep. Required `data` failure is typed. |
| `api.bilibili.com/pugv/view/web/ep/list` | `CheeseInfo.CheeseEpisodeList`; course pages | GET, `data` | `current`; LIVE endpoint responds; NEMO agrees | Keep. Pagination remains explicit. |
| `api.bilibili.com/pugv/player/web/playurl` | `VideoStreamApi.GetCheesePlayUrl`; course streams | GET, `data`, `ep_id` required | `current`; LIVE endpoint responds; YT uses the same route | Keep. `PlayUrlEnvelopeContractTests.CheeseEndpointUsesDataEnvelope` fixes the endpoint contract. |
| `api.bilibili.com/pugv/app/web/season/page` | `UserSpace.GetCheese`; courses published by a user | GET, `data.items` | `current`; NEMO user map agrees | Keep. Active user-space flow; live payload may vary by account/catalog. |
| `api.bilibili.com/x/space/bangumi/follow/list` | `UserSpace.GetBangumiFollow`; followed shows | GET, `data`, visibility/login dependent | `current`; NEMO user map agrees | Keep. Active paging coordinator preserves nonzero API failures. |

## Favorites, History And Personal Lists

| Endpoint | Owner and purpose | Contract/auth | Status and evidence | Decision and regression coverage |
|---|---|---|---|---|
| `api.bilibili.com/x/v3/fav/folder/info` | `FavoritesInfo.GetFavoritesInfo`; folder metadata | GET, `data` | `current`; NEMO and YT agree | Keep. Required payload. |
| `api.bilibili.com/x/v3/fav/folder/created/list` | `FavoritesInfo.GetCreatedFavorites`; paged folders | GET, `data.list` | `current`; LIVE code 0 | Keep paged API. NEMO favors `created/list-all`, but anonymous `list-all` returned null for public probes, so replacement evidence is insufficient. |
| `api.bilibili.com/x/v3/fav/folder/collected/list` | `FavoritesInfo.GetCollectedFavorites`; subscribed folders | GET, `data.list`, Cookie/visibility dependent | `auth-deferred`; NEMO agrees | Keep. No personal Cookie used for audit. |
| `api.bilibili.com/x/v3/fav/resource/list` | `FavoritesResource.GetFavoritesMediaResource`; folder content and keyword search | GET, `data.medias/has_more` | `current`; NEMO/YT agree; search fixtures cover `has_more` | Keep. Search pagination does not trust the unfiltered folder total. |
| `api.bilibili.com/x/v3/fav/resource/ids` | `FavoritesResource.GetFavoritesMediaId`; resource identities | GET, `data[]` | `current`; NEMO/YT agree | Keep. Required payload semantics apply. |
| `api.bilibili.com/x/web-interface/history/cursor` | `HistoryApi.GetHistory`; watch history | GET, `data.cursor/list`, Cookie required | `auth-deferred`; anonymous LIVE returned `-101`; NEMO and DOC agree | Keep. Cancellation and typed API errors are preserved. |
| `api.bilibili.com/x/v2/history/toview` | `ToView.GetToView`; watch later | GET, `data.list`, Cookie required | `auth-deferred`; anonymous LIVE returned `-101`; DOC and maintained UI implementations still use it | Keep. YT uses `/web`; both variants returned `-101` anonymously, so switching without an authenticated fixture would be speculative. |

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
| `api.bilibili.com/x/relation/followers` | `UserRelation.GetFollowers`; followers | GET, `data`, visibility/login limits | `auth-deferred`; NEMO agrees | Keep compatibility and coordinator path. API-imposed page limits remain visible. |
| `api.bilibili.com/x/relation/followings` | `UserRelation.GetFollowings`; following list | GET, `data`, visibility/login limits | `auth-deferred`; NEMO agrees | Keep. |
| `api.bilibili.com/x/relation/whispers` | `UserRelation.GetWhispers`; private follows | GET, `data`, Cookie required | `auth-deferred`; NEMO agrees | Keep. No personal session probe. |
| `api.bilibili.com/x/relation/blacks` | `UserRelation.GetBlacks`; block list | GET, `data`, Cookie required | `auth-deferred`; NEMO agrees | Keep. No personal session probe. |
| `api.bilibili.com/x/relation/tags` | `UserRelation.GetFollowingGroup`; own groups | GET, `data`, Cookie required | `auth-deferred`; NEMO agrees | Keep. |
| `api.bilibili.com/x/relation/tag` | `UserRelation.GetFollowingGroupContent`; group members | GET, `data`, Cookie required | `auth-deferred`; NEMO agrees | Keep. |

## Compatibility Discovery APIs

| Endpoint | Owner and purpose | Contract/auth | Status and evidence | Decision and regression coverage |
|---|---|---|---|---|
| `api.bilibili.com/x/web-interface/ranking/region` | `Ranking.RegionRankingList`; regional ranking | GET, `data[]` | `legacy-working`; LIVE code 0; no production caller | Retain compatibility only. Current product has no ranking workflow, so replacing it would add untested behavior. |
| `api.bilibili.com/x/web-interface/dynamic/region` | `DynamicApi.RegionDynamicList`; regional dynamic list | GET, `data` | `legacy-working/risk`; LIVE returned API `-404` for an empty sample; no production caller | Retain compatibility and typed failure. Do not interpret an empty region as endpoint retirement. |

## Confirmed Changes

1. Anonymous navigation now accepts only API code `-101` at the `/nav` contract, then requires `data`. This repairs WBI bootstrap for public videos without weakening global API error handling.
2. Bangumi playback now uses `/pgc/player/web/v2/playurl` and explicitly selects `result.video_info`. The v2 DTO does not create default payloads.
3. The audit records the retired channel family, invalid nickname query, legacy danmaku path, and watch-later ambiguity without speculative remapping.
4. `BilibiliApiInventoryArchitectureTests` fails when a fixed Core endpoint is not present in this document or when the `/nav` nonzero-code exception spreads to another source file.
5. `script/audit-bilibili-api.ps1 -ConfirmLive` reproduces the anonymous subset and outputs only sanitized diagnostics. It is an operator tool, not a CI test.

## Deterministic Tests

- `UserNavigationContractTests.AnonymousNavigationResponsePreservesPublicWbiMetadata`
- `UserNavigationContractTests.AnonymousCodeRemainsRejectedOutsideTheNavigationContract`
- `PlayUrlEnvelopeContractTests.BangumiEndpointUsesResultVideoInfoEnvelope`
- `PlayUrlEnvelopeContractTests.BangumiV2MissingVideoInfoThrowsTypedContractFailure`
- existing `PlayUrlEnvelopeContractTests` for ordinary video and cheese
- existing `BvFixtureContractTests` for `BV1U7V66FEiK`
- `BilibiliApiInventoryArchitectureTests.EveryHardCodedBilibiliApiEndpointIsRecordedInTheAudit`
- `BilibiliApiInventoryArchitectureTests.AnonymousNonSuccessCodeExceptionIsScopedToNavigation`
- `BilibiliApiInventoryArchitectureTests.OptionalJsonEnvelopeFieldsCannotInventPayloads`
- `BilibiliApiInventoryArchitectureTests.LiveProbeIsExplicitAndDoesNotLoadCookies`

## Maintenance Procedure

1. Update the endpoint row and deterministic fixture in the same PR as any endpoint or envelope change.
2. Run `pwsh ./script/audit-bilibili-api.ps1 -ConfirmLive` only when a live audit is intended. Do not load a browser profile, login file, or Cookie into the script.
3. Treat HTTP success and JSON parse success as insufficient: check API code, required envelope and usable payload.
4. For authenticated contracts, use synthetic/recorded redacted fixtures. A maintainer's live session is never a release prerequisite.
5. If sources conflict, keep the working contract and record the alternative until equivalent payload behavior is proven.

## Gate 3 Local Verification

- Strict .NET 10 Release build with `AnalysisMode=All`: 0 warnings, 0 errors.
- Full solution tests: 543 passed, 0 failed, 0 skipped.
- Format verification: 0 of 742 files changed.
- NuGet vulnerable and deprecated package reports: empty for every solution project.
- Anonymous live probe: 27 results, no transport failures, no local authentication loaded.
- Module boundary audit and `git diff --check`: passed with no new baseline finding.
