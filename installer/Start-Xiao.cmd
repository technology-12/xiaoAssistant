@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Xiao Assistant
pushd "%~dp0" >nul 2>&1
if errorlevel 1 goto path_failed
set "checkonly=0"
if /i "%~1"=="--check" set "checkonly=1"
for %%F in (app\WorkAssistant.Desktop.exe app\WorkAssistant.Desktop.exe.config app\WorkAssistant.Host.exe app\WorkAssistant.Host.exe.config app\WorkAssistant.Core.dll app\WorkAssistant.Infrastructure.dll app\WorkAssistant.Tools.dll app\WorkAssistant.OpenCode.dll app\System.Data.SQLite.dll app\x86\SQLite.Interop.dll app\x64\SQLite.Interop.dll app\app.defaults.json app\kernel.policy.json app\skills\github-docx\SKILL.md app\skills\github-pptx\SKILL.md app\skills\github-pptx-review\SKILL.md) do (
    if not exist "%%F" (
        echo [ERROR] Missing package file: %%F
        echo Extract the complete ZIP to a local folder. Do not run from inside the ZIP.
        goto incomplete
    )
)
call :detect_framework
if %netrelease% GEQ 528040 goto ready
echo [INFO] Microsoft .NET Framework 4.8 or later is required.
if "%checkonly%"=="1" goto runtime_missing
if not exist "prerequisites\NDP48-x86-x64-AllOS-ENU.exe" goto runtime_missing
echo [INFO] Opening the bundled Microsoft installer. Administrator approval may be required.
echo [INFO] Complete setup, restart Windows if requested, then run Start-Xiao.cmd again.
start "" /wait "prerequisites\NDP48-x86-x64-AllOS-ENU.exe" /norestart
set "setupcode=%errorlevel%"
if "%setupcode%"=="3010" goto reboot_required
if "%setupcode%"=="1641" goto reboot_required
if not "%setupcode%"=="0" (
    echo [ERROR] Framework setup did not complete. Exit code: %setupcode%
    goto runtime_missing
)
call :detect_framework
if %netrelease% LSS 528040 goto runtime_missing
:ready
echo [OK] Package files found. .NET Framework release: %netrelease%
if "%checkonly%"=="1" (
    echo [INFO] Basic prerequisite check only; this does not test model connectivity or OS compatibility.
    popd
    exit /b 0
)
start "" /d "%CD%\app" "%CD%\app\WorkAssistant.Desktop.exe"
if errorlevel 1 goto launch_failed
popd
exit /b 0
:detect_framework
set "netrelease=0"
for /f "tokens=3" %%R in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release /reg:32 2^>nul ^| findstr /c:"REG_DWORD"') do set /a "netrelease=%%R" >nul 2>&1
if %netrelease% GEQ 528040 exit /b 0
for /f "tokens=3" %%R in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release /reg:64 2^>nul ^| findstr /c:"REG_DWORD"') do set /a "netrelease=%%R" >nul 2>&1
exit /b 0
:runtime_missing
echo [ERROR] Install .NET Framework 4.8 from the prerequisites folder, then retry.
if not "%checkonly%"=="1" pause
popd
exit /b 2
:reboot_required
echo [INFO] Windows must be restarted before using the application.
pause
popd
exit /b 3
:incomplete
if not "%checkonly%"=="1" pause
popd
exit /b 4
:launch_failed
echo [ERROR] Windows could not start the application. Check permissions and antivirus history.
pause
popd
exit /b 5
:path_failed
echo [ERROR] Cannot access the package folder. Extract it to a local folder first.
pause
exit /b 6
