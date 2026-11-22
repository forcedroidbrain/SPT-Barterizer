param([string]$SptRoot = 'D:\SPT4', [switch]$SkipTests, [string]$PackageSuffix = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# Preserve the configured shared targeting-pack cache before relocating CLI state.
# The client needs NETStandard.Library.Ref even though it has no package dependencies.
if (-not $env:NUGET_PACKAGES) {
    $packageLocation = dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0 -or $packageLocation -notmatch '^global-packages:\s*(.+)$') { throw 'Cannot resolve NuGet cache' }
    $env:NUGET_PACKAGES = $Matches[1].Trim()
}
$env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $repo
try {
    [xml]$project = Get-Content -LiteralPath (Join-Path $repo 'src/SPT.Barterizer.csproj') -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid package version in project file' }
    if ($PackageSuffix -notmatch '^[a-z0-9-]*$') { throw 'Invalid package suffix' }
    $buildArtifacts = Join-Path $repo 'artifacts/package-build'
    dotnet build src/SPT.Barterizer.csproj -c Release --artifacts-path $buildArtifacts "-p:SptRoot=$SptRoot"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet build client/SPT.Barterizer.Client.csproj -c Release --artifacts-path $buildArtifacts "-p:SptRoot=$SptRoot" -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Client build failed' }
    if (-not $SkipTests) {
        dotnet run --project tests -c Release --artifacts-path $buildArtifacts "-p:SptRoot=$SptRoot" -- --spt-root $SptRoot
        if ($LASTEXITCODE -ne 0) { throw 'Verification failed' }
        dotnet run --project client-checks -c Release --artifacts-path $buildArtifacts "-p:SptRoot=$SptRoot" -- $SptRoot (Join-Path $buildArtifacts 'bin/SPT.Barterizer.Client/release/SPT.Barterizer.Client.dll')
        if ($LASTEXITCODE -ne 0) { throw 'Client bindings verification failed' }
    }
    $packageRoot = [IO.Path]::GetFullPath((Join-Path $repo 'dist/package'))
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $repo 'dist')) + [IO.Path]::DirectorySeparatorChar
    if (-not $packageRoot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package path' }
    if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
    $modFolder = Join-Path $packageRoot 'SPT_Runtime/user/mods/SPT-Barterizer'
    New-Item -ItemType Directory -Path $modFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $buildArtifacts 'bin/SPT.Barterizer/release/SPT.Barterizer.dll') -Destination $modFolder
    $clientFolder = Join-Path $packageRoot 'BepInEx/plugins/SPT-Barterizer'
    New-Item -ItemType Directory -Path $clientFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $buildArtifacts 'bin/SPT.Barterizer.Client/release/SPT.Barterizer.Client.dll') -Destination $clientFolder
    foreach ($name in @('config.json', 'README.md', 'RELEASE_NOTES.md', 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $modFolder }
    $suffix = if ($PackageSuffix) { "-$PackageSuffix" } else { '' }
    $zip = Join-Path $repo "dist/SPT-Barterizer-$version$suffix.zip"
    Compress-Archive -LiteralPath @((Join-Path $packageRoot 'SPT_Runtime'), (Join-Path $packageRoot 'BepInEx')) -DestinationPath $zip -Force
    Write-Host "Packaged: $zip"
} finally { Pop-Location }
