# Historical download store fixtures

These SQLite files are immutable schema baselines. Tests copy a file into a
temporary directory before inserting scenario rows or running migrations.
They must never be regenerated with the current DownloadStoreSchema.

v0 and v1 use the ApplyVersionOneAsync SQL from commit 7cd78eb.
v0 drops the migration ledger and quarantine table introduced by that
commit to represent the pre-versioning three-table legacy format.
v1 has ledger row 1 and user_version 1.
v2-v9 were initialized by the SqliteDownloadTaskStore from the version's
historical source commit, without writing task data. Scenario rows are
inserted into test-local copies using columns available in that version.

| Version | Source commit | SHA-256 |
| --- | --- | --- |
| v0 | `7cd78eb` | `77b69aad5fb51420fa41e0841311267318d7e323a71a8e6f72dbb3b7ca4507c7` |
| v1 | `7cd78eb` | `891d79d2b153f8a5152a62d46bfb69f7de0aaa391aa562182860a6bbce8f13d6` |
| v2 | `7cd78eb` | `7304465db533ec70481c5dcc834c9d26e6d5b01c04cf66bc3edce9cd56ad3c16` |
| v3 | `bdd3557` | `5b65823152fd78b7f60560fa99819f88c88c384a97f3651ed0f934a2b5b425ae` |
| v4 | `c22cc02` | `1dd5e55bf1f6e607ea8c4e0a0755c307163d24db1bf7f66032b833d9a6f8139a` |
| v5 | `0d9d0e7` | `0cd83ff35ebefc8e99211d44319af383c071c5983c5ef930232b27eb3efc46dd` |
| v6 | `33364bc` | `436898e4cb7f7fe4dd2a50e8527050f2aae20ea81170def296d72ec77f714163` |
| v7 | `d4345d7` | `5d4ee0888d4b31e416dcfecc1198495fe5b79752c9a7e23cea045e90f08742dd` |
| v8 | `ad26bb8` | `8446e9cbf5d3372ead9f22c82ac2faaf6324ee642ac2d349ba3a3206a0d49bb3` |
| v9 | `9160f00` | `7014ad765a34471288c71d252d7755447a20e9601a8ecccbe3f611ca2bca9d56` |
