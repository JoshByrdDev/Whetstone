@echo off
title Update the Whetstone runner
:: On a worker host: stop the runner, move this checkout to the board host's commit, start it again.
:: See data\update-runner.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0data\update-runner.ps1" %*
echo.
pause
