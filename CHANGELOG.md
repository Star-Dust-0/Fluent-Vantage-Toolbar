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

