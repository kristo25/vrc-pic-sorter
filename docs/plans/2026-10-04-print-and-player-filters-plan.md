# Print-frame and player-name removal filters

## Contract

Preserve the latest interface and scan-accuracy changes. Add two settings, both default off:
- **Remove Print Frame** recycles the whole framed Print, even without an unframed counterpart. Never crop it.
- **Remove my images** requires a player name and matches the complete filename author exactly as entered, case-sensitive, without fuzzy or partial matching. Persist the name locally. Help text: “Stored locally in this app and used to match the player name in image filenames. Not uploaded.” Blank/whitespace-only names prevent enabling; clearing the name disables the filter.

Normal scans filter incoming images. A separate **Rescan archive with filters** button applies enabled filters to current configured archive folders, not retained previous archives. Confirmed detections recycle automatically; uncertain frame detections get Keep/Remove review. Never fall back to permanent deletion.

## Detection

Print detection is offline and Prints-only, returning Confirmed, NeedsReview or NoMatch with evidence. Inspect original-resolution footer and frame geometry independently of the picture background. Require actual “Captured by” or “Captured By” caption, adjoining name content, calendar/date region. White margins, image size, filename and timestamp alone never qualify. Missing caption evidence prevents automatic recycling.

Use fixed caption/icon templates and date-glyph recognition for observed 2048x1440 frames. Uncertain resized/damaged/unfamiliar layouts require review when frame evidence exists. A strict detector result is not a mathematical 100% guarantee. Inspect and measure the reference examples before choosing template tolerances; freeze tolerances before held-out validation. Do not weaken the caption requirement to increase recall.

Filename author parsing supports recognized Print, Sticker and Emoji naming structures, including spaces, underscores and Unicode author names. Use ordinal exact equality. Ambiguous or unsupported names do not trigger removal. Name field represents the filename author, not a verified account identity.

## Execution and persistence

Evaluate before duplicate decisions, archive moves and animation generation. Disabled filters must add no image analysis. Reuse decoded pixels where available and retain worker/memory limits; serialize mutations through existing locks.

Pending incoming reviews are eligible only when included in the scan and not involved in unfinished operations. If both filters match, recycle once and record both reasons. Archive rescan supports progress, cancellation, examined/recycled/review/failed counts. Do not cascade to undisplayed companions. Preserve generated-animation suppression so deleted generated files do not immediately reappear.

Filter reviews show one image, its incoming/archive origin and reasons, with **Keep** and **Remove**:
- Remove revalidates, recycles and advances only after success.
- Keep remembers a content-bound exception per file and filter reason. Incoming files continue normal duplicate/archive processing; archive files stay in place.
- Filters override older duplicate-review Keep protection, as explicitly requested. Filter-specific Keep exceptions are respected.
- Exceptions follow app-managed relocation. Changed/replaced files do not inherit them.

Extend settings with disabled-default switches and the local name. Add typed filter evidence, review origin/reason and content-bound exceptions. Older state/reviews/journals remain readable. Add journaled filter recycling that requires no survivor. Reuse verified recycling including local K: staging; recheck identity and applicable settings immediately before removal. Failures leave files intact and visible for retry. Cancellation stops further removals; interrupted recovery never repeats completed work.

## Implementation units

1. Establish labelled local fixtures and baseline; implement detector and exact author parser with regression tests.
2. Implement persistent settings, filter-specific review/exception records, and verified journaled recycling.
3. Integrate incoming scans, archive rescan, settings controls, Keep/Remove actions, counters and cancellation.
4. Perform independent review, full regression and runtime validation; package and verify the portable executable.

## Mandatory proof of working behavior

Compilation is not acceptance. Do not call the work complete until the following results are recorded against the final source/build.

### Detector evidence
- Read the supplied framed/unframed pair and additional local Prints without changing originals. Build a labelled corpus including white-background content, both caption capitalizations, varying inner-picture sizes, Unicode names, unrelated white borders, missing captions/dates and the known unframed `.000_prnt_` example.
- Separate development and held-out examples by image family so framed/unframed versions of the same picture cannot leak across splits. Record counts, labels, detector outcomes and errors; do not treat every unlabelled file as a negative.
- Freeze detection rules before evaluating holdouts. Require zero **Confirmed** outcomes on labelled negatives, retention of unframed counterparts, and successful detection of the provided frame including white-content positives. Inconclusive evidence must not auto-remove.
- Demonstrate edited/damaged/unsupported frame examples reaching review or NoMatch safely. Clearly report false negatives and remaining coverage limits instead of promising universal accuracy.

### End-to-end isolated evidence
- Use disposable copies and dedicated app data/settings. Never enable test removals against the live collection.
- Run real scanner paths for each filter separately, both together and both off. Confirm exact retained/recycled files by before/after inventory and hashes, not just counters or mocked calls.
- Prove framed Prints recycle whole; matching personal filenames recycle in all categories; similar names, case differences and ambiguous filenames stay safe. Prove no counterpart is required.
- Exercise archive-rescan automatic confirmed removal, uncertain Keep/Remove review, restart persistence, remembered exceptions, and overriding older duplicate Keep decisions.
- Verify exceptions after relocation, changed-content invalidation, duplicate review reconciliation, no companion cascades or regeneration, and one recycle when both filters match.
- Exercise disappearance/change before removal, locked files, recycling failure, cancellation, repeated clicks and interrupted journal recovery. Verify no permanent-deletion fallback and no duplicate completed removal.
- Test actual Windows recycling/local staging on disposable fixtures, including K: when available. Do not disconnect a real share. If unavailable, document it as unverified rather than claiming real K: proof.

### UI and final build evidence
- Verify default-off settings, required-name validation, saved-name help text, archive button, counters, cancellation and Keep/Remove behavior in an isolated app run. Preserve unrelated buttons/layout.
- Run full existing regression suite, focused new tests, formatting checks and diff review. Address actionable review findings and rerun affected checks after fixes.
- Publish portable executable, record SHA256 and exact path, and run the delivered executable with isolated data. Include an end-to-end filter exercise on the delivered build; startup alone is insufficient.
- Record expected versus observed results and failures. Any required failed or unperformed gate remains unfinished work. Deliver the final build path and concise verified results plus material limitations. No GitHub publication is included.

## Continuity

Keep implementation progress in the separate progress checkpoint, not in this plan. Update it after each meaningful unit/test run and before interruption. Record checkout/branch, files changed, commands/results, evidence locations, failures, remaining work and the next concrete action. On resume, read the checkpoint and verify current Git state before continuing. Never mark a task complete solely because its code was written.
