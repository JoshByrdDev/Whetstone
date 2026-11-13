#!/bin/zsh -l
# Start Whetstone on a Mac: the counterpart of Start-Whetstone.bat. Double-click it in Finder, or
# `open` it, so Terminal owns the board rather than whatever started it (CLAUDE.md, "Never host the
# live instance"). The board's own restart (data/restart-board.ps1) starts it again through here.
#
# caffeinate keeps the Mac awake while the board runs: a sleep longer than the heartbeat's stall
# limit makes its watchdog kill every in-flight worker as stalled the moment the Mac wakes.
cd "${0:A:h}" || exit 1
echo "Starting Whetstone on http://localhost:5338 ..."
(sleep 6 && open http://localhost:5338) &
exec caffeinate -is dotnet run --project Whetstone.csproj
