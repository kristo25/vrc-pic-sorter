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
Checkpoint named files, install Magick.NET and establish bounded codec API.
