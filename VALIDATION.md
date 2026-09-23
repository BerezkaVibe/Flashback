# Validation of Flashback

## Version 0.3.6

- 23 encoder checks passed. Simulated RX 7600-only and mixed-vendor topologies select the intended encoder; explicit preferences are respected; failed hardware probes fall back only to another eligible hardware encoder; unsupported GPUs and cancellation are handled. AMD arguments contain no NVIDIA/CUDA dependency, CPU video encoding or CPU scaling. Native capture avoids scaling; reduced dimensions use AMF GPU processing. Bitrate presets, segment keyframes and preference migration are checked.
- A real 12-frame D3D11/NVENC encode probe passed on the installed RTX 3070 Ti without requiring desktop frames. This is an actual hardware encoder test, not a test of AMD hardware.
- 56 startup/recovery checks, 29 recording regression checks and 20 trimmer/library checks passed. The trimmer tests use actual NVIDIA encoding, verify retained color/audio sections, full decoding, cancellation and source preservation. Settings were rendered and checked with the new encoder selector.
- No AMD GPU is available locally. The AMF encode, D3D11-to-AMF GPU resizing and AMD trimming paths are implemented and their command construction is checked, but must be confirmed on the friend's RX 7600. No AMD gameplay FPS benchmark or physical AMD capture test is claimed.
- Intel Quick Sync is not implemented. AMD capture requires a display attached to the AMD encoding adapter; cross-adapter AMD capture is not included. NVIDIA keeps its existing CUDA transfer path for hybrid displays. Unsupported hardware never falls back to CPU video encoding.

Evidence: `test-output-036-encoders`, `test-output-036-startup`, `test-output-036-regression`, `test-output-036-editor`, and `test-output-036-ui`. No test recordings, preferences or diagnostic logs are included in the release ZIP.

## Version 0.3.5

- Release publish succeeds without compiler warnings/errors. No runtime dependency was added.
- 56 startup/recovery checks passed, including the audio-pipe-first failure path with a current DXGI 887a0026 diagnostic. The real recorder process reconnects automatically, completed pre-switch segments remain byte-for-byte intact, a replay spanning both sessions fully decodes, and Pause cancels recovery. A pipe-only failure is not classified as access loss.
- 13 audio checks passed: silent video, speaker-only, mic-only, and mixed recordings all export correctly. Decoded 440 Hz and 880 Hz tones identify the expected sources, including differing input sample rates. Real Windows speaker and microphone pipes concurrently supplied a second of samples (96 kHz playback, 48 kHz microphone); these live samples were discarded rather than saved.
- 10 controlled shortcut checks passed: function-key assignment, modifier handling, exact Windows registration-conflict feedback, settings rollback/persistence, Escape, focus loss, handler cleanup and a subsequent replay save. The controlled test feeds the same key-processing code used by the native handler, but does not prove physical keyboard delivery. The separate SendInput test received zero native function-key events on the current remote desktop, so physical F8 input remains unverified on the affected PC.
- 29 recording regression checks passed, covering buffer ranges, partial exports, persistence, overlapping-save exclusion, pause/restart, audio-off output and full decode. Settings were rendered and visually checked with the speaker and microphone controls side by side.
- The additional live NVENC + speaker + microphone test opened both audio inputs, but desktop capture supplied no video frames and startup timed out. Combined audio export is verified with generated video; end-to-end live desktop recording in this release still needs a test with an available interactive display. Evidence: `test-output-035-audio-hardware`.

Evidence: `test-output-035-recovery`, `test-output-035-audio`, `test-output-035-logic`, `test-output-035-direct7`, `test-output-035-regression`, and `test-output-035-ui-final` in the development workspace. Tests use isolated preferences and output folders. No test recordings or local logs are shipped.

The reported RTX 5070 Ti fullscreen transition, multi-monitor switching and physical F8 entry still need confirmation on the affected systems. Brief capture gaps cannot be recovered when Windows supplies no frames. Microphone and playback are mixed into one track; simultaneous capture from multiple playback devices is not implemented. In-game FPS impact is not benchmarked.

Tested locally on the user's RTX 3070 Ti / Windows PC on September 13, 2026. Tests use separate workspace data folders and a temporary animated desktop window. They do not change the user's normal Flashback preferences or startup registration.

## Version 0.3 library and trimmer

- 23 editor checks passed. Real NVENC exports joined two retained portions in chronological order, removed the middle filler from video and audio, cut between keyframes accurately, and supported audio-free input. Full output decoding succeeded.
- A red/green/blue test source with 440/880/1320 Hz audio tones was cut into red then blue. Pixel samples and audio frequency checks verified the green/880 Hz filler was removed and the retained picture and sound stayed paired.
- Invalid, reversed, out-of-bounds, non-finite and overlapping ranges were rejected. Existing output files and source recordings could not be overwritten. Cancellation removed the partial export, and source hashes remained unchanged.
- Library tests merged current and previous save folders, removed duplicates and ignored missing files/malformed history. External editor preferences persisted without requiring a replay-buffer restart.
- Actual Windows MediaElement preview opened H.264, advanced playback and sought to a chosen timestamp in a temporary window. Populated library and trimmer layouts were rendered and visually checked; the final compact library layout was rendered again after refinement.
- The 28 recording regression checks and 16 overlay/settings checks passed on the 0.3 implementation. Release publishing completed without compiler warnings/errors.
- No new runtime dependency or additional media executable was added. The library scans only when opened/refreshed or updated after a save, and the decoder/timer are confined to the open trimmer. Export re-encoding is temporary work and has not been benchmarked during a game.

