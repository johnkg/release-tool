@echo off
rem Starts the Release Tool from its own folder.
rem
rem The working directory matters: it decides where the app looks for wwwroot,
rem App_Data and logs. Launching the .exe from somewhere else serves a blank
rem page and writes settings to the wrong place, so this always cd's first.

cd /d "%~dp0"

echo.
echo   Release Tool
echo   ------------
echo   Open http://localhost:5000 in your browser.
echo   Leave this window open while you use it; press Ctrl+C to stop.
echo.

ReleaseTool.Api.exe %*
