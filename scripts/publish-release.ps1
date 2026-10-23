<#
.SYNOPSIS
    Publishes the version in versions.json as a GitHub release, from this machine.

.DESCRIPTION
    Releases are published from this machine only: the build compiles against the local
    game-refs, so the game's files never leave it. One command:

      1. checks that versions.json and the CHANGELOG section agree;
      2. builds, tests and packages the mod (zip + .sha256);
      3. tags HEAD v<version> and pushes the branch and the tag;
      4. creates the release as a draft, uploads both files, then publishes it, so the
         launcher never sees a release without its archive; a "(beta)" heading publishes
         a pre-release.

    The GitHub token comes from GH_TOKEN / GITHUB_TOKEN, or from the login git already
    uses for github.com.

.EXAMPLE
    ./scripts/publish-release.ps1            # publish v<versions.json mod>
    ./scripts/publish-release.ps1 -DryRun    # check, build, test and package only
    ./scripts/publish-release.ps1 -Tag v2.6.2-r2   # republish the same version
#>
param(
    [string]$Tag,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path "$PSScriptRoot/..").Path
Set-Location $Root

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

$ModVersion = (Get-Content "versions.json" -Raw | ConvertFrom-Json).mod
if ([string]::IsNullOrWhiteSpace($Tag)) { $Tag = "v$ModVersion" }

Write-Host ""
Write-Host "  CairnMP release $Tag$(if ($DryRun) { ' (dry run)' })" -ForegroundColor Cyan
Write-Host ""

# --- 1. What is being released -------------------------------------------------------
Write-Host "[1/4] Checking versions and changelog..."
Invoke-Checked "Version check" { node scripts/sync-versions.js --check }
$NotesFile = Join-Path ([IO.Path]::GetTempPath()) "cairnmp-release-notes-$ModVersion.md"
$infoLines = node scripts/release-info.js $Tag $NotesFile
if ($LASTEXITCODE -ne 0) { throw "The release cannot be read from versions.json and CHANGELOG.md" }
$info = @{}
foreach ($line in $infoLines) { $key, $value = $line -split '=', 2; $info[$key] = $value }
$Prerelease = $info.prerelease -eq 'true'
Write-Host "  $($info.title)$(if ($Prerelease) { ', pre-release' })"

$dirty = git status --porcelain
if ($dirty) {
    $message = "Commit or stash your changes first: a release is built from HEAD.`n$($dirty -join "`n")"
    if (-not $DryRun) { throw $message }
    Write-Host "  $message" -ForegroundColor Yellow
}
$Head = (git rev-parse HEAD).Trim()
$Branch = (git branch --show-current).Trim()

$remote = (git remote get-url origin).Trim()
if ($remote -notmatch 'github\.com[:/](?<repo>[^/]+/[^/]+?)(\.git)?$') { throw "origin is not a GitHub repository: $remote" }
$Repo = $Matches.repo
$Api = "https://api.github.com/repos/$Repo"

$Token = if ($env:GH_TOKEN) { $env:GH_TOKEN } elseif ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else {
    $env:GIT_TERMINAL_PROMPT = '0'
    ("protocol=https`nhost=github.com`n`n" | git credential fill 2>$null |
        Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
}
if ([string]::IsNullOrWhiteSpace($Token)) { throw "No GitHub token: set GH_TOKEN, or sign git in to github.com." }
$Headers = @{
    Authorization          = "Bearer $Token"
    Accept                 = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
}

$existing = $null
try { $existing = Invoke-RestMethod -Headers $Headers -Uri "$Api/releases/tags/$Tag" } catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
}
if ($existing) {
    $message = "$Repo already has a release for $Tag. Bump the version, or republish with -Tag $Tag-r2."
    if (-not $DryRun) { throw $message }
    Write-Host "  $message" -ForegroundColor Yellow
}

# --- 2. Build, test, package ---------------------------------------------------------
Write-Host "[2/4] Building and testing..."
Invoke-Checked "Build and test" {
    dotnet test CairnMultiplayer.sln -c Release -p:DeployMod=false -p:PackageOnSolutionBuild=false --nologo -v minimal
}

Write-Host "[3/4] Packaging..."
# A dry run packages aside: a rebuilt archive never matches a published one byte for byte.
$Dist = if ($DryRun) { Join-Path ([IO.Path]::GetTempPath()) "cairnmp-release-dry-run" }
        else { Join-Path (Split-Path -Parent $Root) "CairnMP-packages" }
& ./scripts/package-mod.ps1 -SkipBuild -OutputDirectory $Dist
$ZipName = "cairnmp-mod-$ModVersion.zip"
$Zip = Join-Path $Dist $ZipName
$Hash = (Get-FileHash -Algorithm SHA256 $Zip).Hash.ToLowerInvariant()
$ShaFile = "$Zip.sha256"
[IO.File]::WriteAllText($ShaFile, "$Hash  $ZipName`n")

if ($DryRun) {
    Write-Host ""
    Write-Host "  Dry run: $ZipName is ready ($Hash). Nothing was tagged, pushed or published." -ForegroundColor Yellow
    return
}

# --- 3. Tag and push -----------------------------------------------------------------
Write-Host "[4/4] Publishing to $Repo..."
$tagged = git rev-parse -q --verify "refs/tags/$Tag^{commit}" 2>$null
if ($tagged) {
    if ($tagged.Trim() -ne $Head) { throw "Tag $Tag already exists on another commit ($($tagged.Trim()))." }
} else {
    Invoke-Checked "Tagging" { git tag $Tag }
}
if ($Branch) { Invoke-Checked "Pushing $Branch" { git push origin $Branch } }
Invoke-Checked "Pushing $Tag" { git push origin $Tag }

# --- 4. Draft, upload, publish -------------------------------------------------------
$body = @{
    tag_name         = $Tag
    target_commitish = $Head
    name             = $info.title
    body             = Get-Content $NotesFile -Raw
    draft            = $true
    prerelease       = $Prerelease
} | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Headers $Headers -Uri "$Api/releases" -Body $body -ContentType "application/json"
try {
    $upload = "https://uploads.github.com/repos/$Repo/releases/$($release.id)/assets"
    Invoke-RestMethod -Method Post -Headers $Headers -Uri "$upload?name=$ZipName" -InFile $Zip -ContentType "application/zip" | Out-Null
    Invoke-RestMethod -Method Post -Headers $Headers -Uri "$upload?name=$ZipName.sha256" -InFile $ShaFile -ContentType "text/plain" | Out-Null
    $release = Invoke-RestMethod -Method Patch -Headers $Headers -Uri "$Api/releases/$($release.id)" `
        -Body (@{ draft = $false } | ConvertTo-Json) -ContentType "application/json"
} catch {
    # Never leave a half-made release behind: the draft goes, the tag stays for a retry.
    Invoke-RestMethod -Method Delete -Headers $Headers -Uri "$Api/releases/$($release.id)" | Out-Null
    throw
}

# What players download must be what was built here.
$check = Join-Path ([IO.Path]::GetTempPath()) "cairnmp-release-check.zip"
Invoke-WebRequest -Uri "https://github.com/$Repo/releases/download/$Tag/$ZipName" -OutFile $check
$published = (Get-FileHash -Algorithm SHA256 $check).Hash.ToLowerInvariant()
Remove-Item $check
if ($published -ne $Hash) { throw "The published archive does not match the local one ($published vs $Hash)." }

Write-Host ""
Write-Host "  Published $($info.title): $($release.html_url)" -ForegroundColor Green
Write-Host "  SHA256: $Hash"
