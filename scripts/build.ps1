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

& $compiler /nologo /target:winexe /optimize+ "/out:$(Join-Path $root 'galtools.exe')" "/win32manifest:$(Join-Path $desktopSource 'app.manifest')" "/resource:$(Join-Path $desktopSource 'Theme.xaml'),Galtools.Theme.xaml" "/resource:$(Join-Path $desktopSource 'Shell.xaml'),Galtools.Shell.xaml" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.Core.dll /reference:System.Xaml.dll "/reference:$(Join-Path $wpf 'PresentationCore.dll')" "/reference:$(Join-Path $wpf 'PresentationFramework.dll')" "/reference:$(Join-Path $wpf 'WindowsBase.dll')" (Join-Path $desktopSource 'Program.cs') (Join-Path $desktopSource 'BridgeClient.cs') (Join-Path $desktopSource 'FuyinPakBackend.cs') (Join-Path $desktopSource 'GameStore.cs') (Join-Path $desktopSource 'DesktopApp.cs') (Join-Path $desktopSource 'Appearance.cs') (Join-Path $desktopSource 'GameLibrary.cs') (Join-Path $desktopSource 'UnpackPrompt.cs')
if ($LASTEXITCODE -ne 0) { throw 'galtools 桌面程序构建失败。' }
Copy-Item -LiteralPath (Join-Path $desktopSource 'App.config') -Destination (Join-Path $root 'galtools.exe.config') -Force
Write-Host "构建完成：$(Join-Path $root 'galtools.exe')"

if ($Package)
{
    $releaseRoot = Join-Path $root 'release'
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    $files = @('galtools.exe', 'galtools.exe.config', 'README.md', '.gitignore')
    # Allowlist project directories; never include personal data or previous releases.
    foreach ($name in @('src', 'scripts', 'docs', 'tests', 'vendor', 'runtime', 'assets', 'tools')) {
        $sourceRoot = Join-Path $root $name
        if (!(Test-Path -LiteralPath $sourceRoot)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force) {
            $relative = $file.FullName.Substring($root.Length + 1)
            if ($relative -match '(^|[\\/])(\.git|\.vs|\.idea|\.vscode|bin|obj|packages|__pycache__|\.pytest_cache|\.venv|venv|\.downloads)([\\/]|$)') { continue }
            if ($file.Name -match '(^\.env($|\.)|\.(pyc|pyo|log|tmp|partial|galtools-partial|user|suo|cache)$)') { continue }
            if ($relative -match '^tests[\\/].*\.(exe|pdb)(\.config)?$') { continue }
            if ($relative -match '^runtime[\\/]garbro-mod[\\/]Galtools\.FormatData\.(exe|pdb)$') { continue }
            $files += $relative
        }
    }
    Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
    $zip = Join-Path $releaseRoot 'galtools-full.zip'
    $partial = $zip + '.partial'
    $stream = [IO.File]::Open($partial, [IO.FileMode]::Create)
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in $files) {
            $entry = 'galtools/' + $relative.Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root $relative), $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
    Move-Item -LiteralPath $partial -Destination $zip -Force
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($zip + '.sha256', $hash + '  galtools-full.zip' + [Environment]::NewLine, [Text.Encoding]::ASCII)
    Write-Host "完整发布包：$zip"
}
