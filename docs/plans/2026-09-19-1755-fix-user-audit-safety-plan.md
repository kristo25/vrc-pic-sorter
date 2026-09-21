---
title: Repair the nine user-triggered audit findings
date: 2026-09-19
status: planned
type: fix
depth: deep
product_contract_source: ce-plan-bootstrap
---

# Repair the nine user-triggered audit findings

## Goal capsule

Fix F1–F9 without changing the user's archive workflow or weakening exact-match verification. Deliver in small, independently tested steps; do not release a partial repair that still permits out-of-scope deletion. This is an implementation plan, not a claim that the fixes or their tests already exist.

Origin: [2026-09-19 audit](../../../../../../2026-09-10/e/outputs/user-triggered-audit-20260919/AUDIT.md). The audit reproduced F1–F6 with isolated fixtures; F7–F9 are source-confirmed issues requiring deterministic regression coverage. Its baseline was 384 passing Release tests. No tests were executed while preparing this plan.

## Product contract

| ID | Required behavior | Finding |
| --- | --- | --- |
| R1 | A targeted scan or watcher event may mutate only incoming paths in its captured request. Explicit full reconciliation can process the pending queue. | F1 |
| R2 | Exact incoming duplicates keep the verified archived copy; use the existing authorized permanent-removal fallback when recycling is unavailable. Near matches remain review decisions. | F1, F5 |
| R3 | Existing GIFs remain discoverable through current and retained archive roots, including dated reference folders. Automatic export does not create a second copy. | F2 |
| R4 | Relocation never moves a destination into itself or claims success after incomplete source enumeration. | F3, F4 |
| R5 | Storage unavailability cannot prove successful deletion, archive emptiness, or incoming uniqueness. | F5, F6 |
| R6 | The latest settings choice wins. Scans and relocations operate against a coherent settings snapshot. | F7 |
| R7 | Relocation leaves the window responsive and honors Stop at a safe file boundary. | F8 |
| R8 | Errors and cancellation accurately report completed file effects and unresolved recovery. | F9 |
| R9 | Preserve existing state compatibility, exact-match checks, GIF replacement protections, and explicit retained-archive migration. | All |

**Key decision D1 — governs R2:** Permanently remove verified incoming duplicates when recycling is unavailable (session-settled: user-directed — chosen over moving them into a recoverable Duplicates folder). Do not ask this again or broaden it to approximate matches or arbitrary archive cleanup.

**Key decision D2 — governs R3, R6, R9:** Changing Main output directs new work there; moving old archives remains the separate retained-archive action. A settings save must not silently migrate old files.

### Scope boundaries

Repair all nine findings, their shared supporting code, and the tests needed to demonstrate safe behavior. Preserve existing uncommitted work on `fix/audit-f1-f7`; record its starting diff before implementation. No unrelated UI redesign, matching algorithm replacement, global storage abstraction rewrite, or bulk removal of existing user duplicates.

Implementation tests use disposable fixtures and isolated application state. Do not scan, move, delete, or rewrite the user's real collection as validation. Commits, pushing, PR creation, installation over the current build, and release are outside this planning request.

## Planning contract

### Technical decisions

1. Carry operation scope explicitly; do not infer authorization from membership in the global review queue. Preserve full **Scan now** reconciliation of pending extra-folder items through a distinct explicit full-reconciliation path. Watcher and **Scan another folder** remain bounded to their request.
2. Reuse existing path-boundary and runtime coordination helpers. Introduce narrowly scoped storage-observation results where boolean existence checks lose necessary information.
3. Pause mutations for an affected category when configured archive coverage is incomplete. Other fully indexed enabled categories may proceed. Keep cached entries and pending work; show which root must be restored. An unreadable supported image that prevents duplicate coverage also makes that category incomplete; unsupported files do not.
4. Resolve animation ownership from configured current and retained roots, using canonical path containment. Select a single most-specific owner only when mappings are consistent; ambiguous or linked paths fail visibly instead of guessing an output location.
5. Settings requests are versioned, latest-wins, and applied through the same operation boundary as scans/relocation. Background planning can be superseded; an older result cannot apply settings or open an actionable relocation prompt.
6. Move synchronous relocation I/O to a worker. Stop waits for the current move to settle and prevents the next move. Do not add a custom cancellable copy protocol solely to claim immediate cancellation.

### High-level technical design

These sketches describe boundaries and outcomes, not required class signatures.

Component relationships:

```text
UI / watcher -> explicit request scope -> runtime operation coordinator
                                      -> settings snapshot
                                      -> index coverage -> router / relocation
storage observations -----------------> recovery decisions
operation receipts -------------------> truthful UI and history
```

