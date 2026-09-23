# Flashback development

## Release history

- 0.5.2 is the original single-EXE installer and timeline overview baseline. Preserve its source as a separate release commit. Its installer has a known cleanup-lock bug.
- 0.5.3 changes installer reliability. The recorder and trimmer behavior remain the same as 0.5.2.
- 0.5.4 fixes trimmer preview A/V sync and slims the package.

## Source layout

- `Flashback/`: WPF recorder, trimmer, settings and diagnostics.
- `installer/`: single-EXE installer, uninstaller and lock-reproduction helper.
- `licenses/`: bundled dependency notices.
- `build.ps1`: publishes the self-contained app and portable ZIP.
- `build-installer.ps1`: packages the published app as one installer EXE.

Build on Windows x64 with the .NET 8 SDK and a compatible FFmpeg build. Run `./build.ps1 -FfmpegPath <path-to-ffmpeg.exe>`, then `./build-installer.ps1`. Set `DOTNET_CLI_HOME` to a writable local folder if needed. Compiler and runtime versions are recorded in the project file. Update the application project, installer assembly/version constants, setup manifest and build-script defaults together for each release.

Keep generated binaries, recordings, local preferences, downloaded reference projects, caches and test outputs out of Git. Publish installers and source ZIPs as release assets, not tracked source files. No signing key or credential belongs in the repository.

## Checks

Run app diagnostics with an isolated `--data-dir`, for example `Flashback.exe --trim-interaction-test --data-dir <new-test-folder>`. Installer checks use `Setup.exe --smoke-test <new-folder>/app` and `Setup.exe --lock-test <new-folder>/app`; they do not register an installation in the user's profile. See `VALIDATION.md` for test coverage and remaining hardware limitations.
