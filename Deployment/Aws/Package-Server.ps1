[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BuildDirectory,
    [Parameter(Mandatory = $true)][string]$OutputArchive
)
$ErrorActionPreference = 'Stop'
$buildRoot = (Resolve-Path -LiteralPath $BuildDirectory).Path.TrimEnd('\', '/')
$archivePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputArchive)
if ($archivePath.StartsWith($buildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputArchive must be outside the build directory.'
}
if (-not $archivePath.EndsWith('.tar.gz', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use a .tar.gz output name.' }
if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath ($archivePath + '.sha256'))) { throw 'Output archive / checksum already exists; choose a new name.' }
$manifestPath = Join-Path $buildRoot 'server-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Build with Build Linux Server (AWS) first.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.formatVersion -ne 1 -or $manifest.target -ne 'linux-x86_64' -or $manifest.protocolVersion -notin @(5, 6)) {
    throw 'Unexpected server manifest / protocol.'
}
foreach ($required in @('FuchuServer.x86_64', 'FuchuServer_Data', 'UnityPlayer.so', 'StartLocalServer.sh')) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildRoot $required))) { throw "Missing Linux build file: $required" }
}
$tar = Get-Command tar -CommandType Application -ErrorAction Stop
foreach ($existing in @((Get-Item -LiteralPath $buildRoot -Force)) + @(Get-ChildItem -LiteralPath $buildRoot -Recurse -Force)) {
    if ($existing.LinkType) { throw "Symbolic links / junctions are not supported in the bundle: $($existing.FullName)" }
}
$utf8 = New-Object Text.UTF8Encoding($false)
$deploymentPath = Join-Path $buildRoot 'deployment'
[IO.Directory]::CreateDirectory($deploymentPath) | Out-Null
foreach ($name in @('install-server.sh', 'fuchu-server.service', 'server.env', 'fuchu-server.logrotate')) {
    $contents = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $name)).Replace("`r`n", "`n")
    [IO.File]::WriteAllText((Join-Path $deploymentPath $name), $contents, $utf8)
}
$checksums = New-Object 'Collections.Generic.List[string]'
foreach ($item in Get-ChildItem -LiteralPath $buildRoot -Recurse -Force) {
    if ($item.LinkType) { throw "Symbolic links / junctions are not supported in the bundle: $($item.FullName)" }
    if ($item.PSIsContainer -or $item.FullName -eq (Join-Path $buildRoot 'SHA256SUMS')) { continue }
    $relative = $item.FullName.Substring($buildRoot.Length + 1).Replace('\', '/')
    if ($relative.Contains("`n") -or $relative.Contains("`r")) { throw 'Unsupported filename in bundle.' }
    $digest = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums.Add("$digest  $relative")
}
[IO.File]::WriteAllText((Join-Path $buildRoot 'SHA256SUMS'), (($checksums | Sort-Object) -join "`n") + "`n", $utf8)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($archivePath)) | Out-Null
& $tar.Source -czf $archivePath -C $buildRoot .
if ($LASTEXITCODE -ne 0) { throw "tar failed: $LASTEXITCODE" }
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($archivePath + '.sha256', "$archiveHash  $([IO.Path]::GetFileName($archivePath))`n", $utf8)
Write-Output "Package: $archivePath"
Write-Output "SHA256: $archiveHash"
Write-Output 'Local files only. Nothing was uploaded, installed, started or created in AWS.'
