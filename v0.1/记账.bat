@echo off
rem ---------------------------------------------------------------
rem Launcher for the bookkeeping app. ASCII only, on purpose:
rem cmd.exe reads .bat content with the system code page, so any
rem Chinese character in this file would be garbled (the app folder
rem and entry file have Chinese names, this launcher must not).
rem That is why this file only calls run.py, a pure ASCII name.
rem ---------------------------------------------------------------
setlocal
cd /d "%~dp0"

set "PYW="
set "PY="
for %%I in (pythonw.exe) do if not defined PYW set "PYW=%%~$PATH:I"
for %%I in (python.exe) do if not defined PY set "PY=%%~$PATH:I"

if not exist "run.py" goto :noentry
if defined PY goto :have_py
if defined PYW goto :gui_only
goto :nopython

:have_py
if not "%~1"=="" goto :cli

rem ---- GUI mode: pre-flight check, otherwise pythonw fails silently ----
"%PY%" -c "import ledger.ui" >nul 2>nul
if errorlevel 1 goto :precheckfail

:gui_only
if not defined PYW goto :console
start "" "%PYW%" "run.py"
exit /b 0

:console
"%PY%" "run.py"
exit /b %errorlevel%

:cli
"%PY%" "run.py" %*
exit /b %errorlevel%

:precheckfail
echo.
echo [ERROR] Pre-flight check failed, the app cannot start. Details:
echo.
"%PY%" -c "import ledger.ui"
echo.
pause
exit /b 1

:noentry
echo.
echo [ERROR] run.py was not found next to this script.
echo         Keep this launcher, run.py and the ledger folder together.
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