Mutation protocol:

```text
capture scope -> acquire existing operation boundary -> validate settings version
-> establish complete coverage -> reverify affected files -> perform file effect
-> persist receipt/state -> report actual outcome -> release boundary
```

Recovery state decisions:

```text
expected source present -> validate keeper and operation -> safely retry or attention
different source present -------------------------------> attention; never delete it
source confirmed absent, parent reachable -> operation-specific recovery validation
unavailable / denied / ambiguous ------------------------> pending attention, no success
```

Settings lifecycle:

```text
request A -> plan A pending
request B -> supersede A -> plan B -> operation boundary -> recheck B -> apply B
late plan A / stale prompt A -------------------------------------> discard
```

Mode decisions:

| Request | Pending queue reconciliation | Coverage incomplete |
| --- | --- | --- |
| Watcher / selected folder / selected paths | Only captured paths | Retain work; pause affected category |
| Explicit full Scan now | Enabled categories, including pending extra-folder entries | Retain work; pause affected category |
| Relocate retained archive | Validated relocation roots only | Partial/error outcome; retain root mapping |

Lock ownership belongs at the operation entry point; lower-level helpers must not reacquire the same non-reentrant gate. Do not hold the gate while waiting for user confirmation. Revalidate settings version, roots, and file identities after confirmation and before applying effects.

## Implementation units

### U1 — Restrict automatic queue reconciliation (F1)

**Covers R1, R2, R9. First priority.** Modify `src/VrcPicSorter.Core/Scanning/ScanCoordinator.cs`, carrying explicit scope from its entry points to the pending-exact loop. Materialize a canonical request set once. A targeted call cannot process unrelated pending entries merely because their category matches. Keep intentional full queue reconciliation separate and clearly named.

**Execution note:** Convert the F1 probe into a failing safe-behavior regression before changing the loop. Use `tests/VrcPicSorter.Tests/Scanning/ScanCoordinatorTests.cs` and watcher tests.

**Scenarios:** Folder B scan leaves pending exact file A untouched; watcher B does likewise; requested exact B is resolved with recycle and with authorized permanent fallback; explicit full Scan now resolves eligible A and B. Disabled categories, cancelled requests, stale/missing keepers, changed incoming content, and near matches never broaden deletion. Archive keepers remain intact. A settings change cannot add paths to an already captured request.

### U2 — Distinguish unavailable storage from missing files (F5, F6)

**Covers R2, R5, R9. Depends on U1.** Update `FileSystem/FileRouter.cs`, `Storage/OperationJournal.cs`, and `Scanning/ArchiveIndexer.cs` under `src/VrcPicSorter.Core`. Use explicit observations: expected file, different file, confirmed absent with reachable parent, unavailable/error. Missing ancestors, access denial, or an unreachable share remain ambiguous. Never infer successful deletion from `File.Exists == false` alone.

Recovery preserves pending journal/review entries when completion cannot be established. An accessible parent and absent source are only inputs: apply existing operation-specific keeper and identity checks before committing history. Preserve persisted enum meanings and old journal readability; any added fields are optional/versioned and old ambiguous records fail safely.

Index every configured relevant root into a coverage result. Do not filter unavailable roots away or evict their cached entries as confirmed deletions. Incomplete coverage blocks mutations for that category and reports the root/reason. Reuse the existing archive-duplicate refresh behavior where appropriate without weakening it.

**Tests:** Extend router, journal, indexer, and duplicate-refresh tests. Convert F5/F6 probes to safe expectations. Cover unavailable volume/parent, denied enumeration, partial enumeration, unreadable supported image, restored root, truly missing source in a reachable directory, changed/missing keeper, changed source, and legacy journals. Successful completed-delete replay produces history exactly once; unavailable storage produces neither a false success nor a false unique move. Include current-root failure with retained roots available and the reverse.

### U3 — Resolve GIF paths against the owning archive (F2)

**Covers R3, R9. Depends on U2's root/coverage contract.** Change `Atlas/AtlasAnimationCatalog.cs` and `Atlas/AtlasAnimationWriter.cs` under Core so both use the same validated archive owner. Preserve date-relative placement for `Animated/Gif Ref/<date>/sheet.png` and locate the existing `Animated/<date>/sheet.gif` even after Main output changes. Do not derive a root from the sheet's immediate parent.

**Tests:** Extend `AtlasAnimationCatalogTests.cs` and `AtlasAnimationWriterTests.cs`. Recreate F2 with a real exporter-produced GIF; expect reuse and one GIF. Cover current/retained roots, dated/undated sheets, missing GIF, overlapping mappings, unavailable retained roots, and case/trailing separators. Exercise catalog export and scan/backfill callers. Preserve numbered manual replacement path/hash checks, no-overwrite automatic export, and incoming companion behavior.

