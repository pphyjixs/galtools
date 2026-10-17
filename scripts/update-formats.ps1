$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$runtime = Join-Path $root 'runtime\garbro-mod'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $root 'src\Galtools.FormatData\Program.cs'
$utility = Join-Path $runtime 'Galtools.FormatData.exe'

& $compiler /nologo /target:exe /optimize+ "/out:$utility" "/reference:$(Join-Path $runtime 'GameRes.dll')" "/reference:$(Join-Path $runtime 'ArcFormats.dll')" $source
if ($LASTEXITCODE -ne 0) { throw 'Formats.dat 更新工具构建失败。' }
try {
    & $utility apply-all
    if ($LASTEXITCODE -ne 0) { throw 'Formats.dat 更新失败。' }
} finally {
    Remove-Item -LiteralPath $utility -Force -ErrorAction SilentlyContinue
}