Evidence in the development workspace: `test-output-editor-030-final/editor-results.json`, `test-output-030-regression/test-results.json`, `test-output-030-overlay/overlay-results.json`, and `test-output-editor-030-final/ui-library.png`.

Windows Recycle Bin dialogs and launching a separately chosen third-party editor are implemented but were not exercised against user recordings. External editors must accept a video filepath as an argument. Playback seeking may be approximate during preview; exported cuts were verified independently. Export produces a new H.264/AAC MP4 and is not a lossless edit.

## Capture verification retained from 0.2.1

The capture/encoding pipeline is unchanged by the library update. Its earlier hardware test results follow.
## Confirmed

- Release compilation succeeded with zero warnings/errors.
- 28 generated-video regression checks passed, including all ten replay-length selections (30–300 seconds), partial/full saves, overlapping-save exclusion, names, persistence, hotkey conflicts, bounded retention, decoding, stop/restart and audio-off mode.
- 16 overlay/settings checks passed, including preference migration, binding rollback, window styles, all four feedback states, and dismissal.
- 12 live system checks passed with the corrected capture path: actual global save/pause shortcut delivery; overlay visibility without stealing focus; clicks passing through to the test window; timed dismissal; WASAPI loopback with a real six-second test tone; live NVENC replay and full decoding; continuous static-screen capture; a full 30-second save; bounded disk retention; 720p/30 FPS without audio; native 2560×1440/120 FPS.
- FFprobe confirmed H.264 at 1920×1080/60 FPS plus AAC, H.264 at 1280×720/30 FPS without audio, and native 2560×1440/120 FPS. All four saved files from this live test decoded completely without errors.
- A further test confirmed the capture clock advanced 20.45 seconds across a 20-second still-screen interval and successfully saved a clip while the screen remained still.
- Save-success feedback is invoked only after the final MP4 exists. Notification exceptions cannot turn a completed save into a save failure.

Evidence: `test-output-system-cuda/system-results.json`, `test-output-system-021-long/system-results.json`, `test-output-021-regression/test-results.json`, and `test-output-021-overlay/overlay-results.json` in the development workspace. Test recordings and local machine paths are not included in the app package.

- The extended run passed all 13 live checks. Its full replay contains exactly 300.000 seconds of 1080p/60 FPS H.264 video and 300.011 seconds of AAC audio. The entire five-minute file decoded without errors. Temporary retention remained bounded at 155 two-second segments for the five-minute setting.

## Fixes found through live testing

The original D3D11 scaling filter failed to allocate a texture on this GPU. Windows Graphics Capture resized successfully but stopped providing frames on a static desktop; an FPS filter did not resolve live buffer starvation. The final implementation uses Desktop Duplication with repeated static frames, CUDA resizing, and NVENC. Reduced-resolution capture passes frames through system memory between D3D11 and CUDA; native-resolution capture feeds NVENC directly. No CPU video encoder or CPU scaler is used.

The save flag's dismissal timer now uses normal dispatcher priority. The live diagnostic runner returns a failing exit code when any check fails.

## Still requires gameplay testing

This is a desktop capture test, not a game benchmark. Average game FPS, frame-time impact, audio/video sync in gameplay, exclusive-fullscreen overlays, multiple monitors/mixed DPI, HDR, protected content, and capture exclusion of the flag have not been established. Windows accepted the flag's capture-exclusion request, but this is not proof it is omitted under every capture configuration.

When home, run a game in borderless fullscreen, compare the same scene with the buffer paused/active, and check the saved clip's picture, sound, game folder and overlay. Changing the display or playback device may require restarting the buffer. Recording settings and Windows sign-in startup options are available in Settings; first launch starts paused.

The built-in trimmer now supports single trims and multiple retained sections, joined in chronological order.



# 0.3.1 checks — September 13, 2026

- Release build succeeded. Synthetic capture regression: 29 checks passed, including an immediate partial save with a 300-second limit.
- Real RTX 3070 Ti / WASAPI system suite: 13 checks passed, including audio, 1080p60, still-desktop progress, partial/full saves, retention, 720p30, native 120 FPS, hotkeys and overlay behavior.
- The third-party report of “Value does not fall within the expected range” could not be reproduced on this PC. Corrected the loss of extensible audio format/channel-mask information in silent playback initialization; exact confirmation on the reporting PC remains pending.
- Top button/header layout rendered and inspected. Recording FPS is FFmpeg's measured encoder throughput, not in-game FPS; paused display shows the configured rate.


