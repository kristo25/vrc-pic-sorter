# VRC Pic Sorter

VRC Pic Sorter is a local Windows tool for reviewing duplicate and similar images collected by VRCX. It scans the fixed `Emoji`, `Prints`, and `Stickers` categories, keeps unique images organized, and places possible matches in a persistent visual review queue. It also turns animated emoji sheets into GIFs, using the frame count, rate, and loop direction VRChat writes into the file name. See *Animated emoji* below.

## Safety model

- Filenames and metadata do not determine image identity.
- Exact matches use normalized decoded pixels, dimensions, GIF frame order, and frame timing.
- **Exact decoded matches are resolved automatically**: the archived copy is kept and the incoming copy is recycled, or permanently deleted when recycling is unavailable. See *Automatic duplicate resolution* below.
- Non-exact matches go to review by default. Opt-in custom similarity limits can recycle incoming near matches automatically; they never enable permanent deletion of non-exact images.
- Images are processed in path order. Each unique image moves into the archive immediately, so later files in the same scan are compared against the updated archive.
- Possible matches remain in their incoming folders while the Review queue stores references to them; scanning does not create or move a review copy.
- Clearing the Review queue leaves those incoming files untouched. Reviews created by older versions are still returned safely from the legacy holding folder.
- `Keep incoming` and `Keep match` use the Windows Recycle Bin for the image that is not kept. Permanent deletion is never used as a fallback.
- Completed operations and resolved reviews are compacted automatically; the activity history retains the newest 1,000 entries.
- Oversized or unusually frame-heavy images are rejected before full decoding to protect application memory.
- Interrupted moves and recycle requests are recorded in a durable operation journal and reconciled on restart.
- Archive fingerprints are stored locally and reused when a file's path, size, and modification time are unchanged. Every scan refreshes additions, removals, and changed files before matching.
- Settings save automatically when a field is committed; the newest choice wins and destination labels show saved settings. Unsafe overlaps with source, holding, or retained archive folders block the change. Scanning creates the output folder on demand and never creates a source folder.
- An unavailable archive root or unreadable supported archive image pauses changes for that category until coverage is complete. Cached records remain intact. An offline source is not treated as proof that a pending deletion succeeded.
- Animating a sheet only adds files. The sheet itself is kept and filed beside the GIF made from it, never deleted, and an existing animation is never overwritten by a sheet arriving later.
- Tests use temporary folders and never access the configured VRCX or archive folders.

## Automatic duplicate resolution

By default, an incoming image is resolved without asking only when it is the *same picture* as one already
in the archive. That requires an exact decoded fingerprint, including every frame and its timing;
a rounded similarity score of 100% is not enough. In that case the archived copy is kept and the
incoming copy is moved to the Windows Recycle Bin. When recycling is unavailable (for example,
on a network share), the exact incoming duplicate is permanently deleted after both copies are
rechecked. History explicitly records permanent deletion. **Scan now** also retries pending exact
matches in enabled categories, including those from a previously scanned extra folder. Watcher
events and **Scan another folder** only resolve incoming files within that scan's captured scope.

Three deliberate limits on this:

- A 100% match at a **different resolution** goes to review unless custom similarity limits are enabled.
- The archived copy's fingerprint is re-verified immediately before the incoming copy is
  discarded; changed or missing keepers prevent automatic removal.
- Changed, unreadable, or missing copies prevent automatic removal and leave the item for review.
  Non-exact matches never use the permanent-deletion fallback.

Recycled copies can be restored from the Recycle Bin. Permanently removed incoming copies cannot;
the verified archive copy is kept. **History** distinguishes these outcomes.

## Custom similarity limits

In **Settings**, enable **Use custom similarity limits** to override the preset with percentages from 0 to 100. Minimum must not exceed maximum; decimals are allowed.

- Below minimum: archive as unique.
- At minimum and below maximum: queue for review.
- At or above maximum: keep the archived copy and recycle incoming. If recycling is unavailable, a non-exact incoming file remains for review. Both copies are revalidated before recycling.

