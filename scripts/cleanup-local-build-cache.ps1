# Removes only temporary compiler downloads and local test/build outputs.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$targets = @(
    (Join-Path $root '.build-tools'),
    (Join-Path $root 'vendor\GARbro-Mod\packages'),
    (Join-Path $root 'vendor\GARbro-Mod\bin'),
    (Join-Path $root 'vendor\GARbro-Mod\ArcFormats\obj'),
    (Join-Path $root 'vendor\GARbro-Mod\GameRes\obj'),
    (Join-Path $root 'vendor\GARbro-Mod\Net20\obj'),
    (Join-Path $root 'UiSmoke.exe'),
    (Join-Path $root 'WpfSmoke.exe'),
    (Join-Path $root 'WpfSmoke.exe.config'),
    (Join-Path $root 'tests\Probe.exe')
)
foreach ($target in $targets)
{
    if (!(Test-Path -LiteralPath $target)) { continue }
    $absolute = (Resolve-Path -LiteralPath $target).Path
    if (!$absolute.StartsWith($root + '\', [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "拒绝清理项目外路径：$absolute"
    }
    Remove-Item -LiteralPath $absolute -Recurse -Force
    Write-Host "已清理：$absolute"
}
