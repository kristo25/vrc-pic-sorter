# Startup defaults verification

- Branch: fix/startup-settings-defaults, based on published v1.6.0 commit3649b4e. Starting tree clean.
- Compared migration against the previous working checkpoint c809d46: all XAML layouts/buttons and default-setting values are unchanged. Matching parity was verified on28 images/378 pair scores during migration.
- Reproduced missing defaults in the real WPF window with isolated application state and the scanner gate held: OutputRoot stayed empty while startup waited for archive maintenance. Test failed before the fix.
- Fix: populate settings before recovery/cleanup starts; retain final refresh and existing values. Startup handler delegates to an awaitable private task for lifecycle verification.
- Regression verifies paths, Auto workers, Conservative similarity,82/99 limits,60-second interval and disabled removal filters while maintenance is blocked.
- Focused regression fails before the fix and passes after it. Full suite651/651 passes (`startup-defaults.trx`); formatting and portable publish pass.
- Targeted manual review: five production lines; existing default factory, layout, buttons and final refresh retained. No file-operation or matching change. Extra simplification/dedicated review skipped for this small startup-only change.
- Actual portable Clear local data action verified through UI automation on a disposable profile: custom7 workers/85–95 limits/123 seconds/frame filter enabled reset to Auto,82/99 display,60 seconds, filters off and default folder paths. Both UI text and persisted JSON verified. Test process stopped; real profile/archive untouched.
- Local build: C:\Users\krist\Documents\Codex\2026-09-10\e\outputs\VrcPicSorter-defaults-fix\VrcPicSorter.exe.
- User confirmed the reset action was Clear local data, matching the actual portable UI verification above. Defaults restored in both controls and persisted state; no additional code change or repeated test needed for this clarification. Fix is local on fix/startup-settings-defaults; published release unchanged.