0.3.1 startup UI integration: 9 checks passed. Clicking the top Save clip button from the Saved clips tab exported a 4.000-second MP4 immediately after starting with a 300-second replay setting. Button states, actual dimensions, full version and failure-stage logging passed.

# 0.3.2 checks — September 13, 2026

- Release build succeeded with no warnings/errors. 29 capture regression checks and 15 startup/retry/UI checks passed.
- 15 real RTX 3070 Ti / WASAPI system checks passed, including a simulated first-device rejection followed by actual native-resolution CUDA transfer and NVENC encoding with audio. The immediate partial replay under a 300-second limit decoded without errors.
- The retry policy matches the supplied `OpenEncodeSessionEx failed: no encode device (1)` error, retries once, skips unrelated audio errors and avoids repeating an already-CUDA path.
- Actual hybrid integrated/NVIDIA hardware and the friend's RTX 3050 are not available here. The remote-machine diagnosis remains a likely adapter mismatch, not a confirmed physical reproduction.
- The display adapter report was verified to enumerate the local NVIDIA card. Startup monitoring now starts only after successful initialization, avoiding an asynchronous fault callback during a recoverable startup attempt.

## Version 0.3.3 explicit capture adapter selection

September 13, 2026: release build passed with zero warnings/errors. All 29 recording regression checks, 23 startup/mapping/UI checks, and 15 live hardware/audio checks passed. Live tests exercised explicitly bound D3D11 capture, CUDA resizing, native 120 FPS capture, desktop audio, partial-buffer saves, and a forced CUDA transfer with full output decoding on the RTX 3070 Ti.

Eight new mapping checks cover an AMD-connected display with a disconnected NVIDIA adapter first in DXGI order, adapter-local monitor indices, detached/missing outputs, explicit device arguments, proactive transfer, avoiding unnecessary native NVIDIA copies, and matching real DXGI output names to Windows screens. Evidence: test-output-033-regression/test-results.json, test-output-033-startup/test-progress.txt, test-output-033-system/system-results.json.

The supplied 0.3.2 log reports AMD driving the laptop display, an unattached RTX 3050 Laptop GPU, and a capture failure after the CUDA retry. Capture now binds to the adapter owning the selected display and transfers non-NVIDIA capture frames to NVIDIA immediately. The affected hybrid laptop is not physically available locally; its real compatibility and gameplay performance still need confirmation. No new dependency or ongoing adapter polling was added.

## Version 0.3.4 capture continuity, audio sources, and single-key shortcuts

September 13, 2026: the recording pipeline passed 29 regression checks, 50 startup/recovery/settings/shortcut checks, and 19 actual hardware/audio checks (98 total). Evidence: test-output-034-release-regression/test-results.json, test-output-034-release-startup/test-progress.txt, and test-output-034-release-system/system-results.json. After the final wording/render-only changes, the final package was rebuilt with zero warnings/errors and its startup suite/rendering checked again in test-output-034-publish-startup and test-output-034-publish-ui.

Recovery testing injects the exact AcquireNextFrame 887a0026 diagnostic and kills the encoder, then verifies the real fault event reconnects automatically, disables saves during recovery, keeps Pause enabled, and preserves prior completed segment hashes. Saves across capture sessions fully decode. The live NVENC/AAC test retained its pre-interruption buffer and exported a 12.043-second replay after recovery, including 8.317 seconds of earlier recorded time. Actual fullscreen-game transitions on the affected laptop are not physically reproduced.

Retry tests cover the shared three-per-minute budget, cancellation before restart, cancellation during recorder startup, rejecting unrelated errors, and resuming after a stable minute. Pause, Windows lock/sleep, and Quit cancel recovery. Reconnects retain segments only with compatible recording dimensions/settings. Gaps without captured frames and unfinished segments are not restored; incompatible format changes reset the buffer.

Audio tests verified an explicit playback-device source and nonzero recorded audio after switching focus between test windows. The available local machine has ONE display; this does not reproduce the user's second-monitor symptom. Simulated Windows notifications verify automatic default-output following, ignoring communications/microphone changes, pinning a fixed output, and disconnect detection. A missing-sample watchdog requests recovery after five seconds; the existing silent render stream distinguishes quiet audio from a stalled stream. Audio from multiple playback devices is not mixed together.

Single-key function/letter parsing, retained modifier combinations, invalid keys, Windows conflicts, and actual global F23/F24 save/pause event delivery passed. UI wording now clarifies that starting while a game is already open is supported, and Settings retains the optional automatic-buffer-on-launch control. Neutral settings and audio-source layouts were rendered and visually checked.

## Version 0.4.0 — dark UI, AMD conversion and capture recovery

September 19, 2026. Release build succeeded without warnings/errors. Local checks passed: 22 UI/navigation/mute checks, 27 encoder selection/probe checks, 58 startup/recovery checks, 29 synthetic recording checks, 20 trim/library checks and four live playback/mute checks. Evidence is in test-output-040-design-final, test-output-040-encoder, test-output-040-startup, test-output-040-regression, test-output-040-editor and test-output-040-playback.

