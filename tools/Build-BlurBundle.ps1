# Build the key-background blur shader AssetBundle with the local Unity editor, then copy the
# bundle into JipperKeyViewer\assets\ (the packed source of truth that Pack-EmbeddedAssets picks up).
# 用本机 Unity 编辑器构建按键背景模糊的 shader AssetBundle，然后把 bundle 拷进
# JipperKeyViewer\assets\（Pack-EmbeddedAssets 打包的事实来源）。
#
# Rebuild whenever tools\blur-shader\KeyViewerBlur.shader changes:
#   ./tools/Build-BlurBundle.ps1
# 改动 tools\blur-shader\KeyViewerBlur.shader 后重新运行：./tools/Build-BlurBundle.ps1

$ErrorActionPreference = 'Stop'

$unity = 'D:\Program Files\Unity\6000.3.16f1\Editor\Unity.exe'
if (-not (Test-Path $unity)) { throw "Unity editor not found: $unity" }

$repoRoot = Join-Path $PSScriptRoot '..'
$srcShader = Join-Path $PSScriptRoot 'blur-shader\KeyViewerBlur.shader'
$srcBuilder = Join-Path $PSScriptRoot 'blur-shader\JkvBundleBuilder.cs'
$buildProj = Join-Path $env:TEMP 'jkv-blur-bundle-build'
$outBundle = Join-Path $repoRoot 'JipperKeyViewer\assets\KeyViewerBlur.bundle'

New-Item -ItemType Directory -Force -Path (Join-Path $buildProj 'Assets\Shaders') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $buildProj 'Assets\Editor') | Out-Null
# Unity refuses to open a project without ProjectSettings\ProjectVersion.txt, and that file
# cannot be written before its directory exists. / Unity 没有 ProjectSettings\ProjectVersion.txt
# 就打不开工程，而该文件在目录存在之前写不进去。
New-Item -ItemType Directory -Force -Path (Join-Path $buildProj 'ProjectSettings') | Out-Null
# Fresh shader/builder each run; a stale project keeps its Library cache which is fine.
# 每次运行都覆盖 shader/构建脚本；旧工程保留的 Library 缓存无妨。
Copy-Item $srcShader (Join-Path $buildProj 'Assets\Shaders\KeyViewerBlur.shader') -Force
Copy-Item $srcBuilder (Join-Path $buildProj 'Assets\Editor\JkvBundleBuilder.cs') -Force
# The shader must be visible to the AssetDatabase BEFORE Unity launches; print the check so a
# silent copy failure is diagnosable instead of surfacing as "No AssetBundle has been set".
# / shader 必须在 Unity 启动前就位；打印检查结果，避免静默复制失败被误报成「未设置 AssetBundle」。
Write-Host ("shader staged: " + (Test-Path (Join-Path $buildProj 'Assets\Shaders\KeyViewerBlur.shader')))
Set-Content -Path (Join-Path $buildProj 'ProjectSettings\ProjectVersion.txt') -Value "m_EditorVersion: 6000.3.16f1" -Encoding ascii

$log = Join-Path $env:TEMP 'jkv-blur-bundle-build.log'
$proc = Start-Process -FilePath $unity -ArgumentList @(
    '-batchmode', '-quit', '-nographics',
    '-projectPath', $buildProj,
    '-executeMethod', 'JkvBundleBuilder.Build',
    '-logFile', $log
) -PassThru
$proc | Wait-Process -Timeout 600 -ErrorAction SilentlyContinue
if (-not $proc.HasExited) { throw "Unity build timed out after 600s; log: $log" }
if ($proc.ExitCode -ne 0) { Get-Content $log -Tail 40 | Write-Host; throw "Unity build failed with exit code $($proc.ExitCode); log: $log" }

# NOT under Temp\: Unity wipes <project>\Temp on quit. / 不能放在 Temp\ 下：Unity 退出时
# 会清空 <project>\Temp。
$built = Join-Path $buildProj 'jkv-bundle-out\keyviewerblur.bundle'
if (-not (Test-Path $built)) { Get-Content $log -Tail 40 | Write-Host; throw "bundle not produced: $built" }
Copy-Item $built $outBundle -Force
'{0:N0} bytes -> {1}' -f (Get-Item $outBundle).Length, $outBundle
Write-Host "Done. Re-run tools/Pack-EmbeddedAssets.ps1 before building the DLL."
