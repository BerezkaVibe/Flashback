@echo off
rem Runs the FFmpeg preview player's tests and opens the results. Double-click it (Flashback can stay open).
rem The tests use their own data folder, so your settings and clips aren't touched.
setlocal
cd /d "%~dp0"
rem Beside Flashback.exe, or in the repo folder: then the build in artifacts is used.
if not exist Flashback.exe for /d %%D in ("artifacts\Flashback-*") do if exist "%%D\Flashback.exe" cd /d "%%D"
if not exist Flashback.exe (
  echo Flashback.exe wasn't found. Build first:  .\build.ps1 -FfmpegPath ... -FfmpegLibs ...
  pause
  exit /b 1
)
if not exist ffmpeg\avcodec-*.dll (
  echo This build has no FFmpeg player libraries. Build again with -FfmpegLibs pointing at an FFmpeg LGPL shared build's bin folder.
  pause
  exit /b 1
)
set "OUT=%LOCALAPPDATA%\Flashback-tests"
if not exist "%OUT%" mkdir "%OUT%"
del /q "%OUT%\ffmpeg-engine-report.txt" "%OUT%\ffmpeg-engine-results.json" "%OUT%\player-compare.txt" "%OUT%\test-failure.txt" "%OUT%\results.txt" 2>nul

echo Testing the FFmpeg engine (about a minute)...
start "" /wait Flashback.exe --data-dir "%OUT%" --ffmpeg-engine-test
set ENGINE=%ERRORLEVEL%
if exist "%OUT%\test-failure.txt" (
  copy /y "%OUT%\test-failure.txt" "%OUT%\engine-failure.txt" >nul
  del /q "%OUT%\test-failure.txt"
)

echo Comparing the two players (a few minutes; windows open and close by themselves, leave the mouse alone)...
start "" /wait Flashback.exe --data-dir "%OUT%" --player-compare-test
set COMPARE=%ERRORLEVEL%

> "%OUT%\results.txt" (
  echo Flashback FFmpeg player tests, %DATE% %TIME%
  echo.
  if "%ENGINE%"=="0" (echo ENGINE TEST: PASSED) else (echo ENGINE TEST: FAILED)
  if exist "%OUT%\ffmpeg-engine-report.txt" type "%OUT%\ffmpeg-engine-report.txt"
  if exist "%OUT%\engine-failure.txt" (echo. & echo --- What failed --- & type "%OUT%\engine-failure.txt")
  echo.
  if "%COMPARE%"=="0" (echo PLAYER COMPARISON: FINISHED) else (echo PLAYER COMPARISON: FAILED)
  if exist "%OUT%\player-compare.txt" type "%OUT%\player-compare.txt"
  if exist "%OUT%\test-failure.txt" (echo. & echo --- What failed --- & type "%OUT%\test-failure.txt")
)
start "" notepad "%OUT%\results.txt"
