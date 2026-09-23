# Flashback 0.5.3 for Windows

A portable replay recorder using NVIDIA NVENC or AMD AMF hardware H.264 encoding. Close with X to keep recording in the system tray; minimize normally to the taskbar.

## 0.5.3 installer lock fix

Setup retries temporary file-sharing locks. A locked temporary uninstaller no longer turns a completed installation into a failure. If Windows holds an installed file longer, setup reports a retryable error and restores files it changed. Cleanup errors are logged separately; diagnostic details are written beside the installation folder as `Flashback-setup.log`. Saved clips and preferences are unchanged. Close an old setup window before running the new installer.

## 0.5.2 installation and timeline zoom

Download and run `Flashback-0.5.3-Setup.exe`. Quit any running Flashback using its tray menu first. Choose Install, then Launch. The installer includes the app, .NET runtime and FFmpeg; it needs no additional download or administrator access. Start menu and optional desktop shortcuts point to `%LOCALAPPDATA%\Programs\Flashback\Flashback.exe`. Windows Settings > Apps includes an uninstall entry. Uninstall removes installed application files while preserving saved clips and preferences. A future setup can update the same installation. This build is unsigned.

The trimmer now explicitly labels timeline zoom (for example, `Timeline · 4× zoom`). The small full-clip overview highlights the portion currently visible. Drag the highlighted window or click elsewhere in the overview to pan, without changing cut points. The adjacent minus/plus buttons change zoom; Fit restores the entire clip. Keyboard and wheel zoom keep the indicator and overview synchronized. These controls affect only the timeline, not video magnification or exported resolution.

Build setup with `./build-installer.ps1` after publishing the app. It uses the Windows .NET Framework compiler to embed a verified ZIP payload in one setup EXE; no additional installer compiler is needed.

## 0.5.1 recording reliability and controls

- Audio packets now retain their timing against the video clock throughout recording. Small device-clock differences are corrected gradually; missing spans retain their duration as silence. Speaker and microphone clocks are handled independently.
- Audio queues are bounded to two seconds. A video encoder that remains over 1.5 seconds behind for three consecutive seconds after startup is automatically reconnected. Completed replay segments are retained. A full encoder reconnection can omit unfinished footage and has a brief gap; normal display reconnects still keep audio running with black video.
- This targets accumulating delay that clears when recording restarts. It does not guarantee smooth capture when the GPU is saturated. No additional permanent process or CPU video encoder was added.
- Recording turns the button red. Clip selection, dropdowns and hover states follow the selected palette. Settings headers, switches and sliders have larger hit areas.
- Replay length is a slider from 30 to 300 seconds in five-second steps. Apply settings to use the new duration.
- Open Settings > Shortcuts > Trimmer shortcut guide, or press ? in the trimmer. New local controls include comma/period for frames, J/L for preview speed, K for play/pause, M for preview mute, Alt+arrows for volume, Up/Down for sections, Ctrl+Up/Down for zoom, G for timecode, B for split, C for snapshot, and E for export. Preview audio changes never alter the recording or export.

## What's changed

- Smoothness under load: a bounded timestamped frame history absorbs brief encoder stalls while keeping each picture at its original video time. Hardware encoding, resolution and frame rate remain unchanged.
- Smoother capture scheduling: three reusable NV12 readback textures let GPU copies finish asynchronously. The capture thread waits briefly for fresh desktop updates and releases desktop ownership immediately before acquiring the next frame. Recording uses normal process priority; trim exports remain below normal. This trades about 6 MiB of additional 1080p staging storage (plus driver overhead) for fewer GPU stalls, without another background process.

- One uniform app background. GPU capture avoids full-desktop CPU scaling, audio packets avoid a redundant copy, and encoder input queues are smaller. Reduced recording overhead has been measured locally; Siege game FPS recovery still needs confirmation.