The recovery test injects the supplied DDA ReleaseFrame failure with an audio broken-pipe message first. The real recorder fault callback reconnects, preserves prior completed segment hashes and exports a fully decodable replay spanning both capture sessions. Generic errors and pipe-only failures do not trigger blind retries.

AMD checks simulate rejection of the GPU conversion routes and verify selection of compatibility conversion without switching away from h264_amf. Probe and capture use the same selected route. Actual NVIDIA encoding and trimming passed. No physical RX 7600 is available here: successful recording, performance and color output on that card still need remote confirmation. Compatibility conversion can increase CPU use; it does not use a CPU H.264 encoder.

Rendered and inspected Capture, minimum-size Capture, grouped Settings, populated/empty Clips and the dark trimmer. Navigation and controls remain available at minimum size. Dropdowns display their friendly labels. Quick audio mute persists independently of draft settings, leaves buffer-restart comparisons unchanged and preserves PCM sample counts. Live playback on Speakers (JDS Labs Element IV) passed signal/mute/unmute checks using the same pinned audio pipe.

The combined audio suite exported all four speaker/microphone configurations using synthetic tones, with correct audible sources. Actual speaker samples and mute passed. The initial actual microphone test timed out waiting for samples. The initial interactive system test was blocked by Windows rejecting simulated keyboard input; a sandboxed desktop capture attempt was denied desktop duplication access. Controlled F8 entry/conflict checks passed, but its subsequent apply was blocked while the older app was still running. Those failures are retained in their test-failure.txt files and are not counted as complete passes. The user later quit the old app and is testing the new app outside the sandbox in test-output-040-interactive.

No user recordings, preferences or diagnostic outputs are included in the release archive. The recording UI does not add background video decoding, live thumbnails or continuous folder scans.

Live user verification, September 19: after quitting 0.3.6, the user ran 0.4.0 outside the sandbox, set F8 directly, selected JDS Labs Element IV playback and the FIFINE 683 microphone, and confirmed that a saved clip contains both audio sources. Two actual DXGI access-loss events reconnected with the same pinned devices. The resulting 43.197-second replay has 1920x1080 H.264 video at approximately 60 FPS and stereo AAC at 48 kHz; full decoding returned exit 0 with no errors, and audio was non-silent (maximum -15.5 dB). The user confirmed both sources by listening. Evidence: test-output-040-interactive/clips.jsonl, last-recovery.txt, decode-check.txt and audio-check.txt. The test clip and device identifiers are not shipped.

Shared shortcuts and taskbar follow-up: 11 shared-binding/window-behavior checks and 10 controlled shortcut-entry checks passed in test-output-040-shared and test-output-040-shortcuts-shared. Occupied native registrations fall back to RIDEV_INPUTSINK raw input; no polling, key suppression or injected game hooks are used. The native registration path remains in use for available keys. Checks cover shared save/pause, modifiers, repeat suppression, suspend/resume, rebinding cleanup, direct F8 entry, normal minimize, X-to-tray and restoring the window. A separate temporary test process owns F8 for the user's physical background-key check.

## 0.4.1 continuity and compact Capture (2026-09-19)

This section supersedes earlier retry-limit, save-exclusion and Capture-meter descriptions.

- Synthetic continuity diagnostics passed: 25 recoverable failures before success, capped delays, cancellation, six forced producer interruptions without restarting the encoder/audio session, queued saves, and reset-at-press replay ranges. Decoded video includes black frames during the outage; the mixed tone audio has no detected silence.
- Live isolated desktop test (`test-output-041-hardware`) passed on RTX 3070 Ti with pinned JDS Labs Element IV playback and FIFINE microphone. Seven producer connections left one encoder session running; reported capture FPS 59.83. Four saved outputs, including subsecond rapid clips, fully decoded. Black-frame detection found outage placeholders; the live AAC track remained present. This does not establish in-game FPS overhead or prove real driver-reset behavior.
- Final UI rendering (`test-output-041-ui-final`) passed 22 checks. Capture no longer has a progress bar, large duration heading or reconnect count. Icons remain labeled through tooltips and accessibility names.
- Final startup/recovery UI diagnostics (`test-output-041-startup-final`) passed. Save remains enabled through recovery, early five-minute-buffer saves work, recovery clips decode, and Pause cancels recovery and disables saves. An initial test found a pause-refresh timing issue; the final build corrects it.
- New persistent video input uses bounded raw-frame transfer and hardware H.264 encoding. It adds memory-copy overhead; game FPS and sustained multi-hour resource use remain unmeasured. RX 7600 physical validation remains pending. Real user testing of this build is underway.
- Flashback 0.4.1 was opened locally with the existing interactive F8/audio profile. Public Cloudflare downloads have not been updated in this turn.

## 0.4.2 gallery, frame pacing and audio timing (2026-09-19)

