@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Xiao Assistant (Windows XP)
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
set "interopdir=%~2"
if "%interopdir%"=="" set "interopdir=x86"
for %%F in (WorkAssistant.Host.exe WorkAssistant.Host.exe.config WorkAssistant.Core.dll WorkAssistant.Infrastructure.dll WorkAssistant.Tools.dll WorkAssistant.OpenCode.dll WorkAssistant.Compatibility.Xp.dll System.Data.SQLite.dll System.ValueTuple.dll Newtonsoft.Json.dll %interopdir%\SQLite.Interop.dll app.defaults.json kernel.policy.json portable.mode skills\github-docx\SKILL.md) do if not exist "%%F" goto incomplete
call :framework
if "%netfull%"=="1" goto ready
if "%checkonly%"=="1" goto runtime_missing
if not exist "prerequisites\dotNetFx40_Full_x86_x64.exe" goto runtime_missing
echo Microsoft .NET Framework 4 Full is required. Opening the bundled Microsoft installer.
echo Administrator approval may be required. Restart Windows if setup requests it.
start "" /wait "prerequisites\dotNetFx40_Full_x86_x64.exe" /norestart
set "setupcode=%errorlevel%"
if "%setupcode%"=="3010" goto reboot
if "%setupcode%"=="1641" goto reboot
if not "%setupcode%"=="0" goto runtime_missing
call :framework
if not "%netfull%"=="1" goto runtime_missing
:ready
echo [OK] Package files found. .NET Framework 4 Full: yes (release %netrelease%).
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
rem ---- .NET 4 Full detection ------------------------------------------
rem .NET 4.0 does not write the Release value (that starts with 4.5):
rem require v4\Full Install=1. A newer 4.5+ release (>=378389) also satisfies
rem the net40 runtime. Client Profile is NOT sufficient (Full required).
:framework
set "netfull=0"
set "netrelease=0"
set "install=0"
rem Prefer the default registry view: legacy XP reg.exe may not accept /reg switches.
for /f "tokens=3" %%I in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Install 2^>nul ^| findstr /c:"REG_DWORD"') do set /a "install=%%I" >nul 2>&1
if "%install%"=="1" set "netfull=1"
if "%netfull%"=="1" exit /b 0
for /f "tokens=3" %%I in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Install /reg:32 2^>nul ^| findstr /c:"REG_DWORD"') do set /a "install=%%I" >nul 2>&1
if "%install%"=="1" set "netfull=1"
for /f "tokens=3" %%R in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release /reg:32 2^>nul ^| findstr /c:"REG_DWORD"') do set /a "netrelease=%%R" >nul 2>&1
if %netrelease% GEQ 378389 set "netfull=1"
if "%netfull%"=="1" exit /b 0
for /f "tokens=3" %%I in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Install /reg:64 2^>nul ^| findstr /c:"REG_DWORD"') do set /a "install=%%I" >nul 2>&1
if "%install%"=="1" set "netfull=1"
for /f "tokens=3" %%R in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release /reg:64 2^>nul ^| findstr /c:"REG_DWORD"') do set /a "netrelease=%%R" >nul 2>&1
if %netrelease% GEQ 378389 set "netfull=1"
exit /b 0
:runtime_missing
echo [ERROR] Install .NET Framework 4 Full from prerequisites, then try again.
echo         Client Profile is not sufficient; this package requires the Full profile.
if not "%checkonly%"=="1" pause
popd
exit /b 2
:reboot
echo [INFO] Restart Windows, then open the application again.
pause
popd
exit /b 3
:incomplete
echo [ERROR] Extract the complete ZIP to a new folder.
if not "%checkonly%"=="1" pause
popd
exit /b 4
