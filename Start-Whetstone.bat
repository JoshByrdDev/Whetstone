@echo off
title Whetstone
echo Starting Whetstone on http://localhost:5338 ...
echo.

:: Open browser after a short delay (gives server time to start)
start "" cmd /c "timeout /t 3 /nobreak >nul && start http://localhost:5338"

:: Run the server (blocks until Ctrl+C)
dotnet run --project "%~dp0Whetstone.csproj"
