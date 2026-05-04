@echo off
setlocal

rem Build + run the Windows desktop exe.
rem
rem Publishes RtsEngine.Desktop as a self-contained Windows binary, copies the
rem WASM project's wwwroot tree (shaders + planet YAMLs + textures + configs)
rem and the repo-root assets/ tree (models + animations + sprites) next to it
rem so FileAssetSource can find them, then launches the exe.

set ROOT=%~dp0
set PROJECT=%ROOT%src\RtsEngine.Desktop\RtsEngine.Desktop.csproj
set OUTDIR=%ROOT%publish\desktop
set WWWROOT_SRC=%ROOT%src\RtsEngine.Wasm\wwwroot
set WWWROOT_DST=%OUTDIR%\wwwroot
set MODELS_SRC=%ROOT%assets
set MODELS_DST=%WWWROOT_DST%\assets

echo === dotnet publish (Release, win-x64) ===
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -o "%OUTDIR%" --nologo
if errorlevel 1 (
    echo Publish failed.
    exit /b 1
)

echo === copy wwwroot ===
if exist "%WWWROOT_DST%" rmdir /s /q "%WWWROOT_DST%"
xcopy /E /I /Y /Q "%WWWROOT_SRC%" "%WWWROOT_DST%" >nul
if errorlevel 1 (
    echo wwwroot copy failed.
    exit /b 1
)

rem Mirror the WASM csproj layout: repo-root /assets is surfaced under
rem wwwroot/assets at runtime. Skip silently if the assets folder is absent.
if exist "%MODELS_SRC%" (
    echo === copy assets ===
    xcopy /E /I /Y /Q "%MODELS_SRC%" "%MODELS_DST%" >nul
    if errorlevel 1 (
        echo Assets copy failed.
        exit /b 1
    )
)

echo === run ===
"%OUTDIR%\RtsEngine.Desktop.exe"
endlocal
