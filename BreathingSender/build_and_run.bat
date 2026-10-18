@echo off
REM Builds and runs the Presage breathing sender for the StarGame scene.
REM Just double-click this file (or run it from any Command Prompt).
REM The first time, it asks for your SDK folder and API key and saves them
REM in local_config.bat (git-ignored, so your key is never pushed).
REM To change them later, delete local_config.bat and run this again.
setlocal
cd /d "%~dp0"

if not exist local_config.bat goto :ask
call local_config.bat
if not exist "%SMARTSPECTRA_SDK_PATH%\bin\smartspectra.dll" goto :ask
if "%SMARTSPECTRA_API_KEY%"=="" goto :ask
if "%SMARTSPECTRA_API_KEY%"=="PASTE_YOUR_KEY_HERE" goto :ask
goto :configured

:ask
echo.
echo ===== First-time setup =====
echo.
echo 1) SmartSpectra SDK folder: the folder you unzipped the Presage SDK into.
echo    It contains a "bin" folder with smartspectra.dll inside.
echo    Tip: in File Explorer, open that folder, click the address bar, copy, then right-click here to paste.
:askpath
set "SMARTSPECTRA_SDK_PATH="
set /p "SMARTSPECTRA_SDK_PATH=   SDK folder: "
if not defined SMARTSPECTRA_SDK_PATH goto :askpath
set "SMARTSPECTRA_SDK_PATH=%SMARTSPECTRA_SDK_PATH:"=%"
if exist "%SMARTSPECTRA_SDK_PATH%\bin\smartspectra.dll" goto :gotpath
echo    Couldn't find bin\smartspectra.dll in that folder. Try again.
goto :askpath
:gotpath
echo.
echo 2) Presage API key from https://physiology.presagetech.com  ^(right-click to paste^)
:askkey
set "SMARTSPECTRA_API_KEY="
set /p "SMARTSPECTRA_API_KEY=   API key: "
if not defined SMARTSPECTRA_API_KEY goto :askkey
> local_config.bat echo @echo off
>> local_config.bat echo set "SMARTSPECTRA_SDK_PATH=%SMARTSPECTRA_SDK_PATH%"
>> local_config.bat echo set "SMARTSPECTRA_API_KEY=%SMARTSPECTRA_API_KEY%"
echo.
echo Saved. You won't be asked again.
echo.

:configured
REM Find the Visual Studio C++ compiler automatically if it isn't already set up.
where cl >nul 2>nul
if not errorlevel 1 goto :havecl
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto :nocl
for /f "usebackq delims=" %%i in (`call "%VSWHERE%" -latest -products * -property installationPath`) do set "VSINSTALL=%%i"
if not exist "%VSINSTALL%\VC\Auxiliary\Build\vcvars64.bat" goto :nocl
echo Setting up the Visual Studio compiler...
call "%VSINSTALL%\VC\Auxiliary\Build\vcvars64.bat" >nul
where cl >nul 2>nul
if not errorlevel 1 goto :havecl
:nocl
echo Could not find the Visual Studio C++ compiler.
echo Open "x64 Native Tools Command Prompt for VS" from the Start menu and run this file from there.
pause
exit /b 1
:havecl

if not exist build\CMakeCache.txt (
  cmake -S . -B build -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release || (pause & exit /b 1)
)
cmake --build build || (pause & exit /b 1)

set "PATH=%SMARTSPECTRA_SDK_PATH%\bin;%PATH%"
echo.
echo Starting sender. Press Play in Unity (StarGame scene). Ctrl+C to stop.
echo.
build\breathing_udp_sender.exe %*
pause
