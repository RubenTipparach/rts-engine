@echo off
setlocal

rem Build + serve the WASM web build.
rem
rem Publishes RtsEngine.Wasm in Release mode and serves the resulting wwwroot
rem (Blazor framework + game assets) over http://localhost:8080 via npx
rem http-server. Open the URL in a Chrome/Edge/Safari 18+ build for WebGPU.

set ROOT=%~dp0
set PROJECT=%ROOT%src\RtsEngine.Wasm\RtsEngine.Wasm.csproj
set OUTDIR=%ROOT%publish\web
set PORT=8080

echo === dotnet publish (Release, WASM) ===
dotnet publish "%PROJECT%" -c Release -o "%OUTDIR%" --nologo
if errorlevel 1 (
    echo Publish failed.
    exit /b 1
)

set WWWROOT=%OUTDIR%\wwwroot

if not exist "%WWWROOT%\index.html" (
    echo Expected %WWWROOT%\index.html after publish, not found.
    exit /b 1
)

where npx >nul 2>&1
if errorlevel 1 (
    echo npx not found on PATH. Install Node.js ^(https://nodejs.org^) and retry.
    exit /b 1
)

echo === serving %WWWROOT% on http://localhost:%PORT% ===
echo press Ctrl+C to stop.
rem -c-1 disables caching so a re-publish + browser refresh always reflects the new build.
rem --cors lets us iterate without browser security warnings if anything fetches across origins.
npx --yes http-server "%WWWROOT%" -p %PORT% -c-1 --cors
endlocal
