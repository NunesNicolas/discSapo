param([string]$KeepRelease = 'app-0.7.2')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)).TrimEnd('\')
$release = Join-Path $root (Join-Path 'artifacts' $KeepRelease)
if (!(Test-Path -LiteralPath (Join-Path $release 'discSapo.exe'))) { throw "A versão $KeepRelease não foi encontrada. Limpeza cancelada." }

# Explicit generated targets only. Never touch the user's AppData.
$targets = [Collections.Generic.List[string]]::new()
foreach ($relative in @('.tools', 'teste.conf', 'Releases')) {
    $path = Join-Path $root $relative
    if (Test-Path -LiteralPath $path) { $targets.Add($path) }
}
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $root 'artifacts') -Force) {
    if ($item.FullName -ne $release) { $targets.Add($item.FullName) }
}
foreach ($parent in @('src\DiscordVpn', 'tests\BrowserProbe', 'tests\MediaPermissionChecks', 'tests\PortalChecks', 'tests\ProtonChecks', 'tests\TunnelChecks')) {
    foreach ($output in @('bin', 'obj')) {
        $path = Join-Path $root "$parent\$output"
        if (Test-Path -LiteralPath $path) { $targets.Add($path) }
    }
}
foreach ($item in Get-ChildItem -LiteralPath $release -File) {
    if ($item.Extension -eq '.pdb' -or $item.Name -like 'Microsoft.Web.WebView2.*.xml') { $targets.Add($item.FullName) }
}

# Resolve all paths and reject links before deletion; keep native PowerShell
# filesystem operations end-to-end, including paths containing spaces.
$bytes = 0L
foreach ($target in $targets) {
    $resolved = (Resolve-Path -LiteralPath $target).ProviderPath
    if (!$resolved.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $release) {
        throw "Destino fora da área permitida: $resolved"
    }
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Link encontrado: $resolved" }
    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $resolved -Recurse -Force) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Link encontrado: $($child.FullName)" }
            if (!$child.PSIsContainer) { $bytes += $child.Length }
        }
    } else { $bytes += $item.Length }
}
foreach ($target in $targets) { Remove-Item -LiteralPath $target -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $release 'README.md') -Force
Write-Output ("Limpeza concluída: {0} itens; {1:N1} MB removidos." -f $targets.Count, ($bytes / 1MB))