This section supersedes the 0.4.1 frame-transfer implementation and its encoder-FPS-only performance observation.

- Home now shows saved clips with Windows Shell thumbnails; recording status and shortcut badge moved to the header. The rendered gallery with an actual Siege clip was visually inspected. UI/navigation checks and lazy immutable thumbnail loading passed in test-output-042-ui-final.
- The save overlay uses no-activate, click-through and tool-window styles, preserves foreground focus, and is excluded from recording. Overlay diagnostics passed in test-output-042-overlay. The local interactive profile enables success notifications. Visibility above an exclusive-fullscreen game still needs user confirmation.
- Video transfer uses NV12 instead of BGRA on production paths, a large named capture pipe, a dedicated timer-paced writer and a single-frame queue that drops stale frames rather than back-pressuring capture. NVIDIA resize/color conversion now uses two CPU filter threads; H.264 encoding remains NVENC. CPU use and in-game FPS impact are not benchmarked.
- Live fullscreen flash/beep recording on the RTX 3070 Ti measured video 63.3 ms behind audio at three detected flash transitions in the final test (test-output-042-analysis/live9-sync-results.json). An earlier queued-frame implementation measured 266.7 ms. These are end-to-end playback/capture measurements, not a guarantee of zero latency or perfect game synchronization.
- A five-second moving-pattern region retained 233 frames after perceptual duplicate removal. The original reported laggy game clip retained 45 in a five-second region. The content differs, so this is diagnostic evidence of improvement, not a controlled FPS benchmark. Encoded frame counts/hashes alone do not prove smooth motion. Sustained real gameplay validation remains necessary.
- Event-driven WASAPI capture retains packet timing when valid and falls back to local arrival timing when a device reports an unrelated clock domain. This prevents inserting an erroneous large silent prefix. Seventeen audio checks passed: all four source combinations export/decode correctly, actual pinned JDS output and FIFINE microphone supply timed samples, and mute/unmute continues their pipes. Four actual playback signal/mute/unmute checks also passed. Evidence: test-output-042-audio-final and test-output-042-playback-final.
- No physical RX 7600 is present. AMD gameplay and multi-monitor/exclusive-fullscreen behavior remain unverified. No recording, device preference or test log is included in the portable archive.

## 0.4.3 native GPU conversion, faster reconnect and lighter UI (2026-09-19)

- Replaced the usual FFmpeg raw-frame capture producer with native DXGI capture and a Direct3D11 video processor. Scaling/color conversion happen before NV12 readback, avoiding full-desktop BGRA download and CPU resizing. NVENC/AMF encoding and a bounded frame pipe remain. This is not a zero-copy encoder architecture. Unsupported converters and rotated displays fall back to compatibility capture with a diagnostic log.
- Reconnect waits begin at 200 ms and cap at two seconds indefinitely. Receiving a frame resets the delay. Local forced-outage tests recorded 336-446 ms to reconnect, including each requested 200 ms interruption; six interruptions, simultaneous speaker/mic input, queued saves and audio continuity passed. These tests do not measure every actual exclusive-fullscreen game's mode switch. Evidence: test-output-043-recovery-final.
- In sequential 15-second desktop samples, recorder process-tree CPU averaged 1.431 cores on compatibility capture versus 0.447 on native capture; summed working sets peaked at 274.5 versus 232.7 MiB. Desktop content differed and the samples include static periods. They demonstrate lower sampled recorder overhead, not a controlled Siege FPS improvement. The user's reported 90% game FPS loss still requires a real gameplay check. Evidence: test-output-043-perf-compatibility and test-output-043-perf-native.
- Native GPU capture was confirmed in the actual recorder's frame report. The first live flash/beep test measured 30-46.7 ms of video/audio offset. A captured test-pattern frame was visually checked for coherent image output. Frame repetition during static desktop periods is intentional. Evidence: test-output-043-sync and test-output-042-analysis/live43-sync-results.json.
- Reduced the main encoder raw-frame queue from sixteen to four, capped filter threads at two, removed one audio packet copy, and reduced the thumbnail cache to 24 images at 168x94. Thumbnails still use the Explorer provider, only for realized visible rows. Requests queued after minimization/recycling skip extraction; minimized/tray saves skip full library scans. No continuous thumbnail generation is added.
- Uniform home background and the new trimmer were rendered and visually inspected. Twenty-five editor checks passed, including actual precise and stream-copy multi-section exports, full decoding, cancellation cleanup, original-file preservation, splitting and undo. The fast exporter aligns concatenation durations to frame boundaries to avoid duplicate timestamps. Fast cuts follow keyframes and may contain extra edge footage. No LosslessCut code was copied.
- Twenty-one notification checks passed, including five isolated RTSS protocol tests: slot ownership, result text, preserving another owner, cleanup, malformed bounds. Real RTSS game rendering is unverified: RTSS is absent and the user chose to defer installation. Exclusive-fullscreen visual notifications therefore are not active on this PC; the usual no-focus windowed/borderless overlay remains.
- Vortice/SharpGen MIT bindings and their notices are bundled. Physical RX 7600, sustained VRAM/GPU-load measurements, long-session stability and live Siege FPS recovery remain unverified. No private clips/settings are included in the package.

