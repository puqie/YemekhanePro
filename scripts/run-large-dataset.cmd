@echo off
setlocal
set "YEMEKHANE_LARGE_DATASET=1"
dotnet test "%~dp0..\tests\Yemekhane.PerformanceTests\Yemekhane.PerformanceTests.csproj" --configuration Release --filter "Category=LargeDataset" --logger "console;verbosity=detailed"
exit /b %ERRORLEVEL%
