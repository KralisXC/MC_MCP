#Requires -Version 5.1
<#
.SYNOPSIS
    为另一台电脑准备“全量构建”工作区，并打包成 MC_MCP_FullBuild.zip（U 盘拷贝用）。

.DESCRIPTION
    只携带运行所需内容：app / LLM / code / mods / build 脚本 / README / 空 index。
    绝不携带 src、docs、旧数据库、bin/obj、.git、.screenshots。
    对方解压后直接运行 app\MC_MCP.Launcher.exe 即可全量重建，无需重新编译。

.PARAMETER OutputZip
    输出压缩包路径，默认 <工程根>\MC_MCP_FullBuild.zip。

.PARAMETER StagingDir
    临时汇总目录，默认 <工程根>\_transfer_temp，打包完成后自动删除。

.PARAMETER KeepStaging
    保留临时目录（排查复制问题时使用）。
#>
[CmdletBinding()]
param(
    [string]$OutputZip,
    [string]$StagingDir,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'

$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
if ([string]::IsNullOrWhiteSpace($OutputZip)) { $OutputZip = Join-Path $root 'MC_MCP_FullBuild.zip' }
if ([string]::IsNullOrWhiteSpace($StagingDir)) { $StagingDir = Join-Path $root '_transfer_temp' }

$requiredDirs = @('app', 'LLM', 'code', 'mods')
$requiredFiles = @('build.bat', 'build_clean.bat', 'README.md', 'DEVELOPMENT.md')
$forbiddenDirs = @('src', 'docs', 'bin', 'obj', '.git', '.screenshots')
$forbiddenFiles = @(
    'test.db', 'test.db-wal', 'test.db-shm',
    'mcmcp_core.db', 'mcmcp_core.db-wal', 'mcmcp_core.db-shm'
)

function Write-Step {
    param([string]$Message)
    Write-Output "[pack] $Message"
}

foreach ($name in $requiredDirs) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $name) -PathType Container)) {
        throw "缺少必需目录：$name（工程根：$root）"
    }
}

foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf)) {
        throw "缺少必需文件：$name（工程根：$root）"
    }
}

Write-Step "工程根：$root"
Write-Step "临时目录：$StagingDir"
Write-Step "输出压缩包：$OutputZip"

if (Test-Path -LiteralPath $StagingDir) {
    Remove-Item -LiteralPath $StagingDir -Recurse -Force
}

New-Item -ItemType Directory -Path $StagingDir | Out-Null

if (Test-Path -LiteralPath $OutputZip) {
    Remove-Item -LiteralPath $OutputZip -Force
}

function Copy-Tree {
    param([string]$Source, [string]$Destination)

    # /E 复制子目录（含空目录）；/XD 与 /XF 负责剔除开发产物与旧数据库。
    $arguments = @(
        $Source, $Destination,
        '/E', '/R:2', '/W:2',
        '/NFL', '/NDL', '/NJH', '/NJS', '/NP',
        '/XD', 'bin', 'obj', '.git', '.screenshots',
        '/XF', 'test.db', 'test.db-wal', 'test.db-shm',
        'mcmcp_core.db', 'mcmcp_core.db-wal', 'mcmcp_core.db-shm'
    )
    & robocopy @arguments | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy 失败（exit=$LASTEXITCODE）：$Source → $Destination"
    }
}

foreach ($name in $requiredDirs) {
    Write-Step "复制 $name……"
    Copy-Tree (Join-Path $root $name) (Join-Path $StagingDir $name)
}

foreach ($name in $requiredFiles) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $StagingDir $name) -Force
}

# 对方在那边全新生成 mcmcp_core.db，因此这里只创建空目录。
New-Item -ItemType Directory -Path (Join-Path $StagingDir 'index') | Out-Null

foreach ($name in $forbiddenDirs) {
    if (Test-Path -LiteralPath (Join-Path $StagingDir $name)) {
        throw "排除校验失败：临时目录中仍存在 $name"
    }
}

$forbiddenFound = Get-ChildItem -LiteralPath $StagingDir -Recurse -Force -File |
    Where-Object { $forbiddenFiles -contains $_.Name }
if ($forbiddenFound) {
    throw "排除校验失败：发现旧数据库文件 $($forbiddenFound[0].FullName)"
}

if (@(Get-ChildItem -LiteralPath (Join-Path $StagingDir 'index') -Recurse -Force).Count -gt 0) {
    throw "排除校验失败：index 目录必须为空"
}

$stagingFiles = @(Get-ChildItem -LiteralPath $StagingDir -Recurse -Force -File)
$stagingBytes = ($stagingFiles | Measure-Object -Property Length -Sum).Sum
Write-Step ("临时内容：{0:N0} 个文件，{1:N1} MB" -f $stagingFiles.Count, ($stagingBytes / 1MB))

Write-Step "压缩中（Optimal）……"
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $StagingDir,
    $OutputZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

# ZipFile 只枚举文件，空目录不会自动入包；显式补一个 index/ 目录条目。
$archive = [System.IO.Compression.ZipFile]::Open($OutputZip, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    if (-not $archive.GetEntry('index/')) {
        $null = $archive.CreateEntry('index/')
    }
}
finally {
    $archive.Dispose()
}

$zip = Get-Item -LiteralPath $OutputZip
Write-Step ("打包完成：{0:N1} MB" -f ($zip.Length / 1MB))
Write-Output "ZIP_PATH=$($zip.FullName)"
Write-Output "ZIP_BYTES=$($zip.Length)"
Write-Output "ZIP_FILES=$($stagingFiles.Count)"

if (-not $KeepStaging) {
    Remove-Item -LiteralPath $StagingDir -Recurse -Force
    Write-Step "已删除临时目录。"
}
