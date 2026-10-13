@echo off
rem ---------------------------------------------------------------
rem Build a standalone release that runs on PCs without Python.
rem
rem ASCII only, on purpose: cmd.exe reads .bat content with the
rem system code page, so any non-ASCII byte here (file names too)
rem would be garbled and paths would not resolve. That is why the
rem build helper folder is named "LedgerBook\build" (ASCII) while
rem the produced app folder / zip keep their Chinese names - those
rem are created by Python (UTF-8) and are fine.
rem
rem Everything produced lands in the LedgerBook subfolder:
rem   LedgerBook\<app>\          the program folder (exe + runtime)
rem   LedgerBook\<app>-*.zip     the package to copy to another PC
rem   LedgerBook\build\          build tool + work files (deletable)
rem   LedgerBook\backups-folder  source snapshots for rollback
rem build.spec pins distpath/workpath there, so no path options here.
rem ---------------------------------------------------------------
setlocal
cd /d "%~dp0"

set "PY="
for %%I in (python.exe) do if not defined PY set "PY=%%~$PATH:I"

if not defined PY goto :nopython
if not exist "build.spec" goto :nospec

rem Prefer the local copy of PyInstaller (LedgerBook\build\.tools) so the
rem system installation is left untouched.
set "TOOLS=%CD%\LedgerBook\build\.tools"
if not exist "%TOOLS%\PyInstaller" (
    echo === Local PyInstaller not found, downloading it ===
    "%PY%" "LedgerBook\build\download_tools.py"
    if errorlevel 1 goto :fail
)
set "PYTHONPATH=%TOOLS%"

echo === Checking PyInstaller ===
"%PY%" -c "import PyInstaller" >nul 2>nul
if errorlevel 1 (
    echo PyInstaller is missing, installing it now ...
    "%PY%" -m pip install --upgrade pyinstaller
    if errorlevel 1 goto :fail
)

echo === Building ===
rem PyInstaller creates a "build" (and "dist") folder in the CURRENT
rem directory before it even reads the spec, and it refuses --workpath /
rem --distpath when a spec file is given. So build from inside the
rem disposable build folder: the project root stays clean, and build.spec
rem still pins the real output to LedgerBook\.
pushd "LedgerBook\build"
"%PY%" -m PyInstaller --noconfirm --clean "%CD%\..\..\build.spec"
if errorlevel 1 (
    popd
    goto :fail
)
popd

echo.
echo === Packaging portable release ===
"%PY%" "LedgerBook\build\release.py"
if errorlevel 1 goto :fail

echo.
echo === Done. Everything new is inside LedgerBook: ===
dir /b "LedgerBook"
echo.
echo Copy the zip (or the app folder) to the other PC and run the exe inside.
echo Data folders are created next to that exe.
pause
exit /b 0

:fail
echo.
echo [ERROR] Build failed. Please copy the messages above.
echo.
pause
exit /b 1

:nopython
echo.
echo [ERROR] Python was not found in PATH.
echo         Install Python 3.10+ and enable "Add python.exe to PATH".
echo         https://www.python.org/downloads/
echo.
pause
exit /b 1

:nospec
echo.
echo [ERROR] build.spec was not found next to this script.
echo.
pause
exit /b 1
