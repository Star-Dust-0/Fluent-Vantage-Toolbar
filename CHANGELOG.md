## 1.1.0

### Changes
- The battery percentage on the main screen now stays readable at every charge level: the digits draw white on the green fill and dark on the empty part.
- When the app runs without administrator rights, a clear notice under the battery explains that hardware controls need them and how to fix it, instead of every control failing silently. On startup the app now also waits a moment for the elevated instance before giving up.
- Clearer error messages: when Windows or the hardware refuses a change (for example a setting that needs AC power or the internal display), the message says so instead of a generic failure.
- Reordering tiles on the main screen is more reliable.
- Battery details now show a zero cycle count instead of hiding it, and no longer accept impossible manufacture dates.
- Device names that were wrongly rejected (some Lenovo model names) are now accepted.
- Empty readings no longer show stray units like " Wh" or "%".
- Settings are saved atomically, so a crash or power cut while saving can no longer corrupt them; an invalid accent color falls back to the default instead of breaking the theme.
- Updates are verified more strictly: a release file without a SHA-256 checksum is refused, and downloads are only accepted from GitHub release hosts.
- Release notes shown after an update now cover only the version you installed.
- Fixed rare crashes when closing the flyout, opening pages, or clicking tiles.
- The app now runs on .NET 10 and Windows App SDK 2.6, the currently supported Microsoft runtimes.

## 1.0.0

### Changes
- Update from inside the app: when a newer release exists, the app downloads the matching installer itself with live progress and a cancel button, verifies its size and SHA-256 checksum, and runs it only after "Install now". If the download or verification fails, nothing is run and the release page opens as a fallback.
- The automatic update notice now offers the same in-app download instead of only a download link.
- The tray icon follows the Windows taskbar theme: a dark glyph on a light taskbar and a white glyph on a dark one, updating live when Windows switches modes.

## 0.4.0

### Changes
- Fix "Check for updates" failing with a connection error even when the internet works. The updater now recognizes the current release file names.
- Add the battery green (#9CDA9B) as an accent color option.
- Fix the window not appearing when opened from the tray icon after a restart or sign-in.

## 0.3.10

### Changes
- Fix the window sometimes opening full-screen instead of its compact toolbar size after a restart or sign-in.

## 0.3.9

### Changes
- Show manual update-check results inline, including checking, up-to-date, available-update and connection-error states.
- Share update status between Settings and About, with a download link shown only when a newer release is available.
- Correct Arabic update-control alignment.
