# Audit repair progress

## Resume here

**Final checkpoint (2026-09-20): all nine repairs implemented; 421/421 Release tests pass; final portable publish and isolated startup smoke pass.** Build SHA256 F80F48C87A6EC94B13B96220D998A44525E1DD33393CD689573E6CD79EEA391F. Seven local review passes completed; formal merge/independent-validation/report was interrupted by usage limits. The implementing agent disposed the three saved candidates locally: two fixed, one not a defect under the plan's in-flight save boundary. See `docs/reviews/20260920-final-audit/local-completion.md` for evidence and remaining GUI/network validation limits. No further implementation is pending for confirmed findings. No commit/push/install/live-data changes.

Approved plan: `docs/plans/2026-09-19-1755-fix-user-audit-safety-plan.md`.
User authorized implementation and requested durable progress tracking.
Repository: `C:/Users/krist/Documents/Codex/2026-06-29/i/outputs/VrcImageCurator`.
Branch: `fix/audit-f1-f7`; starting HEAD: `7d640e231414e880a7e419ef82b64aae7ffd3224`.
Native implementation, serial units because existing uncommitted work must be preserved.
No commits, pushes, installation, or live-media changes. Use temporary fixture/state directories.
Permanent removal of verified exact incoming duplicates is already authorized when recycling is unavailable.

## Units

| Unit | Findings | Status | Evidence / next action |
| --- | --- | --- | --- |
| U1 | F1 | Complete | Explicit full reconciliation; targeted paths/folders stay scoped. 64 coordinator/watcher tests pass. |
| U2 | F5, F6 | Complete | 5 new/updated red cases then 132 affected tests pass. Ambiguous source stays pending; incomplete archive coverage preserves cache and pauses routing. |
| U3 | F2 | Complete | ArchiveOwner shared resolver; catalog/backfill/companion/filing use retained owner. Worker 190 tests green; final integrated suite still required. |
| U4 | F3, F4 | Complete | 4 red cases then 34 path/settings/relocation tests green. Unknown inventory retained/reported; overlap rejected before any move. |
| U5 | F7 | Complete | Latest-wins saves, UI->scanner gate, stale presentation and move validation; 20 affected tests pass. |
| U6 | F8 | Complete | Worker relocation, gated move/state rebase, non-cancellable persistence; 19 tests pass including blocked-progress responsiveness. |
| U7 | F9 | Complete | Honest partial/unknown errors; 64 router/journal/reporting tests pass. |
| U8 | All | Complete with review limits | 421/421 Release tests, diff check, final publish and isolated smoke pass. Seven local passes collected; formal review finish unavailable after usage limits, local disposition recorded. |

## Starting state

Historical audit baseline: 384 passing Release tests, not rerun yet in this implementation.
Already dirty tracked paths: README; App/MainWindow.xaml.cs; Core AtlasAnimationCatalog, AtlasAnimationWriter, AtlasGifExporter, FileRouter, AppModels, ArchiveDuplicateFinder, ScanCoordinator, OperationJournal; tests AtlasAnimationCatalog, FileRouter, ArchiveDuplicateFinder, ScanCoordinator.
Already untracked: two docs/plans files, Core/Atlas/ExistingAnimations.cs, Core/Scanning/ScanCoordinator.AnimationExports.cs, Core/Scanning/ScanCoordinator.ArchiveDuplicates.cs, tests/Scanning/ArchiveDuplicateRefreshTests.cs.
Preserve these prior changes. They include earlier GIF/export and exact-deletion fixes.

## Verification log

