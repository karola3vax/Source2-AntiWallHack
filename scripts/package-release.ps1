param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Split-Path -Parent $scriptDir
$projectPath = Join-Path $projectDir "S2AWH.csproj"

if ([string]::IsNullOrWhiteSpace($Version))
{
    [xml]$projectXml = Get-Content $projectPath
    $versionNode = $projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace([string]$versionNode.Version))
    {
        throw "Could not resolve <Version> from $projectPath. Pass -Version explicitly."
    }

    $Version = [string]$versionNode.Version
}

$stagingRoot = Join-Path $projectDir "release_staging"
$packageName = "S2AWH-$Version"
$packageDir = Join-Path $stagingRoot $packageName
$zipPath = Join-Path $stagingRoot "$packageName.zip"
$sha256Path = Join-Path $stagingRoot "$packageName.sha256"
$buildOutput = Join-Path $projectDir "bin\$Configuration\net8.0"

Write-Host "Building S2AWH $Version ($Configuration) with strict analyzers..."
dotnet build $projectPath `
    -c $Configuration `
    -p:EnableNETAnalyzers=true `
    -p:AnalysisMode=AllEnabledByDefault `
    -p:TreatWarningsAsErrors=true `
    -p:UseSharedCompilation=false | Out-Host

if ($LASTEXITCODE -ne 0)
{
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$pluginDll = Join-Path $buildOutput "S2AWH.dll"
$pluginDeps = Join-Path $buildOutput "S2AWH.deps.json"
$pluginPdb = Join-Path $buildOutput "S2AWH.pdb"
$exampleConfig = Join-Path $projectDir "configs\plugins\S2AWH\S2AWH.example.json"
$readme = Join-Path $projectDir "README.md"
$license = Join-Path $projectDir "LICENSE"
$changelog = Join-Path $projectDir "CHANGELOG.md"
$releaseNotes = Join-Path $projectDir ("RELEASE_NOTES_{0}.md" -f $Version)
if (-not (Test-Path $releaseNotes) -and $Version -match '^([0-9]+\.[0-9]+\.[0-9]+)')
{
    $releaseNotes = Join-Path $projectDir ("RELEASE_NOTES_{0}.md" -f $Matches[1])
}

if (-not (Test-Path $releaseNotes))
{
    $changelogLines = Get-Content $changelog
    $header = "## $Version"
    $startIndex = [Array]::IndexOf($changelogLines, ($changelogLines | Where-Object { $_ -like "$header*" } | Select-Object -First 1))
    if ($startIndex -ge 0)
    {
        $endIndex = $changelogLines.Length
        for ($i = $startIndex + 1; $i -lt $changelogLines.Length; $i++)
        {
            if ($changelogLines[$i] -like '## *')
            {
                $endIndex = $i
                break
            }
        }

        $generatedNotes = @("# S2AWH $Version", "") + $changelogLines[$startIndex..($endIndex - 1)]
        $releaseNotes = Join-Path $projectDir ("RELEASE_NOTES_{0}.generated.md" -f $Version)
        Set-Content -Path $releaseNotes -Value $generatedNotes -Encoding UTF8
    }
}

$requiredFiles = @(
    $pluginDll,
    $pluginDeps,
    $exampleConfig,
    $readme,
    $license,
    $changelog,
    $releaseNotes
)

foreach ($path in $requiredFiles)
{
    if (-not (Test-Path $path))
    {
        throw "Required release file missing: $path"
    }
}

if (Test-Path $packageDir)
{
    Remove-Item $packageDir -Recurse -Force
}

if (Test-Path $zipPath)
{
    Remove-Item $zipPath -Force
}

New-Item -ItemType Directory -Path (Join-Path $packageDir "addons\counterstrikesharp\plugins\S2AWH") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageDir "addons\counterstrikesharp\configs\plugins\S2AWH") -Force | Out-Null

Copy-Item $pluginDll (Join-Path $packageDir "addons\counterstrikesharp\plugins\S2AWH\S2AWH.dll")
Copy-Item $pluginDeps (Join-Path $packageDir "addons\counterstrikesharp\plugins\S2AWH\S2AWH.deps.json")
if (Test-Path $pluginPdb)
{
    Copy-Item $pluginPdb (Join-Path $packageDir "addons\counterstrikesharp\plugins\S2AWH\S2AWH.pdb")
}
Copy-Item $exampleConfig (Join-Path $packageDir "addons\counterstrikesharp\configs\plugins\S2AWH\S2AWH.example.json")
Copy-Item $readme (Join-Path $packageDir "README.md")
Copy-Item $license (Join-Path $packageDir "LICENSE")
Copy-Item $changelog (Join-Path $packageDir "CHANGELOG.md")
Copy-Item $releaseNotes (Join-Path $packageDir "RELEASE_NOTES.md")

Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
$sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $sha256Path -Value "$sha256 *$packageName.zip" -Encoding ASCII

Write-Host "Release package created:"
Write-Host "  Folder: $packageDir"
Write-Host "  Zip:    $zipPath"
Write-Host "  SHA256: $sha256Path"
