param([string]$Configuration = 'Release', [string]$OutputDirectory = 'artifacts\app')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $root
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
$env:GOPATH = Join-Path $root '.tools\gopath'
$env:GOCACHE = Join-Path $root '.tools\gocache'
$env:GOBIN = Join-Path $root 'artifacts\tunnel'
$env:CGO_ENABLED = '0'
$env:GOTOOLCHAIN = 'go1.24.6'
& (Join-Path $PSScriptRoot 'build-logo.ps1')
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$go = Join-Path $root '.tools\go\bin\go.exe'
if (!(Test-Path $go)) {
    New-Item -ItemType Directory -Force -Path (Join-Path $root '.tools') | Out-Null
    $archive = Join-Path $root '.tools\go.zip'
    $expected = '4fbc8af2cfca9e5059019b5150a426eb78e1e57718bf08f0e52b1c942a2782bf'
    if (!(Test-Path $archive) -or (Get-FileHash $archive -Algorithm SHA256).Hash -ne $expected) {
        Invoke-WebRequest -UseBasicParsing -Uri 'https://go.dev/dl/go1.24.6.windows-amd64.zip' -OutFile $archive
    }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Checksum do Go inválido.' }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $root '.tools') -Force
}
Push-Location (Join-Path $root 'src\WireSocksBuild')
try { & $go build -trimpath -o (Join-Path $root 'artifacts\tunnel\wiresocks.exe') github.com/shahradelahi/wiresocks/cmd/wiresocks }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o motor WireGuard.' }
$env:GOTOOLCHAIN = 'go1.26.6'
Push-Location (Join-Path $root 'src\ProtonBridge')
try { & $go build -trimpath -o (Join-Path $root 'artifacts\tunnel\proton-bridge.exe') ./cmd/discsapo-proton }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar a integração Proton.' }
& $dotnet publish 'src\DiscordVpn\DiscordVpn.csproj' -c $Configuration -r win-x64 --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o aplicativo.' }
Copy-Item -LiteralPath 'THIRD-PARTY-wiresocks.txt' -Destination $OutputDirectory
Copy-Item -LiteralPath 'README.md' -Destination $OutputDirectory
$sourceDestination = Join-Path $OutputDirectory 'ProtonBridge-source'
New-Item -ItemType Directory -Force $sourceDestination | Out-Null
Copy-Item -Path 'src\ProtonBridge\*' -Destination $sourceDestination -Recurse -Force
Write-Host "Aplicativo gerado em $OutputDirectory\discSapo.exe"