U1: `dotnet test tests/VrcPicSorter.Tests/VrcPicSorter.Tests.csproj -c Release --no-restore --filter PendingExactReconciliationHonorsRequestedScope --logger "trx;LogFileName=u1-red.trx"`: four targeted-scope failures, two full-scan passes. After fix, filter `FullyQualifiedName~ScanCoordinatorTests|FullyQualifiedName~WatchServiceTests` passed 64/64 (`u1-green.trx`). Results under tests/VrcPicSorter.Tests/TestResults.
The U1 worker hit its usage limit after writing the six-scenario regression test. Main agent observed red, implemented, and verified green locally. Further work uses local execution until worker availability is established.
Sandbox test launch cannot read Windows SDK metadata; authorized `dotnet test` escalation is used for isolated test runs.
U2: filter `FullyQualifiedName~RecoveryDoesNotCommitRemovalWhenSourceParentIsUnavailable|FullyQualifiedName~UnavailableConfiguredRootPreservesCachedIndex|FullyQualifiedName~UnreadableArchiveFilePreventsClaimingCompleteCoverage` failed all 5 as expected (`u2-red.trx`). Green filter `FullyQualifiedName~FileRouterTests|FullyQualifiedName~OperationJournalTests|FullyQualifiedName~ArchiveIndexerTests|FullyQualifiedName~ScanCoordinatorTests|FullyQualifiedName~ArchiveDuplicateRefreshTests` passed 132/132 (`u2-green.trx`). Existing test explicitly asserting unreadable archive permits unique routing was changed to the approved conservative contract. Existing current-parent completed-delete replay and move recovery continue passing.
U3 worker receipt: dated retained catalog existing/missing cases 2/2 red before fix, Atlas + ScanCoordinator 190/190 green (`u3-animation-regressions.trx`). Added ArchiveOwner and ArchiveOwnerTests. No new dedicated companion/follow-up/junction test yet; shared resolver integrated there and existing tests pass.
U4 red filter `OverlappingRelocationMovesNothing|MissingRelocationRootIsNotReportedAsAnEmptySuccess|OutputNestedInExistingArchiveIsRejected` failed 4/4; green filter `ArchiveRelocationTests|SettingsDraftTests|PathBoundaryTests` passed 34/34 (`u4-red.trx`, `u4-green.trx`). ArchiveRelocationStep now optional InventoryError; result optional InventoryComplete. UI reports incomplete instead of success, retains missing folders. MainWindow IsEmptyNow delegates to positive readable-directory check.
U5 worker hit usage limit after partial service/UI code and three tests. Main finished: stale persistence completion red (1 failure/3 pass), six sequencer/gate/stale-plan tests and surrounding settings/runtime/watcher tests 20/20 pass (`u5-red.trx`, `u5-green.trx`). No red claim for worker's initial three tests. Removed unused autosave migration prompt; explicit retained-archive action remains. Output labels display saved paths; new invalid output invalidates pending saves.
U6 `RelocationReturnsToCallerAndStopsBetweenFiles` failed because caller blocked; after worker implementation, ArchiveRelocation + SettingsSaveCoordinator 19/19 pass (`u6-red.trx`, `u6-green.trx`). Dedicated STA caller, controlled blocking progress, no live I/O; real slow SMB GUI not validated.
U7 actual router fake-recycle post-effect exceptions/cancellation reproduced false messaging 2/2 red; new honest fallback and related router/journal/reporting tests 64/64 green (`u7-red.trx`, `u7-green.trx`). Uncertain outcomes direct user to History/pending recovery rather than claiming rollback.
U8 first full Release suite: 416 passed, 0 failed, 0 skipped (`audit-repairs-full.trx`). Build treats warnings as errors. `git diff --check` passes (only Git CRLF normalization notices). First-use integration initially failed due to unused suggested archive retention; added `OutputRootIsSuggested`, true only for freshly generated defaults, false by default for all old saved state. Only unused suggestions with no indexed/history/journal evidence and no existing directory are exempt from retention. Previously selected/offline roots remain authoritative. End-to-end isolated first output scan now passes. No live state used.

## Recovery rules

Read this file, the approved plan, and the current diff before continuing. Never infer completion from a checked box alone: use test evidence and code. Do not repeat completed tests unless code has changed or concerns remain. Keep unresolved recovery state and media untouched.

## Active implementation notes

The entries below are historical checkpoints; the final checkpoint at the top supersedes their pending status and earlier executable hashes. Final build and review evidence: `docs/reviews/20260920-final-audit/local-completion.md`.

