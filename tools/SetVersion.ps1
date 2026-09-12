# Set the mod version in every place it is hardcoded:
#   - the three AssemblyInfo.cs files (AssemblyVersion / AssemblyFileVersion)
#   - the [MelonInfo] version string in MelonEntry.cs
#   - JipperKeyViewer\Info.json ("Version")
#   - Repository.json ("Version" fields + releases/download/<ver>/ URLs)
#
# Usage:  ./tools/SetVersion.ps1 -Version 1.7.1
# The release workflow runs this on the build runner before msbuild, so release artifacts always
# carry the dispatched/tag version even though the repo files are bumped manually (the projects
# keep the hand-written AssemblyInfo.cs as the single version source with GenerateAssemblyInfo=
# false, so msbuild's /p:Version remains ignored and the sources are patched instead).
#
# 一键修改所有硬编码版本号：3 个 AssemblyInfo、MelonEntry 的 [MelonInfo] 版本串、
# Info.json、Repository.json（版本字段 + 下载 URL）。发版工作流在 msbuild 之前于构建机上运行
# 本脚本（各工程以 GenerateAssemblyInfo=false 保留手写 AssemblyInfo 作为唯一版本来源，
# /p:Version 依然无效，故直接改源码）；本地发版前也可手动运行。

param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$Version = $Version.TrimStart('v', 'V')
if ($Version -notmatch '^\d+(\.\d+)+$') { throw "Invalid version: '$Version' (expected e.g. 1.7.1)" }
# Assembly versions allow exactly 4 numeric parts — pad shorter inputs to 4 and reject 5+.
# 程序集版本号只允许 4 段数字——不足补 0，超过 4 段报错。
$parts = @($Version.Split('.'))
if ($parts.Count -gt 4) { throw "Version has more than 4 parts: $Version" }
$assemblyVersion = (@($parts) + @('0') * (4 - $parts.Count)) -join '.'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Update-File([string]$relPath, [scriptblock]$edit) {
    $path = Join-Path $repoRoot $relPath
    if (-not (Test-Path $path)) { throw "File not found: $path" }
    # Preserve each file's own BOM state and normalize the trailing newline, so repeated runs are
    # byte-stable and pwsh (BOM-less UTF8 by default) and Windows PowerShell 5.1 (BOM) behave
    # identically. / 按各文件自身的 BOM 状态写回并规范结尾换行——重复运行字节级稳定，且 pwsh
    #（默认无 BOM）与 Windows PowerShell 5.1（带 BOM）行为一致。
    $bytes = [IO.File]::ReadAllBytes($path)
    $hadBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [IO.File]::ReadAllText($path).TrimEnd("`r", "`n")
    $new = & $edit $text
    if ($new -ne $text) {
        $enc = New-Object System.Text.UTF8Encoding($hadBom)
        [IO.File]::WriteAllText($path, $new + "`r`n", $enc)
        Write-Host "updated $relPath"
    } else {
        Write-Host "no change in $relPath"
    }
}

$assemblyInfos = @(
    'JipperKeyViewer\Properties\AssemblyInfo.cs',
    'JipperKeyViewer.Loader.UMM\Properties\AssemblyInfo.cs',
    'JipperKeyViewer.Loader.Melon\Properties\AssemblyInfo.cs'
)
foreach ($rel in $assemblyInfos) {
    Update-File $rel { param($t)
        $t -replace 'AssemblyVersion\("[^"]+"\)', ('AssemblyVersion("' + $assemblyVersion + '")') `
           -replace 'AssemblyFileVersion\("[^"]+"\)', ('AssemblyFileVersion("' + $assemblyVersion + '")')
    }
}

Update-File 'JipperKeyViewer.Loader.Melon\MelonEntry.cs' { param($t)
    $t -replace '("Jipper Key Viewer", ")[\d.]+(")', ('${1}' + $Version + '${2}')
}

Update-File 'JipperKeyViewer\Info.json' { param($t)
    $t -replace '("Version"\s*:\s*")[^"]+(")', ('${1}' + $Version + '${2}')
}

Update-File 'Repository.json' { param($t)
    $t -replace '("Version"\s*:\s*")[^"]+(")', ('${1}' + $Version + '${2}') `
       -replace '(releases/download/)[^/]+(/)', ('${1}' + $Version + '${2}')
}

Write-Host "Version set to $Version everywhere."
