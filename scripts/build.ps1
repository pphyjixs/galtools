param([switch]$Package)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$wpf = Join-Path $framework 'WPF'
$runtime = Join-Path $root 'runtime\garbro-mod'
$netstandard = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\netstandard') -Recurse -Filter netstandard.dll -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
$bridgeSource = Join-Path $root 'src\Galtools.GarbroModBridge\Program.cs'
$desktopSource = Join-Path $root 'src\Galtools.Desktop'

if (!(Test-Path -LiteralPath $compiler)) { throw '未找到本机 .NET Framework C# 编译器。' }
if (!(Test-Path -LiteralPath (Join-Path $runtime 'GameRes.dll'))) { throw '未找到项目内 GARbro-Mod 运行库。' }
if (!(Test-Path -LiteralPath (Join-Path $runtime 'GameData\Formats.dat'))) { throw '未找到项目内 Formats.dat。' }
if (!$netstandard) { throw '本机 .NET Framework 缺少 netstandard 2.0 兼容程序集。' }

& $compiler /nologo /target:exe /optimize+ "/out:$(Join-Path $runtime 'Galtools.GarbroModBridge.exe')" "/reference:$(Join-Path $runtime 'GameRes.dll')" "/reference:$(Join-Path $runtime 'ArcFormats.dll')" "/reference:$(Join-Path $runtime 'NAudio.Core.dll')" "/reference:$(Join-Path $runtime 'NAudio.Wasapi.dll')" "/reference:$netstandard" "/reference:$(Join-Path $wpf 'PresentationCore.dll')" "/reference:$(Join-Path $wpf 'WindowsBase.dll')" "/reference:$(Join-Path $wpf 'PresentationFramework.dll')" /reference:System.Xaml.dll $bridgeSource (Join-Path $root 'src\Galtools.GarbroModBridge\EntryInspector.cs')
if ($LASTEXITCODE -ne 0) { throw 'GARbro-Mod 桥接程序构建失败。' }
Copy-Item -LiteralPath (Join-Path $runtime 'GameRes.dll.config') -Destination (Join-Path $runtime 'Galtools.GarbroModBridge.exe.config') -Force

& $compiler /nologo /target:winexe /optimize+ "/out:$(Join-Path $root 'galtools.exe')" "/win32manifest:$(Join-Path $desktopSource 'app.manifest')" "/resource:$(Join-Path $desktopSource 'Theme.xaml'),Galtools.Theme.xaml" "/resource:$(Join-Path $desktopSource 'Shell.xaml'),Galtools.Shell.xaml" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.Core.dll /reference:System.Xaml.dll "/reference:$(Join-Path $wpf 'PresentationCore.dll')" "/reference:$(Join-Path $wpf 'PresentationFramework.dll')" "/reference:$(Join-Path $wpf 'WindowsBase.dll')" (Join-Path $desktopSource 'Program.cs') (Join-Path $desktopSource 'BridgeClient.cs') (Join-Path $desktopSource 'FuyinPakBackend.cs') (Join-Path $desktopSource 'GameStore.cs') (Join-Path $desktopSource 'DesktopApp.cs')
if ($LASTEXITCODE -ne 0) { throw 'galtools 桌面程序构建失败。' }
Copy-Item -LiteralPath (Join-Path $desktopSource 'App.config') -Destination (Join-Path $root 'galtools.exe.config') -Force
Write-Host "构建完成：$(Join-Path $root 'galtools.exe')"

if ($Package)
{
    $releaseRoot = Join-Path $root 'release'
    $portable = Join-Path $releaseRoot 'galtools-portable'
    New-Item -ItemType Directory -Path $portable -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'galtools.exe') -Destination $portable -Force
    Copy-Item -LiteralPath (Join-Path $root 'galtools.exe.config') -Destination $portable -Force
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $portable -Force
    $docsTarget = Join-Path $portable 'docs'
    New-Item -ItemType Directory -Path $docsTarget -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs\UI-DESIGN.md') -Destination $docsTarget -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs\ADDING-GAME-FORMATS.md') -Destination $docsTarget -Force
    $runtimeTarget = Join-Path $portable 'runtime'
    New-Item -ItemType Directory -Path $runtimeTarget -Force | Out-Null
    Copy-Item -LiteralPath $runtime -Destination $runtimeTarget -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'vendor\GARbro-Mod\LICENSE') -Destination (Join-Path $portable 'GARbro-Mod-LICENSE.txt') -Force
    $zip = Join-Path $releaseRoot 'galtools-portable.zip'
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -LiteralPath $portable -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "便携包：$zip"
}
