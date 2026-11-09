@echo off
setlocal
set "ROOT=%~dp0"
cd /d "%ROOT%"
set "ROOT=%CD%"

rem Phase 13: index\mcmcp_core.db lives at the project root and is NEVER touched by a clean build.
echo [clean] This will DELETE app\ (binaries only). The index at index\mcmcp_core.db is kept.
set /p CONFIRM=Type YES to continue: 
if /i not "%CONFIRM%"=="YES" (
    echo Aborted.
    exit /b 1
)

if exist "%ROOT%\app" rmdir /s /q "%ROOT%\app"

echo [clean] app\ removed, running a fresh publish + incremental index ...
call "%ROOT%\build.bat"
exit /b %errorlevel%
