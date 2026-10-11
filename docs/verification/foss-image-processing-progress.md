# FOSS migration progress

## Starting point
- 2026-10-10: branch refactor/foss-image-processing created from 1e2b15d.
- Carried user-authorized changes: atlas routing in ArchiveRelocation/FileRouter/ScanCoordinator plus scanner tests; release drafts in CHANGELOG/README/THIRD-PARTY-NOTICES/App csproj; unsuccessful ImageSharp 4.1.2 upgrade in Core/Tests csproj.
- Old 3.1.12 tree: 638 tests passed with vulnerability warnings permitted. Current 4.1.2 tree cannot build without vendor license. Neither constitutes a green new release.
- Native execution; shared adapter/API is first dependency before caller migration. Main untouched.

## Pending
1. Checkpoint accepted starting state.
2. Capture old build baseline and implement/test library-neutral adapter.
3. Migrate callers/tests/tools; protect persisted identities.
4. Full regression, comparison, benchmark, portable and audit gates.
5. Review, delivery, merge and publication only after all gates pass.

## Next action
Close final review receipt against the persisted portable proof, commit, fast-forward main and publish v1.6.0. See foss-image-processing-results.md for final acceptance evidence.

## Completed implementation and current evidence
- Starting-state checkpoint c809d46; main untouched.
- Magick.NET 14.17.2 adapter and all production callers compile; no ImageSharp package references remain in src/tests/tools.
- Raw fixture helpers replace old test library; 645 Release tests passed, 0 failed/skipped (foss-final.trx).
- Dependency audit reports no vulnerable packages in Core/App/Tests; normal warnings-as-errors enabled.
- Formatting verification passed.
- Legacy decoder migration backs up state/sidecar, invalidates feature cache6, and holds unresolved Keep identities; two focused migration tests pass.
- Old/new real20-image corpus: identical exact identities, frame hashes/timing, and190 pair scores.
- Three warm rounds: local old median685ms/new1050ms; K old856ms/new1061ms. Lower new peak memory. Profiling attributes most slowdown to GIF native read/composition; opaque-frame skip experiment gave no repeatable improvement and was removed.
- Portable output VrcPicSorter-foss-1.6.0 created with native components/notices. Isolated --data-dir process38856 started responsive and was stopped after path verification.
- Review found synchronous native decoding on UI callers; DecodeCoreAsync now offloads via awaited Task.Run with cancellation checked inside, draining before releasing locks. This last edit needs regression/rebuild.
- Final review is in progress under outputs/foss-review. No merge/publication until complete.

## Final acceptance checkpoint — 2026-10-11
- All implementation steps and review fixes complete. Final suite: 651 passed, 0 failed/skipped; formatting and portable publish green.
- Expanded comparison: 28 real copied files, all 378 pair scores identical.
- Fresh final portable UI scan completed with 0 failures; 18 moved, 5 GIF exports, 5 exact duplicates resolved, 5 reviews queued.
- Evidence, benchmark limitations, executable hash and fixture boundaries persisted in foss-image-processing-results.md.
- Remaining: final review gate closure, commit, main merge, GitHub release and asset verification.

## Publication checkpoint
- Independent final review: Ready to merge. Migration committed as 5b2b018, fast-forwarded and pushed to main.
- GitHub release initially stopped at formatting: Windows Roslyn expected CRLF on three expression-member lines while Git normalizes C# to LF. Added a C# end_of_line editor setting matching .gitattributes; no runtime change.
- Next: verify formatting with the repository setting, commit correction, update the unpublished v1.6.0 tag and rerun gated publication.
- Formatting passed on GitHub after f28fce0. The second run passed build but exposed two fixed-delay test races (preview decoding and mutation before scan preflight).
- Preview assertions now await readiness; settle test uses the existing injected clock to change the file strictly between observations. System-clock runtime behavior is unchanged. Both focused regressions passed; full suite and review follow-up in progress.
- Follow-up review Ready; all651 tests and formatting pass. Rebuilt portable fresh fixture scan:23 examined,18 moved,5 animated,5 exact resolved,0 failed/skipped. Final hash in results document. Next: commit correction and rerun release gates.
- Third GitHub run confirmed the two timing fixes but exposed a watcher cancellation race: canceling the first category could release the scan gate before the waiting category's token was canceled. Added a cancellation check under the same scheduling lock after gate acquisition. Next: watcher regressions/full suite, review and rebuilt portable before rerunning publication.
- Watcher fix verified:8 focused regressions and651 full tests pass; formatting/review green. Rebuilt portable23/23 exact duplicates resolved,0 failures,archive preserved. Next: commit/push and rerun final GitHub release gate.
