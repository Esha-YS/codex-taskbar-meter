param(
    [ValidateSet('build','install','status','uninstall')][string]$Action = 'status',
    [string]$NodeArchive
)

$ErrorActionPreference = 'Stop'
$version = '1.0.1'
$exeName = 'CodexUsageTaskbar.exe'

if ($Action -eq 'build') {
    $runtimeName = 'node-v24.22.2-win-x64'
    $runtimeHash = 'ca1bff5d8ead13c9f2ea404a176db46bd430704d5b5113734049fa365e7799e2'
    $archiveName = "CodexTaskbarMeter-v$version-win-x64.zip"
    $dist = Join-Path $PSScriptRoot 'dist'
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $stage = Join-Path $tempRoot ('CodexTaskbarMeter-' + [guid]::NewGuid().ToString('N'))
    $release = Join-Path $stage 'release'
    $app = Join-Path $release 'app'
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $app -Force | Out-Null
    try {
        if (!$NodeArchive) {
            $NodeArchive = Join-Path $stage "$runtimeName.zip"
            Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/download/release/v24.22.2/$runtimeName.zip" -OutFile $NodeArchive -TimeoutSec 600
        }
        if ((Get-FileHash -LiteralPath $NodeArchive).Hash -ne $runtimeHash) { throw 'Unexpected Node.js ZIP checksum.' }
        $runtime = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $NodeArchive).Path)
        try {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($runtime.GetEntry("$runtimeName/node.exe"), (Join-Path $app 'node.exe'))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($runtime.GetEntry("$runtimeName/LICENSE"), (Join-Path $app 'NODE-LICENSE.txt'))
        } finally { $runtime.Dispose() }

        $references = @('System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Web.Extensions.dll',
            'WPF\UIAutomationClient.dll', 'WPF\UIAutomationTypes.dll', 'WPF\WindowsBase.dll') |
            ForEach-Object { '/reference:' + (Join-Path $framework $_) }
        $binary = Join-Path $app $exeName
        & $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$binary" @references (Join-Path $PSScriptRoot 'TaskbarCard.cs')
        if ($LASTEXITCODE) { throw 'C# compilation failed.' }
        $check = Start-Process -FilePath $binary -ArgumentList '--check' -WindowStyle Hidden -Wait -PassThru
        if ($check.ExitCode) { throw 'Taskbar placement checks failed.' }
        & (Join-Path $app 'node.exe') --check (Join-Path $PSScriptRoot 'reader.cjs')
        if ($LASTEXITCODE) { throw 'Quota reader syntax check failed.' }

        foreach ($name in 'LICENSE','manage.ps1','reader.cjs') {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $app
        }
        foreach ($name in 'README.md','preview.svg') {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $release
        }
        foreach ($mode in 'Install','Uninstall') {
            @(
                '@echo off',
                'setlocal',
                ('"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0app\manage.ps1" -Action ' + $mode.ToLowerInvariant()),
                'set "result=%errorlevel%"',
                'pause',
                'exit /b %result%'
            ) | Set-Content -LiteralPath (Join-Path $release "$mode.cmd") -Encoding ASCII
        }
        New-Item -ItemType Directory -Path $dist -Force | Out-Null
        $output = Join-Path $dist $archiveName
        $archive = Join-Path $stage $archiveName
        [IO.Compression.ZipFile]::CreateFromDirectory($release, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
        Copy-Item -LiteralPath $archive -Destination $output -Force
        $checksum = (Get-FileHash -LiteralPath $output).Hash.ToLowerInvariant()
        "$checksum  $archiveName" | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII
        Write-Output "Built $output"
    } finally {
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        $expectedPrefix = Join-Path $tempRoot 'CodexTaskbarMeter-'
        if ($resolvedStage.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
    return
}

$root = Join-Path $env:LOCALAPPDATA 'CodexUsageTaskbar'
$marker = Join-Path $root 'version.txt'
$exe = Join-Path $root $exeName
$node = Join-Path $root 'node.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName = 'CodexUsageTaskbar'
$runCommand = '"' + $exe + '"'
$files = @($exeName, 'reader.cjs', 'node.exe', 'LICENSE', 'NODE-LICENSE.txt', 'manage.ps1')

function Get-Startup {
    if (Test-Path -LiteralPath $runKey) { (Get-Item -LiteralPath $runKey).GetValue($runName) }
}

function Get-Card {
    Get-Process -Name CodexUsageTaskbar -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
}

function Stop-Card {
    Get-Card | ForEach-Object {
        Stop-Process -Id $_.Id
        if (!$_.WaitForExit(5000)) { throw 'The old card process did not stop.' }
    }
    Get-Process -Name node -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $node } | ForEach-Object {
        if (!$_.WaitForExit(5000)) { Stop-Process -Id $_.Id }
    }
}

if (Test-Path -LiteralPath $root) {
    if ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected install path.' }
    if (!(Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Install ownership mismatch.' }
    $installedVersion = (Get-Content -LiteralPath $marker -Raw).Trim()
    if ($installedVersion -ne $version -and !($Action -eq 'install' -and $installedVersion -in @('0.9.5-taskbar.1','0.9.5-taskbar.2','0.9.5-taskbar.3','1.0.0'))) { throw 'Install ownership mismatch.' }
} elseif ($Action -eq 'uninstall') { return }

switch ($Action) {
    install {
        $startup = Get-Startup
        if ($null -ne $startup -and $startup -ne $runCommand) { throw 'The startup name is in use.' }
        if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64' -and $env:PROCESSOR_ARCHITEW6432 -ne 'AMD64') { throw 'This release requires Windows x64.' }
        foreach ($name in $files) {
            if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot $name) -PathType Leaf)) { throw "Missing $name" }
        }
        Stop-Card
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        Set-Content -LiteralPath $marker -Value $version -Encoding ASCII
        foreach ($name in $files) {
            $source = Join-Path $PSScriptRoot $name
            $destination = Join-Path $root $name
            if ($source -ne $destination) { Copy-Item -LiteralPath $source -Destination $destination -Force }
        }
        if (!(Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
        Set-ItemProperty -LiteralPath $runKey -Name $runName -Value $runCommand
        Start-Process -FilePath $exe -WindowStyle Normal
        Start-Sleep -Milliseconds 600
        if (!(Get-Card)) { throw 'The taskbar card did not start.' }
        Write-Output "Installed $version."
    }
    status {
        [pscustomobject]@{
            installed = Test-Path -LiteralPath $marker
            version = $installedVersion
            running = [bool](Get-Card)
            startsAtLogin = (Get-Startup) -eq $runCommand
        } | ConvertTo-Json
    }
    uninstall {
        if ((Get-Startup) -eq $runCommand) { Remove-ItemProperty -LiteralPath $runKey -Name $runName }
        Stop-Card
        foreach ($name in ($files + 'version.txt')) {
            $path = Join-Path $root $name
            if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path }
        }
        if (!(Get-ChildItem -LiteralPath $root -Force)) { Remove-Item -LiteralPath $root }
        Write-Output 'Taskbar card uninstalled.'
    }
}
