# PerfeBT → ModTek upstream proposals

Scope: decide which generic PerfeBT fixes and profiling observations belong upstream in
[ModTek](https://github.com/BattletechModders/ModTek), and what users can turn on in ModTek config today.
Mod-specific PerfeBT fixes (CustomComponents, CustomUnits, CustomFilters, IRBTModUtils) are out of scope.

## Version pin

- Installed: `Mods/ModTek/lib/ModTek.dll` = **4.5.1+8.Branch.master.Sha.679fad49adaae4b23765b11705a1808c8422b855**.
  `mod.json` still says `4.3.6`; ModTek overwrites that value at runtime from GitVersion.
- All code references below are permalinks to that commit:
  `P = https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/`
- Evidence comes from one session (2026-10-18). ModTek logs use UTC and PerfeBT logs use local time (+3 h), so 02:25 UTC and 05:25 local are the same moment.
  Sources: ModTek's `Mods/.modtek/{battletech_log.txt,ModTek.log,HarmonyFileLog.log}` and that session's PerfeBT log and profiler reports (not kept; the figures quoted below are from them).
- Prior art on GitHub: I scanned the titles of all 254 issues and PRs. The search API was rate-limited, so I could not do full-text search.
  - Relevant hits: [#239 Improve json parsing performance](https://github.com/BattletechModders/ModTek/issues/239) (open; the maintainer postponed a full parser rewrite), [#251 RequiredContentPack log spam](https://github.com/BattletechModders/ModTek/issues/251) (open; maintainer response below), [#219 ModTek load time](https://github.com/BattletechModders/ModTek/issues/219), and [PR #252 parallelized FileUtils](https://github.com/BattletechModders/ModTek/pull/252) (closed without merge or comment).
  - No issue or PR title mentions MDDB indexes, the internet check, MaxConcurrentLoads, DataLoader monitoring, or the soundbank handle leak.

## Summary

| # | Candidate | Already in ModTek? | Fit | Proposed home / config | Measured effect (PerfeBT) |
|---|---|---|---|---|---|
| 1 | JsonReflection (cache GetInterfaces/GetGenericArguments, memoize IsValueType) | Partial overlap: `BetterReflectionCache` covers other ReflectionCache calls only | **Fits very well** | `Features/BetterReflectionCache/`, always on like its siblings | save-load window 45.4 → 38.8 s (−15 %) |
| 2 | DataLoaderMonitoring (skip hot-reload registration) | No | **Fits** | `Features/Manifest/Patches/DataLoader_LoadResource_Patch.cs`, no key | removes an O(n) list scan per loaded file (~36k files) |
| 3 | TagSetIndex (NOCASE index on `TagSetTag.TagSetID`) | No; but ModTek owns the modded MDDB file | **Fits very well**, and better upstream than in PerfeBT | `Features/Manifest/MDD/MDDBCache.cs` (persisted in the cached MDDB) | ~11 ms per `GetTagsForSet` call → index seek; 177 ms one-time build |
| 4 | **New:** `BetterBTRL.HasChanges` is never reset, so every `RefreshTypedEntries` does a full rebuild | ModTek bug | **Fits (ModTek-owned code)** | `Features/Manifest/BTRL/BetterBTRL.cs` | `ApplyMemoryStore` 630–750 ms per call runs 2 full rebuilds; RefreshTypedEntries 1.07 s cumulative |
| 5 | CustomSoundBankDefs: pinned GCHandle never freed + sync read/decrypt | ModTek code | **Fits** (leak fix); perf part **partial** | `Features/CustomSoundBankDefs/SoundBanksPatches.cs` | 2.6 s first combat in an earlier run; ~10 ms in this session (warm cache) |
| 6 | SaveLoadThrottle / LoadWindow (light loads 10 → 300 + per-frame budget) | Overlaps: `LoadRequest_PopPendingRequest_Patch` + `DataManagerUnfreezeDelta` | **Partial** | `Features/Manifest/Patches/` next to the unfreeze patch; new keys `DataManagerMaxConcurrentLoads*` | defs window 73 → 53.6 s |
| 7 | HarmonyX "Patching…" Info logging | Configurable in the preloader (`Harmony12XLogChannelFilter`) | Config change now; changing the default is **partial** | `ModTek.Preloader/Loader/Config.cs` default 26 → 24 | ~3.1k Info lines × 3 sinks |
| 8 | InternetCheck (skip 5 s synchronous connectivity check) | No | **Partial** (vanilla behaviour change) | opt-in `SkipInternetConnectivityCheck`, e.g. `Features/Misc/` | `UnityGameInstance::GetHtmlFromUri` 5001.6 ms at Loading |
| 9 | Log volume (~130k lines at startup) | Async logging already removes most main-thread cost | **Doesn't fit as code change** (maintainer rejected aggregation in #251); config fix only | user/modpack `config.json` `Logging.MainLog.Exclude` | main-thread cost measured at ~17 ms on-thread for 131k messages |
| 10 | Manifest processing ~9 s | Partly measured by ModTek itself | Profile first; mostly covered by #4 | `Features/Manifest/` | BuildModdedBTRL 6.58 s, BuildMDDBCache 1.27 s |

## 1. JsonReflection → `Features/BetterReflectionCache`

**PerfeBT** (`PerfeBT/src/ModTek/JsonReflection.cs`):
- Transpilers on `HBS.Util.JSONSerializationUtility.RehydrateObjectFromDictionary` (the 7-parameter overload) and `ConvertValue` replace calls to `Type.GetInterfaces()`, `Type.GetGenericArguments()` and `Enumerable.Contains<Type>` with per-Type cached arrays.
- A prefix/postfix pair memoizes `HBS.Reflection.MemberInfoHelper.IsValueType(Type)` per `Type`.
- PerfeBT.log: "RehydrateObjectFromDictionary 2 reflection calls cached", "ConvertValue 7".
- Result: the save-load window went from 45.4 s to 38.8 s.

**ModTek today:**
- [`ModTek/Features/BetterReflectionCache/BetterReflectionCache_Patches.cs#L10-L163`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/BetterReflectionCache/BetterReflectionCache_Patches.cs#L10-L163) replaces `HBS.Util.ReflectionCache` Get/Set/GetMemberHelper/Invoke/GetMethodHelper with a `(Type,string)` dictionary.
  - It is always on (no `Prepare`/config).
  - It does **not** cover `IsValueType`, `GetInterfaces` or `GetGenericArguments`. No overlap and no conflict.
- [`ModTek/Features/Profiler/Patches/JSONSerializationUtility_RehydrateObjectFromDictionary_Patch.cs`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Profiler/Patches/JSONSerializationUtility_RehydrateObjectFromDictionary_Patch.cs) patches the same overload with prefix/postfix timing, gated by `ProfilerEnabled`.
  - It can coexist with a transpiler.
- Issue [#239](https://github.com/BattletechModders/ModTek/issues/239): the maintainer lists "lookup-caching but its still meta-code working via reflection apis" as the bottleneck and postponed a full rewrite as "too much effort".
  - This fix is the cheap, incremental step in the same direction.

**Proposed upstream change:**
- Add `BetterReflectionCache/JSONSerializationUtility_Patches.cs` (transpilers + a static `TypeInfoCache` with `ConcurrentDictionary<Type, Type[]>` or a lock-free dictionary for interfaces and generic arguments).
- Add `BetterReflectionCache/MemberInfoHelper_IsValueType_Patch.cs`.
- Follow the folder's convention: no config key, `Prepare => ModTek.Enabled`.
- Transpilers should match by `MethodInfo` operand and log or skip when the expected call count is not found, as PerfeBT already does.

**Fit: fits very well.** The behaviour is identical: per-Type results are immutable at runtime, and the change is purely a cache.

**Risk:**
- HBS JSON is used off the main thread during prewarm, so the cache must be thread-safe.

## 2. DataLoaderMonitoring → `Features/Manifest/Patches`

**PerfeBT** (`PerfeBT/src/ModTek/LoadWindow.cs`, `DataLoaderMonitoring`):
- A prefix on `HBS.Data.DataLoader.LoadResource(string, Action<string,Stream>, bool)` forces `monitor = false`.
- Vanilla registers every loaded file for hot-reload with an O(n) `List` contains-scan. The consumer, `CheckFiles`, is unreachable in release builds.

**ModTek today:** nothing patches `HBS.Data.DataLoader`. ModTek's loader-side code is the BTRL/manifest pipeline ([`Features/Manifest/`](https://github.com/BattletechModders/ModTek/tree/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest)).

**Proposed:**
- Add `ModTek/Features/Manifest/Patches/DataLoader_LoadResource_Patch.cs`, with `Prepare => ModTek.Enabled` and a prefix of `monitor = false`.
- No config key is needed. If the maintainer wants one, call it `DataLoaderHotReloadMonitoringDisabled` with a `_Description`, following [`Misc/Configuration.cs`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Misc/Configuration.cs).

**Fit: fits.** It is a generic vanilla hot path that scales with mod-pack size, which is exactly what ModTek's loader patches target.

**Risk:** none known, provided nothing calls `DataLoader.CheckFiles` `[INFERENCE: verified unreachable by PerfeBT analysis, not re-verified here]`.

## 3. TagSetIndex → `Features/Manifest/MDD/MDDBCache.cs`

**PerfeBT** (`PerfeBT/src/ModTek/TagSetIndex.cs`):
- Runs `CREATE INDEX IF NOT EXISTS PerfeBT_TagSetTag_TagSetID_NoCase ON TagSetTag (TagSetID COLLATE NOCASE)` in a prefix on `Tags_MDDExtenstions.GetTagsForSet`, once per `MetadataDatabase.inMemoryDB` connection.
- PerfeBT.log: "TagSetTag NOCASE index ensured in 177 ms".

**Verified on the modded MDDB** (`Mods/.modtek/Database/MetadataDatabase.db`, opened read-only):
- `TagSetTag` has 432,216 rows.
- Existing indexes: `sqlite_autoindex_TagSetTag_1` and `TagSetTagIndex (TagSetID ASC, TagName ASC)`, both BINARY collation.
- `EXPLAIN QUERY PLAN SELECT * FROM TagSetTag tst WHERE tst.TagSetID = ? COLLATE NOCASE` returns **`SCAN tst`** (full table scan).
- The PerfeBT index is **not** in the on-disk file, so PerfeBT rebuilds it (177 ms) every session.

**ModTek today:**
- There is no `CREATE INDEX` or `PRAGMA` anywhere in the repo. ModTek does own the modded MDDB, though:
  - [`MetadataDatabase_MDD_DB_PATH_Patch.cs#L18-L27`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/Patches/MetadataDatabase_MDD_DB_PATH_Patch.cs#L18-L27) redirects the game to `FilePaths.ModMDDBPath`.
  - [`MDDBCache.cs#L63-L73`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/MDD/MDDBCache.cs#L63-L73) `Reset()` copies the vanilla DB there and calls `MetadataDatabase.ReloadFromDisk()`.
  - [`#L76-L106`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/MDD/MDDBCache.cs#L76-L106) `Save()` calls `MetadataDatabase.SaveMDDToPath()` when `hasChanges || SaveMDDB`.
- Existing SQL usage follows the `mdd.Execute`/`Query` pattern: [`CustomTags/MetadataDatabaseExtensions.cs#L13-L60`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/CustomTags/MetadataDatabaseExtensions.cs#L13-L60).

**Proposed:**
- Add `MDD/MDDBIndexes.cs` with `EnsureIndexes(MetadataDatabase mdd)`. It checks `sqlite_master` for the index name and, if missing, runs `CREATE INDEX IF NOT EXISTS ModTek_TagSetTag_TagSetID_NoCase ON TagSetTag (TagSetID COLLATE NOCASE)` and sets `MDDBCache.SaveMDDB = true`.
- Call it in `MDDBCache.BuildCache()` before `Save()` ([`#L238-L239`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/MDD/MDDBCache.cs#L238-L239)).
- The index then persists in the cached DB. Later starts pay nothing (the `MDDBCache: Loaded.` path), and a `Reset()` rebuild recreates it once.
- Optional key: `MDDBExtraIndexesEnabled` (default true) with `_Description`.
- The same hook can host further indexes found with the existing `Logging.LogSqlQueryInitializations` ([`SqliteCommand_CommandText_Setter_Patch.cs`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Logging/Patches/SqliteCommand_CommandText_Setter_Patch.cs)) plus `EXPLAIN QUERY PLAN`.

**Fit: fits very well, and better than in PerfeBT:**
- Only ModTek knows when the MDDB is rebuilt and saved, so it can turn a per-session 177 ms cost into a one-time cost.

**Risks:**
- The DB file grows by roughly the size of the index `[INFERENCE]`.
- `SaveMDDToPath` must preserve indexes `[INFERENCE: HBS writes the in-memory DB to disk; verify the index survives a save/reload once]`.
- If PerfeBT stays installed alongside, its `IF NOT EXISTS` with a different name would create a duplicate index. PerfeBT should check for either name, or drop its fix once ModTek ships this.

## 4. New finding: `BetterBTRL.HasChanges` is never reset → `Features/Manifest/BTRL/BetterBTRL.cs`

Found while tracing the manifest observation. This is ModTek-owned code.
- [`BetterBTRL.cs#L294-L325`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/BTRL/BetterBTRL.cs#L294-L325): `RefreshTypedEntries()` returns early only `if (!HasChanges)`, then rebuilds the whole `TypedManifest`: `Reset(defaultManifest.Entries)`, every addendum, the mods addendum, and the merge cache with a lookup per entry.
- `HasChanges` (field at [`#L25`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/BTRL/BetterBTRL.cs#L25)) is only ever **set** to true (L47, 53, 106, 123, 137, 149) and never reset. The comment says "it changes all the time anyway". So after the first change, **every call is a full rebuild**.
- [`ApplyMemoryStore` #L166-L178](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/BTRL/BetterBTRL.cs#L166-L178) calls `ApplyAddendum` (rebuild #1, L124), then `RefreshTypedEntries()` again (rebuild #2, L177).
- `ModsManifest.HandleModManifestsLoop` ([`ModsManifest.cs#L29-L69`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/ModsManifest.cs#L29-L69)) calls `RefreshTypedEntries()` four times.

**Measured (this session):**
- PerfeBT reports at the Loading scene: `BattleTechResourceLocator_ApplyMemoryStore_Patch::Prefix` 630–750 ms for 1 call; `ApplyAddendum_Patch::Prefix` ~200 ms for 4 calls (~50 ms per rebuild).
- ModTek.log: "Method …BetterBTRL::RefreshTypedEntries took 00:00:01.0676023" (cumulative `LogIfSlow`).

**Proposed:**
- Set `HasChanges = false` at the end of `RefreshTypedEntries()`.
- Add `HasChanges = true` in `IndexMemoryStore` ([`#L194`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/BTRL/BetterBTRL.cs#L194)), the `SubscribeToContentsChanged` callback.
  - Memory-store contents can change after they are applied, and today the always-rebuild behaviour silently covers that case.
- Optionally drop the redundant `RefreshTypedEntries()` at L177 and L191.

**Fit: fits.** It is ModTek's own code and a small diff.

**Gain:** expected to remove at least one of the two rebuilds per `ApplyMemoryStore`, plus every no-op refresh `[INFERENCE: roughly 50–350 ms per memory-store apply, not measured]`.

**Risk:** some code may depend on the "always rebuild" side effect. Validate with a mod-pack start plus save load.

## 5. CustomSoundBankDefs → `Features/CustomSoundBankDefs/SoundBanksPatches.cs`

**Observation:** FINDINGS.md:48 reports `LoadedAudioBank_LoadBankExternal` prefix 2.6 s in the first combat (6 banks, ~440 ms each).

**ModTek code** ([`SoundBanksPatches.cs#L228-L343`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/CustomSoundBankDefs/SoundBanksPatches.cs#L228-L343)): the prefix runs on the main thread and does the following:
- synchronous `File.ReadAllBytes`;
- optionally a managed AES-CBC decrypt through `CryptoStream`, when `SoundBanksProcessHelper.GetRegisteredProcParams` returns params and the file lacks the `BKHD` magic;
- `GCHandle.Alloc(bytes, GCHandleType.Pinned)` followed by `AkSoundEngine.LoadBank(ptr, len, out id)` (in-memory, in-place).

**Bug:**
- The pinned `GCHandle` is never freed. [`LoadedAudioBank_UnloadBank` #L203-L226](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/CustomSoundBankDefs/SoundBanksPatches.cs#L203-L226) only sets `loaded = false`.
- Each combat load/unload leaks the bank buffers (3–7 MB each) as pinned objects. That leaks memory and fragments the Mono heap `[INFERENCE: Wwise in-memory LoadBank requires the buffer to stay alive until UnloadBank; afterwards it must be freed]`.

**Timing in this session:** ModTek.log lines 80967–81041 show all 6 banks (Helicopter 3.3 MB, Screams 3.0 MB, weapons 7.0 MB, enginesound, jetsound, transformsound) loaded within ~10 ms (02:28:37.101 → .112). The 2.6 s figure came from an earlier run, likely a cold OS file cache and/or decrypt `[INFERENCE]`.

**Proposed:**
1. **Leak fix (must-have):** keep `GCHandle`/bank id per `SoundBankDef` and free the handle in an `UnloadBank` postfix after `AkSoundEngine.UnloadBank`.
2. **Perf (optional):**
   - Read and decrypt banks on a background task at sim-game/combat-load start, and pin only on the main thread.
   - Alternatively, cache the decrypted bytes for banks loaded repeatedly.

**Fit:**
- The leak fix fits: it is ModTek-owned code.
- The perf part is partial: the cost is I/O-bound and not reproducible warm, so it needs a cold-cache measurement first.

## 6. SaveLoadThrottle / LoadWindow → `Features/Manifest/Patches` (partial overlap)

**PerfeBT** (`PerfeBT/src/ModTek/LoadWindow.cs`, `SaveLoadThrottle`):
- Between `SimGameState.RequestDataManagerResources` and `RespondToDefsLoadComplete`, it raises `DataManager.SetMaxLightConcurrentLoads(300)` (vanilla is 10).
- It enforces a 1000 ms per-frame budget with a `LoadRequest.PopPendingRequest` prefix (`Priority.First`) that returns `null` once the budget is spent.
- PerfeBT.log: "load limits light 10 -> 300 … restored after 31.8 s, 13 budget cuts", with up to 2,889 pops per frame.
- Defs window: 73 s → 53.6 s.

**ModTek today:**
- [`Features/Manifest/Patches/LoadRequest_PopPendingRequest_Patch.cs#L7-L48`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/Patches/LoadRequest_PopPendingRequest_Patch.cs#L7-L48) returns `null` when `Time.realtimeSinceStartup - lastNull >= Config.DataManagerUnfreezeDelta`, to let a frame render.
  - Config is at [`Misc/Configuration.cs#L42-L50`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Misc/Configuration.cs#L42-L50): `DataManagerUnfreezeDelta` = 2 s, `DataManagerEverSpinnyDetectionTimespan`.
  - `lastNull` is global and is reset by *any* null result, including the natural end of every small batch. With thousands of small batches the unfreeze rarely triggers. It is a "time since last null", not a per-frame budget.
- No code touches `MaxConcurrentLoads` / `SetMaxLightConcurrentLoads` (repo-wide search).
- `ShowDataManagerStatsInLoadingCurtain` ([`LoadingCurtainStatsText.cs#L24-L43`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/LoadingCurtainEx/DataManagerStats/LoadingCurtainStatsText.cs#L24-L43)) reuses the same delta as its refresh interval.

**Conflicts:**
- Both mods prefix `PopPendingRequest`. PerfeBT runs first and can return `null` early. That is compatible, but the two budgets interact.
- CustomDeploy replaces `DataManager.UpdateRequests`, so any `UpdateRequests` hook must tolerate that.

**Proposed:**
- `DataManagerMaxConcurrentLoadsLight` / `DataManagerMaxConcurrentLoadsHeavy` (int, `0` = vanilla) with `_Description`, applied through `DataManager.SetMaxLight/HeavyConcurrentLoads`.
  - Optionally scope them to the save-load window, as PerfeBT does: `DataManagerMaxConcurrentLoadsDuringSaveLoadOnly`.
- Change the unfreeze semantics to a real per-frame budget: record the frame start (or `Time.frameCount` change) and return `null` once `now - frameStart >= budget`, independent of batch ends.
  - This can ship alone as a bug fix of `DataManagerUnfreezeDelta`'s intent.

**Fit: partial.**
- It belongs next to the existing unfreeze patch.
- But raising concurrency changes vanilla scheduling for all mod packs. Packs whose loaders assume ≤10 in flight could expose ordering bugs `[INFERENCE]`. The default should stay vanilla and be opt-in.

## 7. HarmonyX "Patching…" listings → preloader config

**ModTek today:**
- [`ModTek.Preloader/Harmony12X/ShimInjectorPatches.cs#L18-L30`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek.Preloader/Harmony12X/ShimInjectorPatches.cs#L18-L30): when `Harmony12XLogChannelFilter > 0`, it sets `HarmonyLib.Tools.Logger.ChannelFilter` and adds a file logger (`HarmonyFileLog.log`).
- [`ModTek/Features/Logging/HarmonyXLoggerAdapter.cs#L10-L16`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Logging/HarmonyXLoggerAdapter.cs#L10-L16) also forwards every message to the HBS logger `HarmonyX`, so it lands in `battletech_log.txt` too.
- The default is `26` = Info (2) + Warn (8) + Error (16): [`ModTek.Preloader/Loader/Config.cs#L12-L14`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek.Preloader/Loader/Config.cs#L12-L14).
- [`doc/HARMONY12X.md`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/doc/HARMONY12X.md) (lines 25–28) says patch order is intentionally visible in these logs, replacing the removed `harmony_summary.log`.

**Measured:**
- `HarmonyFileLog.log`: 24,845 lines (3,146 "Patching").
- `battletech_log.txt`: 10,855 HarmonyX lines.

**Proposed:**
- Users: see config section below.
- Upstream: default to `24`, and document `26` for patch-order debugging.

**Fit: partial.** The maintainer deliberately kept the Info listing as the replacement for harmony_summary.log, so a default change may be refused. Fair: the cost is mostly disk; HarmonyX formats Info messages only when the channel is enabled `[INFERENCE]`.

## 8. InternetCheck → opt-in vanilla tweak

**PerfeBT** (`PerfeBT/src/ModTek/InternetCheck.cs`) prefixes `UnityGameInstance.CheckInternetConnectivity` and invokes `OnInternetConnectivityResult(false)` immediately.

**Evidence:** the 2026-10-18 `Loading` scene profiler report showed `BattleTech.UnityGameInstance::GetHtmlFromUri 5001.6 ms`, synchronous on the main thread. FINDINGS cites 1.5–5 s.

**ModTek today:** nothing comparable. Its precedents for vanilla behaviour tweaks are `DelayPrewarmToMainMenu` ([`DataManager_ProcessPrewarmRequests_Patch.cs`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/Patches/DataManager_ProcessPrewarmRequests_Patch.cs)) and the PopPendingRequest unfreeze.

**Proposed:**
- `SkipInternetConnectivityCheck` (bool, default false) + `_Description`, in a small `Features/VanillaTweaks/` (or `Misc/`) folder.
- Better alternative that changes no behaviour: run `GetHtmlFromUri` on a worker thread and marshal the result back to the main thread.

**Fit: partial.**
- It is not mod-loading related, and forcing "offline" disables online features (news/paradox account) `[INFERENCE]`.
- The async variant has a better chance of acceptance than the skip.

## 9. Log volume → configuration, not code

**Measured (this session):**
- `battletech_log.txt`: 129,202 lines / 17 MB, of which 80,986 are ModTek lines and 10,855 HarmonyX lines.
- `ModTek.log`: 81,050 lines / 12 MB.
- ModTek lines are written twice because `MainLog` has no effective `Exclude` (see stale config below).
- Top line families:
  - per-entry `\tAdd: …` ([`ModsManifest.cs#L303`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/ModsManifest.cs#L303), [`#L328`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/ModsManifest.cs#L328), formatted in `LogModEntryAction` [`#L360-L363`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/ModsManifest.cs#L360-L363));
  - "Specified RequiredContentPack is being ignored" ×1,034 ([`#L378`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/ModsManifest.cs#L378)).
- ModTek's own stats (ModTek.log:81042–81050): on-thread processing 131,000× totalling 0.0166 s (126 ns avg), with ≥0.391 s offloaded to the logging thread. PerfeBT: `LogImpl_LogAtLevel_Patch::Prefix` ~1 µs per call (e.g. 34,006 calls = 41 ms).
- `Log.Main` is created at `TraceLogLevel` ([`ModTek/Log.cs#L8`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Log.cs#L8)), so `?.` guards never skip the `$"…{entry}"` interpolation.

**Upstream stance:**
- In [#251](https://github.com/BattletechModders/ModTek/issues/251) (comment of 2026-05-27) CptMoore said per-entry warnings are intentional (the support is per resource). He suggested fixing it in the content pack, a global suppress flag, or "another log prefix filter … in the default ModTek config of RogueTech".
- A PR that demotes or aggregates `Add:` lines would likely be refused.

**Fit: doesn't fit as a code change.** The main-thread cost is tens of ms, and the maintainer prefers per-entry logs. Ship it as modpack config instead (next section).

## 10. Manifest processing ~9 s

**Measured (ModTek.log):**
- "BuildModdedBTRL took 00:00:06.5766555" (L65245)
- "BuildMDDBCache took 00:00:01.2680582" (L67366)
- "Done. Elapsed running time: 26,9290401 seconds" (L79662)
- "Prewarming took 00:00:06.5155737" (L80840)

**Known contributors:**
- §4 (full TypedManifest rebuilds).
- `FixMissingIdAndType` LINQ `EntriesByID().Where().ToList()` per entry ([`ModsManifest.cs#L233-L235`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Manifest/ModsManifest.cs#L233-L235)).
- Per-entry log formatting (§9).
- `ContentPackIndex_PatchMDD_Patch::Postfix` ~80 ms at MainMenu (PerfeBT report).

**Proposal:**
- Land §4 first.
- Then profile `ModTek.Features.Manifest.*` with PerfeBT's type-pattern profiler (or ModTek's `ProfilerEnabled`) to attribute the remaining BuildModdedBTRL time before proposing anything else `[INFERENCE: no per-method breakdown captured yet]`.
- PR #252 (parallel FileUtils) was closed without merge, so threading-based proposals face a higher bar.

## Config speedups available now

### Stale keys in `Mods/ModTek/config.json` (important)
The modpack's `config.json` still uses the **pre-4.4 logging schema**. [CHANGES.md 4.4](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/CHANGES.md): "Includes and Excludes were renamed to Include/Exclude … `PrefixesToIgnore` was removed."
- The 4.5.1 schema has no target for these keys, and ModTek ignores them:
  - `MainLog.Excludes` (with `LoggerNames`) and `MainLog.Includes`;
  - `PrefixesToIgnore`, `IndentNewLines`, `NormalizeNewLines`, `MessageSanitizerRegex`;
  - `UseAbsoluteTime`, `IgnoreLoggerLogLevel`, `SkipOriginalLoggers`, `IgnoreSkipForLoggers`;
  - `ShowLoadingScreenErrors`, `ShowErrorPopup`, `UseErrorWhiteList`, `ErrorWhitelist`.
- Valid keys: [`Features/Logging/LoggingSettings.cs`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Logging/LoggingSettings.cs), [`AppenderSettings.cs`](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Logging/AppenderSettings.cs).
- `config.last.json` (the effective config) confirms that `MainLog` has **no Exclude**, so the intended "ModTek out of main log" filter is inactive.

### Recommended `Mods/ModTek/config.json` changes (`Logging` section)
```json
"MainLog": {
  "Exclude": ["ModTek", "HarmonyX"]
},
"LogStackTracesOnExceptions": false,
"ModLogAppendersEnabled": true
```

| Key (source) | Current effective | Recommended | Effect |
|---|---|---|---|
| `Logging.MainLog.Exclude` ([AppenderSettings.cs#L17-L20](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Logging/AppenderSettings.cs#L17-L20)) | none | `["ModTek","HarmonyX"]` | Prefix filter on `"<Logger> [LEVEL] …"` lines. Removes ~81k ModTek + ~11k HarmonyX lines from `battletech_log.txt`. ModTek lines remain in `ModTek.log` (its `Include: ["ModTek"]`) and HarmonyX lines in `HarmonyFileLog.log`. Saves disk I/O, not main-thread time. |
| `Logs["battletech_log_cleaned.txt"]` (local config) | present | remove unless used | Removes a third full log writer. |
| `Logging.LogStackTracesOnExceptions` ([LoggingSettings.cs#L43-L46](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Logging/LoggingSettings.cs#L43-L46)) | true | false | Skips capturing an environment stack trace for every logged exception (matters for packs that log many caught exceptions). |
| `Logging.LogThreadStarts` ([#L33-L36](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/Logging/LoggingSettings.cs#L33-L36)) | false (code default true) | keep false | Avoids a stack trace per thread start. |
| `Logging.AsynchronousLoggingEnabled` (#L72-L75) | true | keep | Formatting and writing happen off the main thread (126 ns per call on-thread measured). |
| `Logging.UnityConsoleAppenderEnabled` (#L53-L56) | false | keep | Help text: "reduces performance". |
| `Logging.ModLogAppendersEnabled` (#L58-L61) | true | false only if no mod's `Logs` files are needed | Help text: "set to false to improve performance". It disables per-mod log files declared in `mod.json`, which hurts debugging. |
| `Logging.LogFlushToDisk` (#L77-L80) | true | false (optional) | `ILog.Flush()` stops blocking on disk. Only matters if mods call Flush often. |
| `Logging.LogExceptionInitializations`, `LogSqlQueryInitializations`, `LogSqlQueryExecutionsFromDapper`, `LogStackTraces`, `DebugLogLevelSetters` | false | keep false | Diagnostic only; each adds patches or stack traces. |
| `ProfilerEnabled` ([Configuration.cs](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Misc/Configuration.cs)) | false | keep false | Adds timing prefix/postfix patches (e.g. `JSON_ToObject_Patch`, `UIManager_LateUpdate_Patch`). Diagnostic, not a speedup. |
| `DelayPrewarmToMainMenu` | true | keep | Defers prewarm (6.5 s this session) until the main menu instead of blocking startup. |
| `ShowDataManagerStatsInLoadingCurtain` | true | keep (cost negligible) | Iterates `activeLoadBatches` once every `DataManagerUnfreezeDelta` seconds while the curtain is visible ([DataManagerStats.cs#L11-L43](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Features/LoadingCurtainEx/DataManagerStats/DataManagerStats.cs#L11-L43)). |
| `DataManagerUnfreezeDelta` ([Configuration.cs#L42-L50](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek/Misc/Configuration.cs#L42-L50)) | 2.0 | 2.0–5.0 | Higher means fewer forced "render a frame" breaks during loading, at the cost of UI responsiveness. Small effect because of the `lastNull` semantics (§6). |

For RogueTech specifically, the maintainer's own suggestion in #251 is to filter the RequiredContentPack line by prefix. That only works per appender and requires a logger-name prefix: `"ModTek [WARNING] \t\tSpecified RequiredContentPack"` `[INFERENCE: exact rendered prefix incl. tabs to be verified against ModTek.log]`.

### `Mods/ModTek/ModTekPreloader.config.json`
```json
{
  "Harmony12XEnabled" : true,
  "Harmony12XLogChannelFilter" : 24
}
```
- `24` = Warn + Error ([Config.cs#L12-L14](https://github.com/BattletechModders/ModTek/blob/679fad49adaae4b23765b11705a1808c8422b855/ModTek.Preloader/Loader/Config.cs#L12-L14); bit values in `ModTekPreloader.config.help.json`).
- It drops the ~3.1k Info "Patching…" listings from `HarmonyFileLog.log` and, via `HarmonyXLoggerAdapter`, from the main log.
- Use `26` again when debugging patch order.
- `0` skips the file logger entirely and leaves HarmonyX's built-in default channel filter.

## Out of scope (stay in PerfeBT)
CustomComponents, CustomUnits, CustomFilters and IRBTModUtils fixes target specific mods. They belong upstream in those mods, not in ModTek.

## Suggested PR order
1. **JsonReflection** → `BetterReflectionCache`: self-contained, no behaviour change, biggest measured win.
2. **DataLoader hot-reload monitoring off**: one-liner.
3. **BetterBTRL.HasChanges reset** (+ `IndexMemoryStore` sets it): bug fix in ModTek code.
4. **MDDB NOCASE TagSetTag index** in `MDDBCache.BuildCache`: persisted, one-time cost.
5. **Soundbank GCHandle leak**: correctness; then measure the cold-cache cost before any async prefetch.
6. **PopPendingRequest per-frame budget semantics**, then opt-in `DataManagerMaxConcurrentLoads*`.
7. **HarmonyX default filter 24**: discuss first (deliberate design).
8. **Internet check**: async version preferred; skip only as opt-in.
9. **Log verbosity**: config/modpack only (maintainer position in #251).