### U4 — Validate relocation boundaries and inventory (F3, F4)

**Covers R4, R5, R9. Depends on U2.** Update `FileSystem/ArchiveRelocation.cs` and application `SettingsDraft.cs`. Canonicalize source and destination pairs before planning and again before execution. Exact same-root pairs are no-ops. Reject strict ancestor/descendant overlap in either direction for relocation, including preexisting destination content. Settings validation prevents a new output from being nested inside a retained source where it creates recursive archive discovery; do not reject every harmless parent output selection solely because of a common ancestor. Apply checks to each actual category root pair.

Replace enumeration-that-catches-and-returns-empty with complete/partial/unavailable results. Unknown remaining counts stay unknown, not zero. Preserve retained-root mappings until verified successful completion and re-enumeration; absent/offline roots cannot be marked empty. Keep existing collision protection. Recheck link/reparse boundaries rather than trusting lexical containment.

**Tests:** Extend `ArchiveRelocationTests.cs`, `PathBoundaryTests.cs`, and `Services/SettingsDraftTests.cs`. Convert F3/F4 probes. Cover nested destination already containing files, reverse overlap, same root, case variants, missing or denied roots, mid-enumeration failure, destination disconnection, collisions, partial previous moves, repeat execution, and existing junction safeguards. No invalid plan moves any file.

### U5 — Make settings application latest-wins (F7)

**Covers R6, R9. Depends on U4.** Refactor the save orchestration around `MainWindow.xaml.cs` autosave into a testable sequencer, reusing `LatestRequestGuard` where suitable. Snapshot the entire draft including checkboxes. Check the request version after asynchronous planning and again inside the runtime operation boundary immediately before application. Discard stale results and prompts; ensure awaited save failures remain visible.

Audit every direct save and relocation entry point, not just folder pickers. Operations already underway retain their captured settings; the next operation sees the newest successfully applied snapshot. Serialize state persistence so an older disk write cannot overwrite a newer applied version. Present pending/failed settings truthfully rather than showing an unpersisted root as active.

**Tests:** Extend `LatestRequestGuardTests.cs`, `AppRuntimeTests.cs`, `SettingsDraftTests.cs`; add a focused settings-sequencer test file if needed. Use controllable planner completions, not sleeps: A slow/B fast, reverse order, failing B, rapid checkbox changes, stale prompt acceptance, save during scan, and save during relocation. Assert persisted state, active root, visible labels, and actual subsequent destinations agree. No nested-gate deadlock or lost exception.

### U6 — Run relocation off the UI thread (F8)

**Covers R4, R6, R7, R9. Depends on U4, U5.** Dispatch relocation work from the MainWindow entry point through the runtime boundary to a worker. Marshal progress and completion to WPF. Disable conflicting mutations while preserving responsive Stop and navigation. Cancellation is checked before the first and between subsequent moves; an in-flight move is allowed to settle.

Keep source/destination and completed-move outcomes available after partial failure. On restart, re-inventory both roots and retain unresolved mappings; do not assume a cancelled or crashed relocation rolled back. Do not overwrite collisions on retry.

**Tests:** A fake blocking mover proves UI dispatcher callbacks and Stop execute while the worker is busy. Release the current move after cancellation; assert its result is recorded and the next file is untouched. Cover cancellation before start, failure after one move, and restart after partial relocation. Later isolated manual smoke checks slow/cross-volume storage where available; unit tests cannot establish real SMB responsiveness.

### U7 — Report actual partial outcomes (F9)

**Covers R8, R9. Depends on U1, U2, U6.** Replace MainWindow's blanket failure message claiming no deletion with operation-aware results. Distinguish recycled, permanently removed, moved, skipped, failed, and unresolved outcomes. A file effect followed by a persistence failure is an unresolved recovery case, not proof that nothing happened. Where receipt detail is unavailable, say the action failed and some effects may have completed; do not invent zero counts.

**Tests:** Extend `Services/AsyncCommandRunnerTests.cs`, router/journal tests, and a focused outcome-presentation test. Inject failure after the first removal, after a file effect but before journal commit, during refresh, and during mixed-action cancellation. Assert correct history/counts and no false rollback or no-deletion claim. Use disposable files and fake recycling only.

### U8 — Integration, review, and isolated build validation

**Covers R1–R9. Depends on U1–U7.** Run the regression suite and combined scenarios below, review the final diff for destructive paths and settings races, then publish to a new isolated output directory. Record exact commit plus dirty-diff identity, test results, and executable hash. Preserve the existing executable as a reference, without presenting it as fixed.

