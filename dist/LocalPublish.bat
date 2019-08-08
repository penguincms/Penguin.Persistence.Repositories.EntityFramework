@echo off
setlocal enabledelayedexpansion
set /a ArgCount = 0
for %%a in (%*) do (
  set /a ArgCount += 1
  set [File.!ArgCount!]=%%~a
)
for /l %%i in (1, 1, %ArgCount%) do (
   echo ![File.%%i]!
   nuget add "![File.%%i]!" -source C:\Nuget
)
pause