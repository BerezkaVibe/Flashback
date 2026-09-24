# Flashback 0.9.0 for Windows

A portable replay recorder using NVIDIA NVENC or AMD AMF hardware H.264 encoding. Close with X to keep recording in the system tray; minimize normally to the taskbar.

**[Download the latest Flashback Setup.exe](https://github.com/BerezkaVibe/Flashback/releases/latest)**: one installer with the app, .NET runtime and FFmpeg. No admin rights needed. The build is unsigned.

## Recent changes

- **0.9.0**: A finished-video view for the timeline: the small swap icon beside the video track switches from the whole recording to the video as it will export, with sections in play order, slowed and sped-up parts at their real length, and freeze frames as highlighted holds (drag the end slit to change how long one holds). Everything slides with its footage when holds and speed parts change, music included. Music and sounds get their own speed (0.25× to 4×), with or without keeping their pitch, or can match the speed of the part under them, and can start partway into a freeze's hold. The preview plays music where the export does. Fixes: Compress leaving out freeze frames.
- **0.8.3**: Much faster exports: text, pictures and shapes are prepared in parallel and remembered, so exporting again after small changes skips most of the wait; busy exports run nearly twice as fast. Dragging the playhead keeps up with the cursor on big edits. Fixes: large edits failing to export, freeze frames inside zooms holding too long, a freeze with an inset video's sound failing to export, opening another video keeping the last one's freezes and music, pasting freeze frames, arrow nudges on keyframed items, joined clips keeping separate desktop and microphone tracks.
- **0.8.2**: Draw tool (W) beside Shapes: draw freehand, drag a straight line, or click corners to join lines, picked from the arrow inside the button. Lines have a thickness, can be solid, dashed or dotted, and each end can finish in an arrow or a dot. Corners and line ends stay draggable. Shapes can be Outline only, for circling things, with dashed or dotted outlines. Freeze frames (F) are now a slit on the timeline: the picture holds for the time you set, then the clip carries on from the same frame, so nothing is skipped. Sections play in the order of their chips; drag a chip to rearrange them. Add a clip to the end (File menu, or drop a video on the timeline) to cut several videos together.
- **0.8.1**: The trimmer is now the editor. Shapes tool (D) with stickers and shapes that can blur or pixelate what's under them. The picture tool also adds videos that play over the clip, cut to a shape. Keyframes move, scale, turn and fade anything over time. Double-click text to type on the video. Freeze frames in the speed pop-up. Volume parts and music or sound files (V and N) on the audio timeline, with fades and ducking. Layers fold into one slim strip. Drag a part's ends, type its times or pick them on the timeline; copy and paste parts; nudge with the arrows. Faster GPU-accelerated export, a Performance settings tab with Lighter mode, and projects that keep every part with edit recovery.
- **0.8.0**: Text and picture tools (T and P): captions with fonts, outlines, gradients, shadows and shapes you can reshape by their corners; pictures and GIFs with crop, flip, borders and background removal; animations in and out; layers you drag to reorder; scroll to scale, rotate or fade on the video. Delete removes the clicked timeline part. Tools in a compact block.
- **0.7.5**: Zoom parts on the timeline (Q): aim with a box on the video, shape the zoom-in with an editable curve or a preset, save your own presets, optional zoom out; zoom parts can overlap speed parts.
- **0.7.4**: In-app updates now work (a green download icon appears beside the title when one is ready); speed parts from 0.1× to 4× with a slider; X and R hotkeys for the cut out and speed tools; one Esc leaves a tool.
- **0.7.3**: Faster Record (encoder check remembered, no freeze) with Record/Recording button; rename clips in place; slow-motion parts on the timeline and a slow-motion export speed; microphone noise suppression; fixed garbled Compress file names.
- **0.7.2**: Smoother scrubbing with the audio open; scroll to zoom the timeline, Ctrl+scroll for preview speed (0.1x to 4x); click-twice cut out tool that locks onto the playhead, with undo; ruler inside the timeline; shading on parts that will not be exported; Export menu with Copy and Compress; options in a pop-up; playback resumes after scrubbing.
- **0.7.1**: Cut out tool to black out video or mute audio; audio lanes fold away and load only when opened (faster trimmer); Copy button beside Export; tidier per-app mixer with app icons.
- **0.7.0**: Per-app recording mixer; faster alt-tab recovery (about 30 ms); lower memory; auto-start with games; optional separate audio tracks; trimmer with smart ruler, audio lanes, crop, GIF/MP3/MOV export and Compress for Discord.
- **0.6.1**: Built-in updates: Flashback checks GitHub Releases and installs new versions in one click.
- **0.6.0**: Settings in side tabs; every hotkey rebindable; record light; audio device lock, recording volume and quality; monitor names and bitrates; cleaner, video-first trimmer.
- **0.5.4**: Trimmer preview audio no longer drifts from the video. Smaller download.
- **0.5.3**: Setup recovers from file-lock errors during install.
- **0.5.2**: Single-EXE installer; timeline zoom indicator and overview.
- **0.5.1**: Steadier audio timing, automatic encoder reconnect, new trimmer shortcuts.
- **0.5.0**: Trim projects, share presets with size limits, snapshots, color themes.

## Start and save

1. Quit any older Flashback from its tray menu. Run `Flashback-0.9.0-Setup.exe`, choose Install, then Launch. After installation, open Flashback from your Start menu or desktop shortcut. No separate .NET installation is needed. Uninstall from Windows Settings > Apps; your clips and settings are kept.
2. Open the bottom-left gear. Under Video & display choose the display, replay length, resolution, quality and FPS. Under Storage & startup choose where clips are saved. Click Apply settings.
3. Press Start recording to start buffering; it becomes a Recording button while active. Pausing clears temporary replay history, not saved clips.
4. Press the save icon/button or the save hotkey. A five-minute setting is a maximum: if you have recorded less, the clip contains the available footage. The first completed segment takes about two seconds.
5. Use the filmstrip icon to browse clips. Closing the window leaves Flashback in the tray; use the tray menu to quit.

The header shows recording resolution, measured encoder FPS and version. Recording FPS is not in-game FPS. The recorder captures the entire selected display, including other apps on it.

## Headphones and microphone

In Gear → Audio devices, enable speaker output and select the exact headphones/output you listen through. This pins recording to that Windows playback device independently of the selected display, app, tab or foreground window. Game and Discord sound must actually play through that device to be included. Follow Windows default instead switches sources when Windows changes its default output.

Enable microphone and choose its input to include your own voice. The default mic follows the Windows communications input. Playback and microphone are mixed into one stereo AAC track. Changing enabled sources or devices restarts the buffer; the top speaker/mic mute controls do not. Microphone capture needs Windows desktop-app microphone permission. Muting affects newly captured samples, not audio already buffered. Noise suppression (Off, Light or Strong) removes steady background noise such as fans, hum and hiss from the microphone while recording, using about 1% of one CPU core.

Display capture retries indefinitely, with bounded memory and capped retry delays. It uses black video while display frames are unavailable and keeps connected audio sources running. Audio-device interruptions may rebuild the recording session; a physically disconnected source cannot supply audio. Recoverable failures have no retry-count limit. Pause, Windows lock/sleep and Quit cancel recovery. Disk-full, invalid configuration and unrelated fatal encoder errors still report a failure rather than pretending to record.

## Shortcuts and notifications

- Save: Ctrl + Shift + F8. Start/pause: Ctrl + Shift + F9.
- Click a shortcut field and press a key; Ctrl, Alt and Shift are optional. Escape cancels. Apply to activate it. Single-key shortcuts work globally, including while typing. If another app owns the shortcut, Flashback listens for the same key in the background. Both apps may respond. Windows-reserved shortcuts and elevated/protected apps can still impose restrictions.
- The optional overlay appears immediately when Save replay or its hotkey is pressed, then updates with success or failure; saving runs in the background. Choose its corner and duration in Settings. Windows notifications are separate. Banners show over borderless and windowed games.
- Startup and automatic buffering are optional and off by default.

## Clips and editing

Clips save into game/app subfolders with local timestamps, for example `Rainbow Six Siege — 2026-09-19_14-22-08-125 — 60s.mp4`. A manual game-folder override is available in Settings.

Click the scissors in the left navigation to open the editor without a saved clip. Drop one MP4, M4V or MOV video onto the main window or editor, or choose Open video. You can also select a saved clip and click its scissors, or double-click it. Original files stay in place; imports are not copied or converted. Windows codec support determines preview availability. Use the timeline to scrub or drag the trim handles; scroll over it to zoom, Shift+scroll to pan and Ctrl+scroll to change preview speed. The green/amber Start and End buttons (I/O) mark the playhead; the + icon adds the marked section (also + on the keyboard). Click the time readout to type exact times. Parts that will not be exported are shaded. The cut out tool blacks out video or mutes audio: click two spots (the cutter locks onto the playhead within 3 px), right-click a cut to restore it, Esc cancels. The speed tool (R) works the same way: click two spots to play that part at 0.5×, then click its speed tag to set anything from 0.1× to 4× with the slider or quick picks, or remove it. Speed parts and cuts never overlap. The cut out tool is X; Esc leaves either tool. Options also has a speed for the whole export. Change Add trim section in Settings > Shortcuts. With the timeline focused, arrows step one source frame, Shift+arrows half a second, Ctrl+arrows one second, and Home/End jump to clip edges. S splits, Ctrl+Z/Y undo/redo, and Space plays/pauses. Export a single marked range directly, or add each section you want to keep. Precise and rough modes are under Options; the arrow beside Export holds Copy to clipboard and Compress. Sections play in chronological order; gaps are removed. Export creates a new MP4 and preserves the original. Precise export re-encodes and temporarily increases GPU/CPU work. Fast export copies compressed packets and joins them without re-encoding; it may include extra footage at keyframe edges and is intended for rough cuts. The original and existing exports cannot be overwritten. Deleting a clip asks for confirmation and uses the Windows Recycle Bin when available.

Click a clip's file name (or press F2) to rename it in place; Enter saves and Esc cancels. The library scans on demand. Visible rows request Windows Explorer thumbnails on a background worker and cache up to 24 at 168x94; minimized/tray windows do not start gallery scans. Configure an optional external editor under Gear → External editor.

## Recording choices and compatibility

Replay lengths: 30 seconds through 5 minutes in 5-second steps. Frame rates: 30, 60, 90 or 120 FPS. Resolution: 720p, 1080p, 1440p, 2160p or native; aspect ratio is preserved and smaller displays are not upscaled. Quality presets: Compact, Balanced or High.

Automatic encoder selection prefers the selected display's GPU. AMD requires a display connected to the AMD adapter; NVIDIA transfers captured NV12 frames to its encoding adapter. Intel Quick Sync and CPU H.264 encoding are not included. Hardware encoding reduces overhead but does not guarantee unchanged game FPS. Lower recording FPS/resolution if needed, especially with AMD compatibility conversion.

The independent capture worker relays a bounded number of raw frames through memory to the hardware encoder. A paced writer selects timestamped frames from a bounded history, preserving motion through brief encoder read stalls. At 1080p60 the history holds up to sixteen pictures (about 64 MiB of pooled storage); it grows on demand and never waits for capture. Long overloads still drop old history. Native DXGI capture resizes and converts desktop textures to NV12 on the GPU before readback. Compatible displays use this route on NVIDIA and AMD; unsupported converters or rotated displays use the older compatibility path. Capture retries start after 200 ms and cap at two seconds, resetting when frames return. This adds memory-copy overhead; game FPS impact has not been benchmarked. Encoder FPS counts repeated frames too and is not a measurement of distinct captured motion. Resolution stays fixed for a recording session across display-mode changes; restart capture to adopt a new aspect ratio.

Replay history uses a rolling disk buffer. Pending saves temporarily retain their needed chunks; saved-range footage is reclaimed afterward. Settings and error details normally live under `%LOCALAPPDATA%\Flashback`; test profiles use their supplied data directory. Saved clips are kept in your chosen output folder. Copy error details from the status area when reporting a failure.

## Build and diagnostics

Build with `build.ps1 -FfmpegPath D:\ffmpeg\ffmpeg.exe` using .NET 8 for Windows. Bundled FFmpeg must include the NVENC/AMF encoders and D3D11/CUDA/AMF capture filters.

Diagnostics accept `--data-dir <separate test folder>`: `--continuity-test`, `--continuity-hardware-test`, `--ui-test`, `--encoder-test`, `--startup-ui-test`, `--self-test`, `--edit-test`, `--audio-test`, `--playback-test`, `--sequence-test`, `--lag-compare [clip] [edit.flashtrim]` (preview smoothness, for comparing builds), and `--shortcut-logic-test` and `--shared-hotkey-test`. `--capture-test` and `--system-test` need an unlocked interactive desktop; the latter exercises keyboard/mouse input and audio. Audio diagnostics can emit a quiet test tone. Inspect the generated result files and any `test-failure.txt`; some older window-based diagnostics shut down before a failure exit code is delivered.

See VALIDATION.md for tested paths and limitations, and THIRD-PARTY-NOTICES.md for dependency licenses. AMD error 10 is AMF_NOT_SUPPORTED in [AMD's result definitions](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/blob/master/amf/public/include/core/Result.h); the alternate GPU conversion uses [FFmpeg's Direct3D11 scaler](https://ffmpeg.org/doxygen/trunk/vf__scale__d3d11_8c_source.html).
