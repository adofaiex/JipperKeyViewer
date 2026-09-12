# Pack the default assets into DEFLATE-compressed blobs for DLL embedding.
# Regenerate after changing any file under JipperKeyViewer\assets\ :
#   ./tools/Pack-EmbeddedAssets.ps1
# The blobs are gitignored build inputs (the ORIGINALS in assets\ are the tracked
# source of truth) — CI runs this before msbuild, and so must a fresh local clone.
#
# 把默认资源压成 DEFLATE 嵌入 DLL 的二进制块。改动 JipperKeyViewer\assets\ 下任何
# 文件后重新运行：./tools/Pack-EmbeddedAssets.ps1
# 产物不进 git（assets\ 里的原始文件才是被跟踪的事实来源）——CI 在 msbuild 前运行本脚本，
# 本地全新克隆后也需要先跑一次再编译。

param(
    [string]$SourceDir = (Join-Path $PSScriptRoot '..\JipperKeyViewer\assets'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\JipperKeyViewer\embedded')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$files = @(
    'KeyBackground.png',
    'KeyOutline.png',
    'GhostRain.png',
    'MAPLESTORY_OTF_BOLD.OTF',
    'cjkFonts-regular-normalized.otf'
)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

foreach ($name in $files) {
    $src = Join-Path $SourceDir $name
    if (-not (Test-Path $src)) { throw "Source asset not found: $src" }
    $dst = Join-Path $OutDir ($name + '.dfl')
    $bytes = [IO.File]::ReadAllBytes($src)
    $ms = New-Object IO.MemoryStream
    $ds = New-Object IO.Compression.DeflateStream($ms, [IO.Compression.CompressionLevel]::Optimal, $true)
    $ds.Write($bytes, 0, $bytes.Length)
    $ds.Close()
    [IO.File]::WriteAllBytes($dst, $ms.ToArray())
    '{0,-36} {1,12:N0} -> {2,11:N0}  ({3:P0})' -f $name, $bytes.Length, $ms.Length, ($ms.Length / $bytes.Length)
}
Write-Host "Packed $($files.Count) assets into $OutDir"