- Dark interface with the existing app icon, a Clips home screen, and a corner gear for settings. Recording controls stay available on every page. Hover an icon to see its action.
- Display, encoder, headphones, microphone, storage, startup and notification options are grouped in Settings. Clip actions include play, scissors for trim/combine, reveal, external editor and delete.
- Minimize keeps Flashback in the taskbar; X hides it in the tray. Quit is available in the tray menu.
- Occupied shortcuts automatically use shared background input. F8 can save while another app owns the same shortcut and Flashback is unfocused. Both apps may respond. The listener is event-driven, keeps only current key-down state, and stores no typing history.
- Speaker and microphone icons mute/unmute new recording audio without restarting the buffer. Their state is remembered. If a source is disabled, its icon opens Audio devices for setup. Previously buffered audio is unchanged.
- Display capture now runs independently from the persistent hardware encoder and audio inputs. A display interruption sends black frames while audio continues. Capture retries indefinitely with a delay capped at two seconds; there is no reconnect-count cutoff. Counters and retry progress stay out of the Capture page.
- Saving stays available during display reconnects and while another save finishes. Requests queue with their own press times. A successful save resets the replay range to that press; newer footage remains available for the next clip. Failed saves do not reset it. Very short clips show fractional seconds in their filenames.
- The header contains recording status, the save hotkey and recording controls. The body shows saved clips with Windows Explorer thumbnails. Duration and device options remain in Settings.
- AMD startup now tests Direct3D11 conversion, then AMF conversion, then a compatibility conversion path. Each route still uses the AMD hardware H.264 encoder. The compatibility route uses CPU color conversion/resizing and is announced when recording starts; it may cost more FPS. This addresses the RX 7600 report where `vpp_amf` rejected converter initialization before the encoder could be checked. Physical RX 7600 confirmation remains pending.

## Start and save

1. Quit any older Flashback from its tray menu. Run `Flashback-0.5.3-Setup.exe`, choose Install, then Launch. After installation, open Flashback from your Start menu or desktop shortcut. No separate .NET installation is needed.
2. Open the bottom-left gear. Under Video & display choose the display, replay length, resolution, quality and FPS. Under Storage & startup choose where clips are saved. Click Apply settings.
3. Press Start recording to start buffering; it becomes a Recording button while active. Pausing clears temporary replay history, not saved clips.
4. Press the save icon/button or the save hotkey. A five-minute setting is a maximum: if you have recorded less, the clip contains the available footage. The first completed segment takes about two seconds.
5. Use the filmstrip icon to browse clips. Closing the window leaves Flashback in the tray; use the tray menu to quit.

The header shows recording resolution, measured encoder FPS and version. Recording FPS is not in-game FPS. The recorder captures the entire selected display, including other apps on it.

## Headphones and microphone

In Gear → Audio devices, enable speaker output and select the exact headphones/output you listen through. This pins recording to that Windows playback device independently of the selected display, app, tab or foreground window. Game and Discord sound must actually play through that device to be included. Follow Windows default instead switches sources when Windows changes its default output.

Enable microphone and choose its input to include your own voice. The default mic follows the Windows communications input. Playback and microphone are mixed into one stereo AAC track. Changing enabled sources or devices restarts the buffer; the top speaker/mic mute controls do not. Microphone capture needs Windows desktop-app microphone permission. Muting affects newly captured samples, not audio already buffered.

Display capture retries indefinitely, with bounded memory and capped retry delays. It uses black video while display frames are unavailable and keeps connected audio sources running. Audio-device interruptions may rebuild the recording session; a physically disconnected source cannot supply audio. Recoverable failures have no retry-count limit. Pause, Windows lock/sleep and Quit cancel recovery. Disk-full, invalid configuration and unrelated fatal encoder errors still report a failure rather than pretending to record.

## Shortcuts and notifications

