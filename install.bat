@echo off
REM install.bat — create a shortcut in the user's Startup folder
REM The shortcut will launch the .exe in this folder with the --background argument.
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

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$WshShell = New-Object -ComObject WScript.Shell; ^
  $startup = [Environment]::GetFolderPath('Startup'); ^
  $link = Join-Path $startup 'StoreAppUpdateBlocker.lnk'; ^
  $s = $WshShell.CreateShortcut($link); ^
  $s.TargetPath = '%FOUND_EXE%'; ^
  $s.Arguments = '--background'; ^
  $s.WorkingDirectory = Split-Path '%FOUND_EXE%'; ^
  $s.IconLocation = '%FOUND_EXE%'; ^
  $s.Save(); ^
  Write-Output ( 'Created shortcut at ' + $link )"

if %ERRORLEVEL% EQU 0 (
  echo Shortcut created in Startup.
) else (
  echo Failed to create shortcut.
)
pause
