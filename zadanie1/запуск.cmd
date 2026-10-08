@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem 
for /d %%d in (*) do if exist "%%d\WukongBenchAuto\WukongBenchAuto.csproj" set "PROJECT=%%d\WukongBenchAuto"
dotnet run -c Release --project "%PROJECT%" -- %*
pause