## 0.4.4 asynchronous readback and recording scheduling (2026-09-19)

- Inspected the user's latest 64.6-second Siege clip: it contains nominal 60 FPS video, but perceptual duplicate removal retained 103, 101 and 81 frames across three five-second samples. This indicates repeated captured pictures rather than a missing FPS container setting. Perceptual duplicate removal is an estimate, not an exact game-FPS counter.
- Native capture now cycles through three reusable NV12 staging textures and uses D3D11 Map(DO_NOT_WAIT). If the GPU copy is pending, it returns without blocking. A full staging ring drops acquisition work rather than growing memory. Desktop duplication ownership is held between ticks and released just before the next acquisition, following Microsoft's documented performance recommendation. A bounded 1-8 ms AcquireNextFrame wait avoids missing fresh frames because of zero-time polling. Normal encoder process priority reduces scheduling starvation under game load; no high/realtime priority or extra process was added. Hidden/null cursors skip GDI work, and cursor hotspot metadata is cached.
- The final moving test-pattern sample retained 272/300 frames after perceptual duplicate removal, versus 240 in a prior 0.4.3 test-pattern sample. The initial zero-wait experiment retained only 149 and was rejected. These runs were on the same PC but not identical game-load conditions, so they do not establish sustained Siege performance.
- Four matched flash/beep events measured 40.0-56.7 ms of video delay. The resulting clip fully decoded. Evidence: test-output-044-motion2, test-output-044-analysis, and test-output-042-analysis/live44b-sync-results.json. Real gameplay verification is still needed.
- Each completed save writes a compact local last-capture-performance.txt diagnostic with cumulative frame/readback counters and capture-call timing, to support investigation of subsequent in-game stalls. No UI polling, telemetry or continuous diagnostic file writes were added.
- This is a modest-memory, two-process capture path, not zero-copy capture. Three 1080p NV12 staging images hold about 8.9 MiB versus 3.0 MiB previously, before driver overhead. Hardware encoding and bounded queues remain. CPU/GPU saturation can still force repeated frames.

- Final hardware continuity checks passed with six forced capture interruptions, pinned speaker/microphone audio, rapid queued saves and cancellation. Five measured reconnects took 327-335 ms including the forced 200 ms outage. These are controlled outages, not measured Siege exclusive-fullscreen switches. Evidence: test-output-044-recovery.

## 0.4.5 timestamped frame handoff (2026-09-19)

- User testing rejected 0.4.4 as still laggy under Siege load. Its new gameplay clip retained 108 distinct pictures in a five-second sample, despite capture receiving approximately 58 pictures per second. Repeated pictures were still introduced downstream.
- Replaced the single-frame channel with bounded timestamped history. When an encoder pipe write stalls, the writer catches up using pictures from their original capture times instead of repeating whichever picture is newest. Past timestamps cannot consume future pictures. Outage markers and static-desktop heartbeats preserve black recovery and static scenes. Capture never waits for the encoder; long overloads discard old history. At 1080p60, sixteen queued pictures use up to about 64 MiB of pooled arrays, allocated on demand, plus the current image and existing capture buffers. No additional process or background polling was added.
- Twenty-one deterministic checks cover 180 ms stalled-consumer ordering, no future-frame substitution, bounded overflow, timestamped outage/recovery, static scenes and stale-source black frames. Synthetic continuity tests passed queued rapid saves, buffer reset, six capture interruptions, black placeholders, continuous mixed audio, cancellation and full decoding. Evidence: test-output-045-history and test-output-045-continuity.
- Corrected the hardware motion diagnostic to preserve microphone input, matching the user's speaker-plus-microphone configuration. Real NVENC/DXGI recording with both devices measured 296 pipe writes longer than one frame interval, with a worst write of 117.57 ms. The timestamped history dropped no entries. A five-second moving test-pattern sample retained 278/300 pictures after perceptual duplicate removal. This demonstrates resilience to observed pipe stalls, not a controlled Siege benchmark.
- The clip fully decoded. Five steady flash/beep events measured video 70 ms behind audio; the first event at playback startup measured 186.7 ms. Evidence: test-output-045-motion and test-output-042-analysis/live45-sync-results.json. This is not zero audio/video latency; gameplay performance and synchronization still need user confirmation.

User confirmation, September 20: the user reported that everything works perfectly with 0.4.5 after the Siege test. This is user-observed gameplay validation, in addition to the local diagnostic evidence above.

## 0.4.6 unified trimmer and explicit recording controls (2026-09-20)

