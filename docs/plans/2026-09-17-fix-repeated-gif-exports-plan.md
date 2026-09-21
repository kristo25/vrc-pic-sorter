---
title: Prevent repeated GIF exports and show current archive duplicates
date: 2026-09-17
artifact_contract: ce-unified-plan/v1
product_contract_source: confirmed-gif-diagnosis
execution: code
---

## Goal Capsule

Users can keep an archived animation without the app recreating the discarded copy, and can review the GIFs actually present in their archive.

## Product Contract

### Problem Frame

The animation catalog checks only the sheet-derived exact filename. Keeping a numbered GIF makes the unnumbered destination look missing. An isolated probe reproduced two byte-identical GIFs on current HEAD. The live archive also contains 40 same-emoji pairs and 104 unindexed GIFs; none of those 40 pairs have equal decoded fingerprints, so deleting them based solely on filename identity is inappropriate.

### Requirements

- R1: Missing-only export and scan-generated animations recognize retained numbered animations, including ones on disk but absent from the index and sheets under `Animated/Gif Ref`.
- R2: Explicit manual re-export remains possible for deliberate corrections, with predictable destination selection and no silent overwrite of an unrelated numbered variant.
- R3: Duplicate review refreshes configured archive indexes from disk, surfaces incomplete refreshes, and preserves files with differing decoded content/timing for individual review. Only verified exact matches qualify for bulk removal.
- R4: Export and scan operations preserve current path-boundary checks and never act on live user media during development verification.

### Scope Boundaries

Fix generation, archive duplicate reporting, regression coverage, and provide a locally published executable. Do not clean the real archive, change user settings, create commits, push, or open a PR.

## Planning Contract

### Decisions

- Reuse `EmojiIdentity`, `PathBoundary`, and `ArchiveIndexer`. Add one shared bounded disk lookup for existing GIFs, excluding redirected paths; the name establishes an existing candidate, not authority to delete it. Match within the intended archive destination scope and preserve distinct variants.
- Separate missing-only export from explicit re-export. Recheck existence immediately before automatic export rather than relying on a stale catalog item. An existing candidate suppresses automatic generation; manual correction is an explicit operation.
- Coordinate duplicate refresh and export plus its indexing follow-up through `ScanCoordinator`'s existing scan semaphore; the UI busy flag alone does not stop watcher scans. Separate already-gated internal helpers to avoid nested acquisition. An incomplete refresh must not show a reassuring zero or enable bulk removal from stale data.
- Keep non-exact same-emoji variants visible but out of bulk deletion, including when their names claim equal frame parameters.
- No new dependencies or state migration. Local code patterns suffice; no external research needed.

### Assumptions

The user wants the diagnosed behavior fixed, not automatic deletion of existing animation variants. A local build is sufficient delivery; installing or launching against live state is outside verification.

## Implementation Units

### U1 — Prevent repeat automatic exports

Files: `src/VrcPicSorter.Core/Atlas/AtlasAnimationWriter.cs`, `src/VrcPicSorter.Core/Atlas/AtlasAnimationCatalog.cs`, `src/VrcPicSorter.Core/Scanning/ScanCoordinator.cs`; tests in `tests/VrcPicSorter.Tests/Atlas/AtlasAnimationCatalogTests.cs`, `tests/VrcPicSorter.Tests/Atlas/AtlasAnimationWriterTests.cs`, `tests/VrcPicSorter.Tests/Scanning/ScanCoordinatorTests.cs`.

Start with failing safe-behavior regressions derived from the diagnostic probe. Share lookup across catalog and automatic write paths; retain explicit manual export. Cover numbered indexed/unindexed GIF, reference sheet, repeated calls, deletion/replacement between listing and export, unrelated identity, manual correction, corrupt/redirected candidates, and scan rerun. Use existing safe enumeration and cancellation patterns.

### U2 — Accurate and conservative duplicate review

Depends on U1 where shared outcomes affect export follow-up. Files: `src/VrcPicSorter.App/MainWindow.xaml.cs`, `src/VrcPicSorter.Core/Scanning/ArchiveDuplicateFinder.cs`, `src/VrcPicSorter.Core/Scanning/ArchiveIndexer.cs` or an existing runtime coordinator if needed. Tests: `tests/VrcPicSorter.Tests/Scanning/ArchiveDuplicateFinderTests.cs`, `tests/VrcPicSorter.Tests/Scanning/ArchiveIndexerTests.cs`, `tests/VrcPicSorter.Tests/Services/AppRuntimeTests.cs` as appropriate.

Refresh configured roots before duplicate reporting with serialized operations and visible warnings. Preserve export follow-up indexing. Restrict bulk removal to exact decoded matches with existing keeper verification. Test discovering missing GIF records, removing deleted entries, failure/cancellation behavior, non-exact same-name/timing variants, and preservation of matching content in different categories.

### U3 — Validate and package

Depends on U1 and U2. Run targeted regressions, full Release suite, diff checks, code review and any resulting focused retests. Publish a fresh Windows executable into a separate local output folder with its commit/diff provenance. Smoke-test only with isolated state/settings and disposable roots. Document residual runtime limitations and the executable path.

## Verification Contract

The known keep-numbered/delete-unnumbered/export sequence must leave one GIF with unchanged retained bytes. A second scan/export must add nothing. Explicit re-export must remain usable. Disk-only GIFs must appear in refreshed duplicate reporting. Non-exact variants must survive bulk cleanup. Failed refreshes must be visible. Full Release tests and isolated publish/launch must pass; compilation alone is not UI validation.

## Definition of Done

Plan reviewed before implementation, findings reconciled; all requirements implemented with meaningful tests; code review completed; clean verification recorded and fresh executable supplied. Real images and live state untouched, changes left uncommitted.

## Sources

Current source at `7d640e2`, prior confirmed GIF diagnosis and diagnostic probe, existing tests and safe filesystem helpers. No blocking questions; exact helper/interface layout is an implementation detail.

## Pre-implementation Review

Coherence, feasibility, design, and adversarial document reviews completed before production edits. One feasibility finding was accepted: serialize refresh/export using the scanner semaphore, not merely UI busy state. The plan above incorporates it. No remaining blocking findings. Confidence: high for bounded existing-code changes; UI behavior and packaging require the stated execution checks. Native session execution; no model elevation configured.