Limits use the raw score, not the rounded displayed percentage. Exact duplicates retain the existing verified removal rules. Changes apply to newly analyzed files; existing review items remain manual decisions. Known variants of the same animated emoji still receive review even below minimum, preserving duplicate-GIF protection. Disabling custom limits restores the selected preset. Existing saved settings default to custom limits disabled.

## Visual matching

Still images retain full-canvas comparisons and an additional view without fully transparent borders. Border alignment applies only when canvas dimensions differ and the visible content has a compatible aspect ratio; moving artwork within the same canvas remains a layout change. Animations retain detailed frame samples and now compare compact summaries of every decoded frame, allowing cyclic loop alignment while lowering confidence when an unsampled frame differs.

Similarity percentages are estimates, not probabilities or proof of identity. Compact animation summaries can miss small changes, and some edited or shifted images still need manual review. Exact-match removal continues to require the complete decoded fingerprint. Fingerprint feature version 5 causes older cached visual features to be recomputed during archive refresh.

## Animated emoji

VRChat saves an animated emoji as a single sheet: one image holding every frame in a grid. The
frame count, the rate, and the loop direction are recorded in the file name, like
`..._64frames_16fps_linearloopStyle.png`. Those three numbers are what the animation follows, and
the sheet is exported as a GIF from them.

**The name decides.** Measured against 56 real sheets, the name was right on 55; the best reading
of the pixels managed 43. So where the drawing on the sheet disagrees with what the name counts,
the name still wins and the disagreement is reported rather than acted on. Refusing a good sheet
was the more common mistake by a wide margin.

Open **Animations** to see the sheets still waiting on a decision. Selecting one shows:

- the sheet itself, with every cell the animation uses outlined, the cell playing now highlighted,
  and any art the name does not count marked;
- a preview of the GIF it will produce.

The same measurement draws the outlines and drives the export, so the picture cannot disagree with
what gets written.

**Frames**, **Frames per second**, and **Loop** can each be corrected before exporting, for the
occasional sheet whose name is wrong. **Export GIF** writes it; **Export missing** does every sheet
that has no animation yet.

Missing-only export and scans recognize retained numbered GIFs such as `name (2).gif`, even
before they are indexed. They leave that animation unchanged instead of recreating `name.gif`.
Manual **Export GIF** can replace the displayed animation after confirmation; if its content
changes after the list was loaded, refresh the list and confirm again. An unreadable archive
shows an error and disables export instead of presenting an empty queue.

The duplicate report in **Settings** refreshes the archive folders before listing copies.
Only equal decoded fingerprints qualify for bulk recycling. Same-emoji GIFs with different
pixels or timing remain listed for comparison and are preserved. An incomplete archive check
disables bulk recycling and explains which folders or files could not be read.

**Skip** sets aside a sheet not worth animating. Nothing is moved or deleted - the sheet stops
counting as work still to do, and stops being decoded on every scan. A skip is remembered by the
image's fingerprint rather than its path, so it survives a rename, a move, and an index rebuild.
*Show skipped* brings them back, *Clear queue* skips everything still waiting, and *Show exported*
lists the ones already done.

### Where the files go

```text
<output>\Emoji\Animated\<name>.gif          the animation
<output>\Emoji\Animated\Gif Ref\<name>.png  the sheet it was cut from
```

The folder reads as the GIFs you browse and, one level down, the atlases they came from. Everything
in it is indexed like any other archived image, so a second copy of an animation has something to
be compared against.

A ready-made GIF arriving in a source folder is filed with the animations and compared against
them. One that matches a sheet already in the archive is compared against that sheet's own
animation, generated for the comparison if it has not been made yet.

### Two things to know

- **The frame count comes from the name.** A sheet whose name overcounts exports blank frames; one
  whose name undercounts leaves art out. The export says which of the two happened and records it
  in **History**, and the count can be corrected by hand before exporting.
- A sheet whose pixels disagree with its name is reported as information, not as a scan failure.
  The scan still succeeded.

## Supported files

PNG, animated GIF, JPG/JPEG, WebP, and BMP are supported. MP4, `.temp`, and unsupported files stay untouched.

## Getting started