- One full-width timeline owns the playhead, kept ranges and wider trim handles. Paused MediaElement timing can no longer overwrite a pending seek with zero before I/O marking. Preview seeks coalesce at approximately 30 Hz while dragging, with the final drag position applied immediately. The video occupies the full width above the controls.
- Timeline focus enables left/right source-frame steps, Shift half-second steps, Ctrl one-second steps and Home/End. Timestamp text fields retain normal editing. MP4 sample timing supplies the source frame rate rather than assuming the recording preference.
- Plus (main keyboard or numpad) adds the marked range. The binding is editable under Settings > Shortcuts and applies only in the trimmer; reserved navigation/mark shortcuts are rejected. It does not restart recording. Precise/rough mode and a concise keyframe warning sit next to Export. A single marked range can export without first adding it to the multi-section list.
- Main buttons now use compact Start recording, Recording and Save replay labels. No capture/encoder implementation changed from the user-confirmed 0.4.5.
- Twenty-six focused interaction checks passed, including stale-zero I/O regression, source-frame stepping, focus behavior, both plus keys, custom binding persistence, undo/redo, large handle hit targets and minimum layout. Twenty-two main UI checks and the existing twenty-five editor/export checks passed, including precise/rough exports, joined audio/video and preservation of originals. Normal/minimum trimmer and main-window renders were visually inspected. Evidence: test-output-046-trim, test-output-046-ui, test-output-046-editor. Physical mouse/key input and live WPF preview for this version still need user confirmation.

- Explicit light foregrounds now cover the derived trim window, list items and dropdown selected content; the green action buttons retain dark text for contrast.


## 0.4.7 external videos and compact trim controls (2026-09-20)

- Added an independent scissors navigation button that opens the trimmer without a selected library clip. Its empty view accepts a file drop or Open video. File drops also work on the main window and the populated trimmer. Supported containers are MP4, M4V and MOV; Windows codec support still governs preview playback. No import conversion, background decoding or duplicate source copy is introduced.
- Invalid or multiple-file drops leave the current edit intact. Valid replacement asks before discarding edited ranges; replacement is blocked during export. Existing sources are read without modification, and exports retain the original-file protections.
- Start/end controls use distinct green/amber hand icons and separate I/O key badges, matching the timeline edges. Transport controls, timestamp fields, section actions and kept-section chips are more compact. The empty section list collapses to give the preview more room. Precise/rough mode remains beside Export.
- Live playback testing identified that issuing a seek before switching the Windows preview into playback could resume at zero. Playback now enters Play before issuing its target seek. Pending seek acknowledgement also tolerates a delayed UI update without accepting a stale earlier position. The original failed checks remain in test-output-047-preview and preview2; the corrected live check passed in test-output-047-preview3, covering MP4 opening, paused I/O marks, resuming from six seconds and preserving a paused source-frame step.
- Thirty-three interaction/import checks and the main UI suite passed on the compact version. Normal/minimum layouts and the empty import view were rendered and visually checked. Evidence: test-output-047-compact and test-output-047-ui. Physical Explorer dragging still needs user confirmation; tests exercise Explorer file-drop payloads, import validation and replacement behavior.

## 0.5.0 export and editor workspace (2026-09-20)

- Release build passes with no warnings. Recording/capture pipeline is unchanged.
- Existing 25 editor checks passed with the new seek-before-input precise path: between-keyframe single cuts, multiple retained ranges, chronological picture colors and matching audio tones, silent media, decoding, cancellation and original preservation. Evidence: test-output-050-editor.
- Final 18 workspace checks passed: default rough mode, project round trip and close-time autosave, rejecting changed sources, zoom coordinate mapping, size validation and explanatory labels, minimum layout, palette persistence without capture restart, hardware share output below 1,000,000 bytes with joined audio/video, PNG/JPG snapshots and queued cancellation. Evidence: test-output-050-final-workspace. Hardware export was checked on NVIDIA; AMD share settings still need physical AMD validation.
- Final 33 interaction/import and 23 general UI checks passed. Wide/minimum layouts and nearly black styling were rendered and inspected. Evidence: test-output-050-final-trim and test-output-050-final-ui. The reference screenshot renders deliberately disable video playback.
- Overlay checks passed request/saved/error rendering, no-focus native styles, display exclusion, pending-to-result lifetime and RTSS protocol behavior. The save request now calls the existing Saving overlay before awaiting the recorder. Prior stored ShowSavingOverlay=false no longer suppresses request feedback; the general OverlayEnabled switch controls all three states. RTSS is still not installed on this PC; real exclusive-fullscreen rendering is not newly validated. Evidence: test-output-050-overlay.
- A single synthetic 60-second, 320x180/30 FPS source test retained seconds 55–58: previous decode-from-zero command 2526.56 ms, new input-seek export 2161.15 ms. Process startup and local load are included; this is not a controlled gameplay benchmark or a guarantee for other files.
- No external reference code, recordings, user settings, projects or test logs are included in release/source archives. References remain separately under references/supplied.

## 0.5.1 reliability, theme and shortcut verification (September 20, 2026)

