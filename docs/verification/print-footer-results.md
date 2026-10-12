# Print footer fix verification

Date:2026-10-12. Branch:`fix/print-footer-layout`; base:`55e721d`. The GIF compatibility and defaults fixes remain included. Main and GitHub are unchanged.

## Debug Summary

**Problem**: Seven native-size framed Prints were unnecessarily queued for review with Remove Print Frame enabled.

**Root Cause**: PrintFrameDetector pinned the calendar/date to x1760/1798. World metadata moved the calendar to x1272–1347. A layout-only repair fixed one case; six additionally failed because normalized0/slash glyph masks differed under subpixel rendering. Caption/person scores already met their existing thresholds.

**Fix**: Search only the bounded native footer for a calendar immediately followed by a recognized valid date. Require photographer-name ink before that calendar so metadata cannot substitute for the name. Keep dimension, opaque-white-border, caption, person, confidence and per-file Keep safeguards. Group extra glyph masks within each character before applying the character-class confidence margin. Add3/7/9 digit templates and valid/invalid-date coverage. Cache the footer's dark pixels once without changing the similarity calculation or search order.

**Prevention**: Shifted footer tests failed before the fix. Added cases cover moving date blocks, metadata/name separation, nonadjacent dates, native subpixel variants, valid dates containing3/7/9 and invalid dates. Existing white-background, missing-signal, nonwhite/transparent-border and animation safeguards remain covered.

**Confidence**: High for the seven observed queued cases and preserved safeguards. Real native3/7/9 dates remain independently unverified; those new templates have synthetic coverage. Unrecognized/damaged/resized footers remain review-only.

## Verification results

| Check | Result |
|---|---|
| Seven real queued Prints, read-only | All changed from Uncertain to Confirmed |
| Full Release suite |682 passed;0 failed;0 skipped |
| Formatting |Clean |
| Dependency audit |No known vulnerable direct/transitive packages reported by configured NuGet sources |
| Portable publish/startup |Passed |
| Actual isolated scan |Seven framed copies recycled; three controls preserved; zero failures |
| Existing review recovery |A copied pending filter review completed and was compacted correctly |
| Real originals and actual queue |No writes; original image hashes unchanged |

The controls were an ordinary white image, a real frame with its caption removed, and a real frame with its photographer name removed. The first two retained archive copies; the last remained pending review. Repeated testing exercised normal exact-duplicate handling for the already archived controls.

Evidence under `C:/Users/krist/Documents/Codex/2026-09-10/e/outputs/print-footer-investigation/`: `before.json`, `diagnostic.json`, `glyphs.json`, `after-final.json`, `portable-scan.ps1`. Runtime fixture evidence: `outputs/print-footer-fixtures/result.json`, manifest and isolated profile. New0/slash masks came from one native Darkium footer; other queued names/layouts supplied additional real-image checks. Digits3/7/9 were rendered from the upstream Noto Sans diagnostic font; the font file is not packaged and no runtime dependency was added.

## Post-Fix Quality

**Scope**: Only this fix against55e721d; no prior GIF/defaults work reviewed or changed.

**Simplify**: Three independent passes completed; applied calendar-strip caching and stable test names; no reuse changes needed.

**Review**: Compound Engineering review complete, Ready to merge, no actionable findings. Receipt:`C:/Users/krist/Documents/Codex/2026-09-10/e/review-artifacts/ce-code-review/20261012-print-footer/review.json`. Static correctness and independent local adversarial reads; runtime evidence supplied by the implementing parent.

**Residuals**: Independently labelled native3/7/9 samples and a broader family-separated negative corpus are not established by these seven real cases and three controls. This is a recorded coverage limit, not a demonstrated false-removal defect. No tracker ticket created.

**Re-verification**:682-test suite, formatting, audit, final real-image probe and portable scan passed after simplification.

## Local build

`C:/Users/krist/Documents/Codex/2026-09-10/e/outputs/VrcPicSorter-print-footer-fix/VrcPicSorter.exe`

SHA256:`2370D1DF1F1922F8FE236198BC87CD3B9D8B43DE30E328BC81C43B2F6EE08E59`.

No UI/settings changes, archive rewrites, real-image removals, dependency additions or publication.
