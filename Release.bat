@echo off
rem ============================================================================
rem  MacPartsFinder - make a new release (the .dmg downloads on GitHub).
rem  Double-click after your final commit in Visual Studio. It:
rem    1. checks everything is committed and you're on master
rem    2. suggests the next version number (e.g. v0.1.0 -> v0.1.1)
rem    3. after you confirm, pushes any unpushed commits and the version tag
rem    4. opens GitHub Actions, where the Mac build runs (about 1-2 minutes)
rem  The downloads then appear on the Releases page. Nothing is changed until you confirm.
rem ============================================================================
setlocal EnableExtensions EnableDelayedExpansion
title MacPartsFinder release
cd /d "%~dp0"
set "REPO_URL=https://github.com/Ian-Johnston/MacPartsFinder"

echo.
echo  MacPartsFinder release
echo  ======================
echo.

git --version >nul 2>&1
if errorlevel 1 (
    set "MSG=Git was not found. Install Git for Windows, or run this from a Git-enabled terminal."
    goto fail
)

rem ---- On master? ----
for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD') do set "BRANCH=%%b"
if /i not "%BRANCH%"=="master" (
    set "MSG=You're on branch "%BRANCH%". Switch to master in Visual Studio first."
    goto fail
)

rem ---- Everything committed? (new files Git isn't tracking are ignored) ----
set "DIRTY="
for /f "delims=" %%l in ('git status --porcelain --untracked-files^=no') do set "DIRTY=1"
if defined DIRTY (
    echo  These files have changes that aren't committed:
    echo.
    git status --short --untracked-files=no
    echo.
    set "MSG=Commit them in Visual Studio first, then run this again."
    goto fail
)

rem ---- Latest from GitHub ----
echo  Checking GitHub...
git fetch --tags origin
if errorlevel 1 (
    set "MSG=Couldn't reach GitHub. Check your internet connection and try again."
    goto fail
)
set "AHEAD=0"
for /f %%n in ('git rev-list --count origin/master..HEAD') do set "AHEAD=%%n"
set "BEHIND=0"
for /f %%n in ('git rev-list --count HEAD..origin/master') do set "BEHIND=%%n"
if not "%BEHIND%"=="0" (
    set "MSG=GitHub has %BEHIND% commit(s) you don't have. Pull them in Visual Studio first."
    goto fail
)

rem ---- Suggest the next version: last vX.Y.Z tag with Z + 1 ----
set "LAST="
for /f "delims=" %%t in ('git tag --list "v*" --sort^=-v:refname') do if not defined LAST set "LAST=%%t"
if defined LAST (
    for /f "tokens=1-3 delims=v." %%a in ("%LAST%") do (
        set /a "PATCH=%%c + 1"
        set "SUGGEST=v%%a.%%b.!PATCH!"
    )
    echo  Last release: %LAST%
) else (
    set "SUGGEST=v0.1.0"
    echo  No releases yet.
)
echo.
set "VER="
set /p "VER=Version to release [Enter for %SUGGEST%]: "
if not defined VER set "VER=%SUGGEST%"
if /i not "%VER:~0,1%"=="v" set "VER=v%VER%"

echo %VER%| findstr /r /x "v[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*" >nul
if errorlevel 1 (
    set "MSG="%VER%" isn't a version number like v0.1.1"
    goto fail
)
git rev-parse -q --verify "refs/tags/%VER%" >nul 2>&1
if not errorlevel 1 (
    set "MSG=%VER% has already been used. Run this again and pick a higher number."
    goto fail
)

rem ---- Confirm ----
for /f "delims=" %%c in ('git log -1 --format^="%%h  %%s"') do set "COMMIT=%%c"
echo.
echo  About to release %VER% from your latest commit:
echo     !COMMIT!
if not "%AHEAD%"=="0" echo  Your %AHEAD% commit(s) not yet on GitHub will be pushed first.
echo.
set "OK="
set /p "OK=Go ahead? (Y/N): "
if /i not "%OK%"=="Y" (
    set "MSG=Cancelled - nothing was changed."
    goto fail
)
echo.

rem ---- Push commits, then the tag (the tag starts the Mac build) ----
if not "%AHEAD%"=="0" (
    echo  Pushing commits...
    git push origin master
    if errorlevel 1 (
        set "MSG=Pushing your commits failed - see the message above. Nothing was released."
        goto fail
    )
)
echo  Tagging %VER%...
git tag %VER%
if errorlevel 1 (
    set "MSG=Creating the tag failed - see the message above."
    goto fail
)
echo  Pushing tag %VER%...
git push origin %VER%
if errorlevel 1 (
    git tag -d %VER% >nul
    set "MSG=Pushing the tag failed - see the message above. The tag was removed again, so you can retry."
    goto fail
)

echo.
echo  DONE. %VER% is on its way:
echo    - The Mac build is running now (about 1-2 minutes) - opening GitHub Actions to watch it.
echo    - Then the downloads appear at %REPO_URL%/releases/tag/%VER%
start "" "%REPO_URL%/actions"

rem ---- The direct download links, for forums etc. (replaced each release) ----
set "LINKS=%~dp0download links.txt"
set "DL=%REPO_URL%/releases/download/%VER%"
> "%LINKS%" echo MacPartsFinder %VER% - download links (written by Release.bat, %DATE% %TIME:~0,5%)
>>"%LINKS%" echo.
>>"%LINKS%" echo Apple Silicon Macs (M1 and later):
>>"%LINKS%" echo %DL%/MacPartsFinder-mac-arm64.dmg
>>"%LINKS%" echo.
>>"%LINKS%" echo Intel Macs:
>>"%LINKS%" echo %DL%/MacPartsFinder-mac-intel.dmg
>>"%LINKS%" echo.
>>"%LINKS%" echo Release page:
>>"%LINKS%" echo %REPO_URL%/releases/tag/%VER%
>>"%LINKS%" echo.
>>"%LINKS%" echo The links work once the GitHub build has finished (a few minutes), and for other people only once the repo is public.
if errorlevel 1 (
    echo    - Couldn't write "download links.txt" - the links are the ones above.
) else (
    echo    - The download links are saved in "download links.txt" in this folder.
)
goto end

:fail
echo.
echo  STOPPED: %MSG%

:end
echo.
pause
