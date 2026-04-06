@echo off
setlocal
REM install.bat — create a shortcut in the user's Startup folder and launch the blocker now
REM The shortcut will launch the .exe in this folder with the --background argument.
REM Launching the blocker now will replace any running copy by default.
REM Usage: double-click this file, or run: install.bat [MyApp.exe]

REM Find exe: use argument if provided, otherwise pick first *.exe in current directory
if "%~1"=="" (
  for %%F in (*.exe) do (
    set "FOUND_EXE=%%~fF"
    goto :found
  )
  echo No .exe found in "%CD%".
  echo Place the executable here or run: install.bat MyApp.exe
  pause
  exit /b 1
) else (
  if exist "%~1" (
    for %%I in ("%~1") do set "FOUND_EXE=%%~fI"
  ) else (
    echo Specified exe "%~1" not found.
    pause
    exit /b 1
  )
)

:found
echo Using "%FOUND_EXE%"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; $exe = $env:FOUND_EXE; $workingDirectory = Split-Path -LiteralPath $exe; $WshShell = New-Object -ComObject WScript.Shell; $startup = [Environment]::GetFolderPath('Startup'); $link = Join-Path $startup 'StoreAppUpdateBlocker.lnk'; $s = $WshShell.CreateShortcut($link); $s.TargetPath = $exe; $s.Arguments = '--background'; $s.WorkingDirectory = $workingDirectory; $s.IconLocation = $exe; $s.Save(); Start-Process -FilePath $exe -ArgumentList '--background' -WorkingDirectory $workingDirectory -WindowStyle Hidden -ErrorAction Stop; Write-Output ('Created shortcut at ' + $link); Write-Output 'Launched StoreAppUpdateBlocker.'"

if %ERRORLEVEL% EQU 0 (
  echo Shortcut created in Startup and blocker launched.
) else (
  echo Failed to create shortcut or launch blocker.
)
pause
