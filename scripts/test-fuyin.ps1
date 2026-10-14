param([Parameter(Mandatory=$true)][string]$Archive)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $root 'galtools-fuyin-smoke.exe'
try {
    & $compiler /nologo /target:exe "/out:$output" "/reference:$(Join-Path $root 'galtools.exe')" (Join-Path $root 'tests\SmokeFuyin.cs')
    if ($LASTEXITCODE -ne 0) { throw 'FUYINPAK 冒烟测试构建失败。' }
    & $output $Archive
    if ($LASTEXITCODE -ne 0) { throw 'FUYINPAK 冒烟测试失败。' }
} finally {
    Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
}
