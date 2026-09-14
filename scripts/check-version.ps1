param([Parameter(Mandatory=$true)][string]$Tag)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\DiscordVpn\DiscordVpn.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { throw "Versão inválida no projeto: $version" }
if ($Tag -ne "v$version") { throw "Use a tag v$version; foi recebida $Tag." }
Write-Output "Versão confirmada: $version"