## Verification contract

1. Before implementation: capture branch, HEAD, dirty diff, existing tests, state formats, and runtime gate ownership. Re-read applicable instructions. Confirm audit line references against current source. Preserve all preexisting changes.
2. For each unit: first demonstrate the relevant failing safe-behavior test, apply the minimal repair, then pass its targeted tests. Port probe behavior into the repository tests; never keep a test whose expected result is the defect.
3. After integration: run `dotnet test VrcPicSorter.sln -c Release --logger trx`, retaining the fresh TRX. The historical 384-test baseline is a floor for retained coverage, not a target exact count; new tests should increase it. Investigate failures rather than deleting coverage or lowering thresholds.
4. Build and publish Release using the repository's documented Portable profile: `dotnet publish src/VrcPicSorter.App/VrcPicSorter.App.csproj -c Release -p:PublishProfile=Portable`, overriding the publish directory to a fresh test-build location. Run packaged startup with `--data-dir` pointing to an absolute disposable directory and disposable media. This documented isolation redirects state, logs, holding files, and defaults, and disables startup registration changes. Verify version/build identity; compilation alone is not a UI or storage test.
5. Combined workflow: queue exact A, scan B, change output twice, keep the old archive retained, export its dated GIF, disconnect a configured archive via a test seam, reconnect, explicitly reconcile, and relocate into a valid empty destination. Verify scope, file inventories/hashes, one GIF, latest settings, history, and recovery after interruption.
6. UI smoke: change Main output rapidly; confirm subsequent files go there; relocate a disposable archive; exercise Stop; induce a partial error and inspect its message. If real slow-network or cross-volume facilities are unavailable, report those scenarios as unverified. Never substitute a live-user-library run.
7. Final review checks every mutation call site against scope, coverage, identity revalidation, keeper survival, journal outcome, cancellation, and error reporting. No shipping while F1 or any unresolved loss-of-data regression remains.

## Delivery and recovery checkpoints

- Checkpoint A: U1–U2 pass focused tests; scoped deletion and ambiguous recovery are safe before any packaged test.
- Checkpoint B: U3–U4 pass; archive ownership and relocation inventory are consistent.
- Checkpoint C: U5–U7 pass; latest settings, responsive relocation, and truthful outcomes work together.
- Checkpoint D: U8 passes; deliver a separately named test build and evidence. Report any environmental validation gaps explicitly.

Before any later use with real data, preserve state/configuration/journal backups and keep unresolved operations visible. Prefer backward-compatible additive state changes. Do not automatically downgrade a binary against state containing pending operations it may misinterpret; validate compatibility first. Restoring a settings backup does not restore deleted images or reverse moved files. Permanent deletion is not rollback-capable, which is why its scope and identity tests are release gates.

## Definition of done

- F1–F9 each have a repaired code path and passing regression evidence, with GUI-specific validation recorded separately.
- Current and retained archives, exact fallback deletion, near-match review, manual GIF replacement protections, and shared-operation gating still behave as specified.
- No unselected incoming file is changed by a targeted action; unavailable storage never produces false success or false uniqueness.
- Settings, disk persistence, displayed paths, and subsequent destinations agree after competing saves.
- Final test build is traceable and isolated; no real collection was modified during validation.
- Final report lists each finding as fixed, blocked, or unverified with evidence; nothing is silently omitted.

## Assumptions and remaining risk

The full Scan now action retains its existing intent to retry queued exact matches, including extra folders; narrower operations do not. Archive coverage failure pauses only affected categories. These conservative technical choices implement the existing request without expanding deletion authorization.

A filesystem can change between checks. Preserve existing identity/handle protections and inspect the final destructive boundary; preflight checks alone cannot promise safety against arbitrary external replacement. Network failures and in-flight cross-volume moves can outlast a cancellation request. Tests and staged validation reduce regression risk; they cannot guarantee that no bug remains.

## Plan review

Author review checked all nine findings against requirements, implementation units, and verification gates. Particular checks: no repeated deletion permission request; scope captured before queue processing; unavailable versus truly missing distinctions; destination overlap in both directions; latest-wins persistence; no nested runtime gate; safe cancellation boundary; no claim that a backup reverses deletion.

A separate read-only reviewer applied coherence and feasibility lenses to the full plan and audit, with source spot-checks of indexing, settings/runtime coordination, and relocation. It returned no consequential findings. Coverage limitation: both lenses were performed by one reviewer, not two independent reviewers; no cross-model review or execution validation was performed. Author checks also verified the audit link, concrete test-file locations, and documented validation commands. No implementation has started under this plan.