- Save: Ctrl + Shift + F8. Start/pause: Ctrl + Shift + F9.
- Click a shortcut field and press a key; Ctrl, Alt and Shift are optional. Escape cancels. Apply to activate it. Single-key shortcuts work globally, including while typing. If another app owns the shortcut, Flashback listens for the same key in the background. Both apps may respond. Windows-reserved shortcuts and elevated/protected apps can still impose restrictions.
- The optional overlay appears immediately when Save replay or its hotkey is pressed, then updates with success or failure; saving runs in the background. Choose its corner and duration in Settings. Windows notifications are separate. Exclusive fullscreen requires RTSS running with its OSD enabled for the game; enable the RTSS option in Settings. RTSS is not bundled. When it is absent, the regular windowed/borderless banner is used. RTSS notifications use its global layout, may appear in captured frames, and depend on game compatibility.
- Startup and automatic buffering are optional and off by default.

## Clips and trimming

Clips save into game/app subfolders with local timestamps, for example `Rainbow Six Siege — 2026-09-19_14-22-08-125 — 60s.mp4`. A manual game-folder override is available in Settings.

Click the scissors in the left navigation to open the trimmer without a saved clip. Drop one MP4, M4V or MOV video onto the main window or trimmer, or choose Open video. You can also select a saved clip and click its scissors, or double-click it. Original files stay in place; imports are not copied or converted. Windows codec support determines preview availability. Use the unified timeline to scrub or drag the wide trim handles. The compact green/amber hand buttons mark start/end; their separate I/O badges show the shortcuts. I/O mark the current playhead; + adds the marked section (also numpad +). Change Add trim section in Settings > Shortcuts. With the timeline focused, arrows step one source frame, Shift+arrows half a second, Ctrl+arrows one second, and Home/End jump to clip edges. S splits, Ctrl+Z/Y undo/redo, and Space plays/pauses. Export a single marked range directly, or add each section you want to keep. Precise and rough modes sit beside Export. Sections play in chronological order; gaps are removed. Export creates a new MP4 and preserves the original. Precise export re-encodes and temporarily increases GPU/CPU work. Fast export copies compressed packets and joins them without re-encoding; it may include extra footage at keyframe edges and is intended for rough cuts. The original and existing exports cannot be overwritten. Deleting a clip asks for confirmation and uses the Windows Recycle Bin when available.

The library scans on demand. Visible rows request Windows Explorer thumbnails on a background worker and cache up to 24 at 168x94; minimized/tray windows do not start gallery scans. Configure an optional external editor under Gear → External editor.

## Recording choices and compatibility

Replay lengths: 30 seconds through 5 minutes in 5-second steps. Frame rates: 30, 60, 90 or 120 FPS. Resolution: 720p, 1080p, 1440p, 2160p or native; aspect ratio is preserved and smaller displays are not upscaled. Quality presets: Compact, Balanced or High.

Automatic encoder selection prefers the selected display's GPU. AMD requires a display connected to the AMD adapter; NVIDIA transfers captured NV12 frames to its encoding adapter. Intel Quick Sync and CPU H.264 encoding are not included. Hardware encoding reduces overhead but does not guarantee unchanged game FPS. Lower recording FPS/resolution if needed, especially with AMD compatibility conversion.

The independent capture worker relays a bounded number of raw frames through memory to the hardware encoder. A paced writer selects timestamped frames from a bounded history, preserving motion through brief encoder read stalls. At 1080p60 the history holds up to sixteen pictures (about 64 MiB of pooled storage); it grows on demand and never waits for capture. Long overloads still drop old history. Native DXGI capture resizes and converts desktop textures to NV12 on the GPU before readback. Compatible displays use this route on NVIDIA and AMD; unsupported converters or rotated displays use the older compatibility path. Capture retries start after 200 ms and cap at two seconds, resetting when frames return. This adds memory-copy overhead; game FPS impact has not been benchmarked. Encoder FPS counts repeated frames too and is not a measurement of distinct captured motion. Resolution stays fixed for a recording session across display-mode changes; restart capture to adopt a new aspect ratio.

