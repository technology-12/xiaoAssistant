@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Xiao Assistant
pushd "%~dp0" >nul 2>&1
if errorlevel 1 exit /b 6
set "checkonly=0"
if /i "%~1"=="--check" set "checkonly=1"
set "mainexe="
set "maincount=0"
for %%F in (*.exe) do if /i not "%%~nxF"=="WorkAssistant.Host.exe" (
    set "mainexe=%%~nxF"
    set /a maincount+=1 >nul
)
if not "%maincount%"=="1" goto incomplete
if not exist "%mainexe%.config" goto incomplete
for %%F in (WorkAssistant.Host.exe WorkAssistant.Host.exe.config WorkAssistant.Core.dll WorkAssistant.Infrastructure.dll WorkAssistant.Tools.dll WorkAssistant.OpenCode.dll System.Data.SQLite.dll x86\SQLite.Interop.dll x64\SQLite.Interop.dll app.defaults.json kernel.policy.json portable.mode skills\github-docx\SKILL.md skills\github-pptx\SKILL.md skills\github-pptx-review\SKILL.md) do if not exist "%%F" goto incomplete
call :framework
if %netrelease% GEQ 528040 goto ready
if "%checkonly%"=="1" goto runtime_missing
if not exist "prerequisites\NDP48-x86-x64-AllOS-ENU.exe" goto runtime_missing
echo Microsoft .NET Framework 4.8 is required. Opening the bundled Microsoft installer.
echo Administrator approval may be required. Restart Windows if setup requests it.
start "" /wait "prerequisites\NDP48-x86-x64-AllOS-ENU.exe" /norestart
set "setupcode=%errorlevel%"
if "%setupcode%"=="3010" goto reboot
if "%setupcode%"=="1641" goto reboot
if not "%setupcode%"=="0" goto runtime_missing
call :framework
if %netrelease% LSS 528040 goto runtime_missing
:ready
echo [OK] Package files found. .NET Framework release: %netrelease%
if "%checkonly%"=="1" (
    popd
    exit /b 0
)
start "" /d "%CD%" "%CD%\%mainexe%"
if errorlevel 1 (
    echo [ERROR] Windows could not start the application.
    pause
    popd
    exit /b 5
)
popd
exit /b 0
:framework
set "netrelease=0"
for /f "tokens=3" %%R in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release /reg:32 2^>nul ^| findstr /c:"REG_DWORD"') do set /a "netrelease=%%R" >nul 2>&1
if %netrelease% GEQ 528040 exit /b 0
for /f "tokens=3" %%R in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release /reg:64 2^>nul ^| findstr /c:"REG_DWORD"') do set /a "netrelease=%%R" >nul 2>&1
exit /b 0
:runtime_missing
echo [ERROR] Install .NET Framework 4.8 from prerequisites, then try again.
if not "%checkonly%"=="1" pause
popd
exit /b 2
:reboot
echo [INFO] Restart Windows, then open the application again.
pause
popd
exit /b 3
:incomplete
echo [ERROR] Package is incomplete or contains extra executable files.
echo Extract the entire ZIP to a clean local folder. Do not copy just the EXE.
if not "%checkonly%"=="1" pause
popd
exit /b 4