U1-U7 implementation is complete. U8 remains active: final review, full rerun, isolated publish and startup smoke.
2026-09-20 final-pass checkpoint: full Release suite now passes 417/417 with zero skips after all changes above and an additional offline-parent retention regression; git diff --check passes. Portable publish succeeded to C:/Users/krist/Documents/Codex/2026-09-10/e/outputs/VrcPicSorter-audit-fixes/VrcPicSorter.exe. Isolated startup smoke at outputs/audit-smoke-20260920-4ad9e13605c143bf9aa1cdd5b2aa5b4d: input idle true, responding true, expected window title, state/fingerprints only, normal CloseMainWindow exit. SHA256 704CE41E0C418EDA7D3BF9B9FD15B6F7A3D208F9081F9D27299B1D56015CC5FD. Any review fixes require rebuilding this artifact.
Final ce-code-review is in progress under docs/reviews/20260920-final-audit. Automatic approval review rejected the external Claude diff disclosure as unauthorized; no peer started. Local review fallback is active. Do not report external review success.
Review checkpoint: first correctness pass returned zero findings. Remaining lenses/validation/report are pending. One subsequent source-line change corrects settings failure wording to avoid claiming rollback when persistence succeeded but automation/inventory refresh failed. Rebuild before delivery. Build folder TESTING.md documents the isolated launch command.
Review repair implemented: reliability found File.Exists(destination) after successful File.Move can omit the actual move receipt when access disappears. Root additionally reproduced progress.Report IOException double-counting a completed move as LeftBehind, and progress OperationCanceledException losing the entire returned receipt. New ProgressFailurePreservesCompletedMoveReceipt theory: 2/2 red (review-relocation-red.trx), then relocation suite 15/15 green (review-relocation-green.trx). Removed post-success existence check, recorded successful move immediately, separated progress error handling from filesystem move accounting, and return stopped results preserving completed receipts on reporting failure/cancellation. Final full tests/publish still pending.
Review continuation: coordinator hit usage limit after four collected lenses; user said continue. Resumed from durable review artifacts, security and remaining lenses/finish are active without repeating completed reviews.
Latest validation after receipt repair: full Release 419/419 passed, zero skipped (audit-repairs-full.trx); diff --check passes with only line-ending notices. Security retry inspected repaired code and returned zero findings. Final packaged build is still the earlier snapshot until republish.
Latest package supersedes the previous smoke/hash: portable publish succeeded after receipt repair, SHA256 49B8BBA9B07B6BD2F7C31A985BE3BD2FCC455259B31A20E31481523F641D045B. Isolated smoke at outputs/audit-smoke-final-5f3aaeb0c6e043eca11481e4d5a97fb6: input idle/responding true, VRC Pic Sorter window, only state.json/fingerprints.json created, normal CloseMainWindow exit. No startup error log. Final code review receipt remains pending; republish only if more source changes occur.
Subsequent UI-review repair: settings restart could reactivate watching after explicit Stop, and the temporary stopped interval could interpret a Stop click as Start. Added WatcherLifecycleCoordinator: shared lifecycle gate and immediate requested-running intent, restart rechecks intent after awaited stop. AppRuntime Start/Stop/ApplyAutomation now share this coordinator; MainWindow toggle uses requested intent. Two controlled callback tests cover Stop during blocked restart and Start after blocked Stop; runtime/watcher/coordinator 7/7 pass (review-watcher-green.trx). Old race was source-confirmed; no pre-fix executable red claimed. Full rerun and package refresh are required again.
Other UI candidate concerns a new invalid edit during an already-started disk write. Root assessment: U5 explicitly allows operations already underway to retain captured settings and uses newest successfully applied snapshot. No newer successful save is overwritten; existing superseded-during-persist test covers stale presentation suppression. Final validator will determine disposition against the plan.
Integration also reproduced changing selected output A to B before the first scan: a never-created category folder in A blocked conservative coverage. New ArchivePathKnownMissing defaults false for existing state; newly selected paths record positive absence only by complete readable immediate-parent enumeration. A later change omits such a path only while positive absence remains proven and no indexed images/journal reference it. A successful index clears this marker. Offline/unknown roots remain retained. The expanded two-output first-use regression failed before this fix; runtime/settings/indexer 19/19 pass in integration-output-green.trx.
Latest changes after the first full run also guard stale save-error callbacks and reconcile automation after an already-persisted superseded save; the unexpected WPF error handler uses the honest partial-outcome message.
Simplification: three independent reviews collected. Removed unused TryCreateFolder, passthrough IsEmptyNow, and unreachable successful-index skipped-file branches. Kept freshness probes, no-overwrite destination precheck, and current save callback contract: proposed optimization/API changes did not justify new safety behavior during this repair. Root availability caching can wait for measured performance work. All safety guards remain.