Replay history uses a rolling disk buffer. Pending saves temporarily retain their needed chunks; saved-range footage is reclaimed afterward. Settings and error details normally live under `%LOCALAPPDATA%\Flashback`; test profiles use their supplied data directory. Saved clips are kept in your chosen output folder. Copy error details from the status area when reporting a failure.

## Build and diagnostics

Build with `build.ps1 -FfmpegPath D:\ffmpeg\ffmpeg.exe` using .NET 8 for Windows. Bundled FFmpeg must include the NVENC/AMF encoders and D3D11/CUDA/AMF capture filters.

Diagnostics accept `--data-dir <separate test folder>`: `--continuity-test`, `--continuity-hardware-test`, `--ui-test`, `--encoder-test`, `--startup-ui-test`, `--self-test`, `--edit-test`, `--audio-test`, `--playback-test`, and `--shortcut-logic-test` and `--shared-hotkey-test`. `--capture-test` and `--system-test` need an unlocked interactive desktop; the latter exercises keyboard/mouse input and audio. Audio diagnostics can emit a quiet test tone. Inspect the generated result files and any `test-failure.txt`; some older window-based diagnostics shut down before a failure exit code is delivered.

See VALIDATION.md for tested paths and limitations, and THIRD-PARTY-NOTICES.md for dependency licenses. AMD error 10 is AMF_NOT_SUPPORTED in [AMD's result definitions](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/blob/master/amf/public/include/core/Result.h); the alternate GPU conversion uses [FFmpeg's Direct3D11 scaler](https://ffmpeg.org/doxygen/trunk/vf__scale__d3d11_8c_source.html).





## 0.5.0 editor workspace

Rough cut is the default: it copies compressed streams quickly and its edges may extend to nearby keyframes. Choose Precise cut for exact selections. Precise export now seeks each retained section before decoding, avoiding discarded lead-in and gaps. All export paths preserve the original and refuse to overwrite existing videos.

- File / Edit / View menus expose open/recent videos, projects, snapshots, section actions, undo/redo and zoom. Ctrl+O opens a video; Ctrl+S saves a project.
- Save a `.flashtrim` project to resume the source, trim range, sections, playhead and cut mode. Named projects autosave after edits and flush on close. The source file must remain at its recorded location with unchanged size and modification time. Share presets and file-size limits are session controls, not part of the project.
- Ctrl+mouse wheel zooms around the pointer; the wheel pans. View offers zoom around the playhead and Fit timeline. Arrow / Shift+arrow / Ctrl+arrow step a frame / half second / second while the timeline is focused.
- Standard export preserves the selected cut mode. Share 1080p/60 and Compact 720p/30 produce H.264/AAC MP4, never upscale the source height or frame rate, and re-encode only kept sections. Set Limit MB to your upload limit, or 0 for no limit. MB means 1,000,000 bytes; no Discord subscription limit is assumed. A video bitrate budget is shown beside export. Completed size is verified, with at most one lower-bitrate retry; an oversize result is not reported as successful. A long selection in a small file necessarily loses detail.
- Video re-encoding waits for recording to pause by default. Uncheck the wait option to start anyway. One editor export runs at a time; Cancel also cancels a queued job. Capture and replay saves remain independent. Starting recording during an already running export does not suspend that export.
- File > Save snapshot exports a full-resolution PNG or JPG from the current playhead. Snapshot generation runs only on request. Existing MP4/M4V/MOV import support remains; this release does not add arbitrary-format imports or GIF/WebP output.
- Settings > Appearance offers Charcoal (nearly black), Midnight and Slate, plus Mint, Blue, Lavender, Rose or Amber accents. Changes apply and save immediately, without restarting capture. Start/end handles keep distinct semantic colors.

The reference archives are research material only and are not bundled or executed by Flashback. No Electron runtime or reference-project code was added.




