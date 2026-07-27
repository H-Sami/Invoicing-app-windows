param(
    [string]$Thumbprint = $env:MHC_DEV_SIGNING_THUMBPRINT,
    [string[]]$Paths = @("src", "tests"),
    [switch]$IncludeAll
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
    $Thumbprint = [Environment]::GetEnvironmentVariable("MHC_DEV_SIGNING_THUMBPRINT", "User")
}
if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
    throw "MHC_DEV_SIGNING_THUMBPRINT is not configured."
}

$certificate = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey) {
    throw "The selected development signing certificate has no private key."
}

$signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $signTool) {
    throw "Windows SDK signtool.exe was not found."
}

$files = foreach ($path in $Paths) {
    if (Test-Path $path) {
        Get-ChildItem $path -Recurse -File |
            Where-Object {
                ($IncludeAll -or $_.FullName -match "[\\/]bin[\\/]") -and
                ($_.Name -like "MHC*.dll" -or
                 $_.Name -like "MHC*.exe")
            }
    }
}
$files = @($files | Sort-Object FullName -Unique)
if ($files.Count -eq 0) {
    throw "No MHC binaries were found beneath the requested paths."
}

foreach ($file in $files) {
    & $signTool.FullName sign /sha1 $Thumbprint /fd SHA256 $file.FullName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Development signing failed for $($file.FullName)."
    }
}

Write-Output "Development-signed files: $($files.Count)"
Write-Warning "These signatures are trusted only on this development PC and are not public-release signatures."
