@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Project folder has a Russian name; find it without writing non-ASCII here (cmd misreads such lines after chcp 65001).
for /d %%d in (*) do if exist "%%d\WukongBenchAuto\WukongBenchAuto.csproj" set "PROJECT=%%d\WukongBenchAuto"
dotnet run -c Release --project "%PROJECT%" -- %*
pause