- Accelerated two-hour timestamp simulations at +/-200 ppm for float32 and PCM16/24/32 stereo kept sample time within 2.019 ms. This tests the audio normalizer, not two hours of live gameplay.
- A 60-second encoded flash/beep fixture with simulated +200 ppm drift produced 29 decoded matching pulses; audio-minus-video offsets were 0.437 to 2.063 ms. Evidence: test-output-051-final-sync-media/sync-results.txt.
- 120 seconds of continuous synthetic frame-bridge recording exported a fully decoded 45-second replay. Maximum retained segments: 29. Warm app working set: 71.6 to 79.4 MiB (excludes FFmpeg/driver memory). Evidence: test-output-051-soak/soak-results.txt.
- Injected encoder backlog was detected by the recorder fault path and restarted through the normal recovery coordinator. Completed history plus new footage produced a fully decoded 13.16-second replay. Lag-monitor tests also cover startup and brief load spikes. Evidence: test-output-051-check3-backlog/backlog-results.txt and test-output-051-check3-clock/clock-results.txt.
- Continuity regression checks passed: six display interruptions, black placeholder with mixed audio, rapid save requests, continued recording and cancellation. Evidence: test-output-051-final-continuity/continuity-results.txt.
- Trimmer interaction and shortcut tests passed, including frame stepping, I/O, plus, undo/redo, speed limits, preview mute/volume, section navigation, zoom, validated timecode and text-field/repeat protection. Evidence: test-output-051-check3-trim-interaction/trim-interaction-results.txt.
- UI render/template geometry checks passed for red recording state, restored accent, palette-linked selection, larger full-header/switch/dropdown/slider targets and five-second replay values. Charcoal and Rose/Midnight renders and the shortcut guide were visually inspected. Template geometry tests are not simulated physical mouse clicks. Evidence: test-output-051-check4-ui/ui-results.txt.
- Synthetic audio export tests passed for all four speaker/mic combinations. The live 96 kHz JDS Labs speaker pipe passed corrected sample delivery, mute and unmute. The Windows default microphone rejected WASAPI initialization with 0x80070057 before samples could be captured. No microphone sync claim is made for that endpoint. Evidence: test-output-051-check2-audio/audio-results.txt and test-failure.txt.
- Remaining confirmation: sustained Siege load with the user's selected recording devices. Synthetic timing and short soak checks do not prove multi-hour physical clock behavior, game FPS or freedom from every driver fault. Completed segments survive encoder recovery; unfinished footage and time during a full encoder restart can be omitted. Display-only recovery remains continuous.

## 0.5.2 timeline overview and installer (September 21, 2026)

- 49 trimmer interaction checks passed, including synchronized zoom labels, quarter-width viewport at 4x zoom, Fit, and panning without changing cut points or playhead. Both normal and minimum-size rendered layouts were visually inspected. The 30-pixel overview row leaves the preview full-width. Evidence: test-output-052-trim-final/trim-interaction-results.txt and trim-zoomed-small.png.
- Single EXE installer includes the self-contained Windows x64 application, FFmpeg and a small uninstaller. Embedded payload SHA-256 is verified before extraction; paths outside the install root and redirected filesystem paths are rejected. Setup checks for running Flashback and asks the user to quit it instead of terminating recordings.
- Isolated installer smoke test passed payload verification, extraction, repeated installation, real Windows shortcut creation/target verification, and manifest-based uninstall while retaining an extra user file. Evidence: test-output-052-install/app-results.txt. The test does not register an application or create desktop/Start menu shortcuts in the user's actual profile.
- Windows Settings uninstall registration and user-profile shortcut destinations are implemented but were not exercised against the user's live installation. Existing preferences and recordings outside the application folder are untouched. Installer remains unsigned; the installer UI uses the Windows .NET Framework already provided with supported Windows 10/11 systems.
- Build: publish Flashback, then build-installer.ps1. Setup source, build script and manifest are included in the source ZIP. No downloaded installer compiler or extra resident service is used.

## 0.5.3 installer file-lock recovery (September 22, 2026)

- Reproduced the reported 0.5.2 error by holding the temporary Uninstall.exe open after payload copy. The original installer threw from Directory.Delete even though installed app files were already present. Evidence: test-output-053-repro2/app-reproduction.txt.
- Setup now retries sharing/lock violations for up to four seconds, keeps temporary cleanup failures separate from installation outcome, records full error details, and preserves rollback backups if restoration fails. Concurrent setup attempts for the same target are serialized.
- Lock tests passed both transient and persistent temporary-uninstaller locks without turning a completed installation into failure. Tests also held the installed Uninstall.exe open, verified a useful error and unchanged previous DLL, then released the lock and successfully retried. Test-owned user files survived removal. Evidence: test-output-053-lock/app-results.txt; process exit 0.
- Installer smoke checks passed payload verification, extraction, repeated installation, Windows shortcut target verification, and uninstall preserving user-added files. Evidence: test-output-053-install/app-results.txt; process exit 0.
- These installer tests use workspace-only destinations and do not modify the user's live installation or recording settings. No recorder or trimmer behavior changed in 0.5.3.
