@echo off
setlocal
set "ROOT=%~dp0"
cd /d "%ROOT%"
set "ROOT=%CD%"

rem Phase 13: the index lives at %ROOT%\index\mcmcp_core.db (NOT app\index).
rem Never delete or clear index\ - it holds symbols, edges and hundreds of thousands of vectors.
echo [1/4] Preparing app and index directories ...
if not exist "%ROOT%\app" mkdir "%ROOT%\app"
if not exist "%ROOT%\index" mkdir "%ROOT%\index"

echo [2/4] Publishing Launcher ...
dotnet publish "src\MC_MCP.Launcher\MC_MCP.Launcher.csproj" -c Release -o "%ROOT%\app" --nologo
if errorlevel 1 exit /b 1

echo [3/4] Publishing McpServer ...
dotnet publish "src\MC_MCP.McpServer\MC_MCP.McpServer.csproj" -c Release -o "%ROOT%\app" --nologo
if errorlevel 1 exit /b 1

echo [4/4] Incremental SQLite index (index\mcmcp_core.db) ...
"%ROOT%\app\MC_MCP.McpServer.exe" --index "%ROOT%"
if errorlevel 1 exit /b 1

echo Done: %ROOT%\app  (index at %ROOT%\index\mcmcp_core.db is preserved)
exit /b 0
