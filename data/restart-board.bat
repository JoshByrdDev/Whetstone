@echo off
:: Started by the board through explorer.exe (POST /api/heartbeat/restart-after-drain), so the
:: restart runs outside the board's own process tree and survives stopping it.
start "Whetstone restart" /min powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0restart-board.ps1"