1. Run `VrcPicSorter.exe`.
2. Open **Settings**.
3. Choose the source folders and enable the categories you want to use.
4. Choose one main output folder. The default is `Archived Images` inside your `Pictures\VRChat` folder.
5. Settings save automatically as you change them. Select **Scan now** when you are ready; the output folder is created if it does not exist yet.
6. Use **Scan another folder** for a one-time recursive scan outside the configured VRCX folders.
7. Use **Start watching** when you want the app to monitor configured folders during the current session. Choose **OnDetection** to analyze each image as it arrives, or **OnInterval** to ignore individual arrivals and sweep the folders on a timer instead (default 60 seconds, range 15–3600). Temporarily unavailable folders are attached automatically when they return.
8. Review matches with **Keep incoming**, **Keep match**, or **Move as Unique**. A review holding several matches asks for confirmation once, not once per match.
9. Open **Animations** to turn emoji sheets into GIFs. See *Animated emoji* above.
10. **Stop** interrupts a running scan. It stops between images, never during one, so nothing is left half-moved.

Suggested VRCX source root:

```text
C:\Users\<you>\Pictures\VRChat
```

The default category folders are `Emoji`, `Prints`, and `Stickers` beneath that root, and the default archive is `Archived Images` beside them. The Pictures folder is located through Windows, so a Pictures folder redirected to OneDrive is found correctly.

The app suggests category folders beneath that root. Source paths remain editable. New files are written beneath the single output root, under their category. **Archive folders** in Settings decides what happens below that, for an image that came from `Emoji\2025-05\image.png`:

- **CategoryRoot** (the default) writes `<output>\Emoji\image.png`. Everything for a category lands directly in that category's folder.
- **CategoryYearMonth** writes `<output>\Emoji\2025-05\image.png`, one folder per month taken from when the image was written.
- **PreserveIncomingRelativeFolder** writes `<output>\Emoji\2025-05\image.png`, recreating whatever folders the image already sat in under its source.

Changing this decides where new files go. Existing archived files are not moved.

Use **Settings > Retained archives > Move them into my archive** to relocate old archives explicitly.
Overlapping relocation folders are refused, and inaccessible folders remain listed with an error.
Moving runs in the background; Stop finishes the current file and prevents the next move. Completed
changes remain after a partial failure or cancellation. Existing GIFs in retained dated folders are
reused in their original archive until it is explicitly relocated.

**Start with Windows** launches the app in background watching mode. Ordinary launches begin with watching stopped.

## Portable installation and removal

Download `VrcPicSorter.exe` from the [latest release](../../releases/latest). No installer is
required; keep the executable anywhere you can write and run it. Each release also carries a
`VrcPicSorter.exe.sha256` file, so you can confirm the download matches what the build produced:

```powershell
Get-FileHash VrcPicSorter.exe -Algorithm SHA256
```

The executable is not code-signed, so Windows SmartScreen shows *"Windows protected your PC"* the
first time you run a downloaded copy. Choose **More info > Run anyway** if you trust the source.
The app makes no network connections of any kind: it contains no HTTP client and no sockets, and
everything it reads or writes is on your own disk.

Before removing the executable, use **Settings > Clear local data** if you also want to remove settings, index, queue, and history. Clearing stops folder monitoring and Windows startup registration, and is blocked while a review or file operation still needs the state.

If a file operation needs manual attention, use **Settings > Operation recovery**. Safe retries are rechecked against the files on disk. Dismissing an ambiguous operation never changes either file and marks the affected index for rebuilding.

After removing the executable, local state can be removed manually from:

```text
%LOCALAPPDATA%\VrcPicSorter
```

If **Start with Windows** was enabled, disable it in Settings before removal. Its per-user registration is stored at:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

under the value `VrcPicSorter`.

## Build

Requires the .NET 10 SDK on Windows.

```powershell
dotnet restore VrcPicSorter.sln
dotnet test VrcPicSorter.sln -c Release
dotnet publish src\VrcPicSorter.App\VrcPicSorter.App.csproj -p:PublishProfile=Portable
```

The portable output is written to `publish`.

For isolated verification, `--data-dir C:\absolute\temporary\folder` redirects state, holding files, logs, and default image folders beneath that location. Windows startup registration changes are disabled in this mode.

## License

VRC Pic Sorter is released under the MIT License. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
