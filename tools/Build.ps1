param([string]$SptRoot = 'D:\SPT4', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $repo
try {
    dotnet build src/SPT.Barterizer.csproj -c Release "-p:SptRoot=$SptRoot"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if (-not $SkipTests) {
        dotnet run --project tests -c Release "-p:SptRoot=$SptRoot" -- --spt-root $SptRoot
        if ($LASTEXITCODE -ne 0) { throw 'Verification failed' }
    }
    $packageRoot = [IO.Path]::GetFullPath((Join-Path $repo 'dist/package'))
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $repo 'dist')) + [IO.Path]::DirectorySeparatorChar
    if (-not $packageRoot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package path' }
    if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
    $modFolder = Join-Path $packageRoot 'SPT_Runtime/user/mods/SPT-Barterizer'
    New-Item -ItemType Directory -Path $modFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'src/bin/Release/net10.0/SPT.Barterizer.dll') -Destination $modFolder
    foreach ($name in @('config.json', 'README.md', 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $modFolder }
    $zip = Join-Path $repo 'dist/SPT-Barterizer-0.1.1.zip'
    Compress-Archive -LiteralPath (Join-Path $packageRoot 'SPT_Runtime') -DestinationPath $zip -Force
    Write-Host "Packaged: $zip"
} finally { Pop-Location }
