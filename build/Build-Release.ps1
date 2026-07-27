[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$UseDevelopmentSigning
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "MHC.Invoicing.sln"
$appDirectory = Join-Path $root "src\MHC.Invoicing.App"
$project = Join-Path $appDirectory "MHC.Invoicing.App.csproj"
$publish = Join-Path $root "artifacts\publish\win-x64"
$testResults = Join-Path $root "artifacts\test-results\release"
$installerDirectory = Join-Path $root "artifacts\installer"
$innoScript = Join-Path $root "installer\MHC.Invoicing.iss"
$developmentSigningScript = Join-Path $root "build\Sign-Development.ps1"
$releasePackagingScript = Join-Path $root "build\Release-Packaging.ps1"
$xamlBuildOutput = Join-Path $root "src\MHC.Invoicing.App\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64"
$appBin = Join-Path $appDirectory "bin"
$appObj = Join-Path $appDirectory "obj"

. $releasePackagingScript

function Invoke-Checked {
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

Push-Location $root
try {
    # XBF/PRI staging must never read a persistent ignored build tree. Remove the
    # entire app build state before restore/build so every staged asset is fresh.
    Remove-Item $appBin, $appObj, $publish, $testResults, $installerDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $publish, $testResults, $installerDirectory -ItemType Directory -Force | Out-Null

    Invoke-Checked dotnet @("restore", $solution, "--locked-mode")
    Invoke-Checked dotnet @("format", $solution, "--no-restore", "--verify-no-changes", "--verbosity", "minimal")
    Invoke-Checked dotnet @("build", $solution, "-c", $Configuration, "-p:Platform=x64", "--no-restore", "-v", "minimal")
    if ($UseDevelopmentSigning) {
        & $developmentSigningScript -Paths @("src", "tests")
    }
    Invoke-Checked dotnet @("test", $solution, "-c", $Configuration, "-p:Platform=x64", "--no-build", "--no-restore", "--logger", "trx", "--results-directory", $testResults, "-v", "minimal")
    if ($UseDevelopmentSigning) {
        Invoke-Checked dotnet @(
            "test", "tests/MHC.Invoicing.Infrastructure.Tests/MHC.Invoicing.Infrastructure.Tests.csproj",
            "-c", $Configuration, "-p:LocalQaArtifact=true", "--no-restore",
            "--filter", "FullyQualifiedName~AppDataPathsTests", "-v", "minimal")
        Invoke-Checked dotnet @(
            "test", "tests/MHC.Invoicing.Application.Tests/MHC.Invoicing.Application.Tests.csproj",
            "-c", $Configuration, "-p:LocalQaArtifact=true", "--no-restore",
            "--filter", "FullyQualifiedName~TemporaryPdfFileTests", "-v", "minimal")
        Invoke-Checked dotnet @(
            "test", "tests/MHC.Invoicing.Ui.Tests/MHC.Invoicing.Ui.Tests.csproj",
            "-c", $Configuration, "-p:Platform=x64", "-p:LocalQaArtifact=true", "--no-restore",
            "--filter", "FullyQualifiedName~LocalizationResourceTests", "-v", "minimal")
    }
    Invoke-Checked python @("tests/packaging/test_release_packaging.py")
    $publishArguments = @("publish", $project, "-c", $Configuration, "-p:Platform=x64", "-r", "win-x64", "--self-contained", "true", "--no-restore", "-o", $publish, "-v", "minimal")
    if ($UseDevelopmentSigning) {
        $publishArguments += "-p:LocalQaArtifact=true"
    }
    Invoke-Checked dotnet $publishArguments

    # Self-contained .NET output includes crash-dump/debugger helpers that are not
    # required to run the application. Exclude them from the production payload;
    # endpoint protection can quarantine these binaries while Inno is reading the
    # source tree, which otherwise makes installer creation nondeterministic.
    $diagnosticRuntimeNames = @("createdump.exe", "dbgshim.dll", "mscordbi.dll")
    Get-ChildItem $publish -File | Where-Object {
        $_.Name -in $diagnosticRuntimeNames -or $_.Name -like "mscordaccore*.dll"
    } | Remove-Item -Force

    # The unpackaged WinUI publish target omits compiled XAML and the application
    # PRI. Validate and copy only the exact app-owned set from this clean build.
    Copy-AppRuntimeAssets -ProjectDirectory $appDirectory -BuildOutput $xamlBuildOutput -PublishDirectory $publish
    if ($UseDevelopmentSigning) {
        & $developmentSigningScript -Paths @($publish) -IncludeAll
    }
    Write-AppPayloadManifest -PublishDirectory $publish

    $application = Join-Path $publish "MHC.Invoicing.exe"
    if (-not (Test-Path $application -PathType Leaf)) {
        throw "Published application executable is missing."
    }
    $forbidden = Get-ChildItem $publish -Recurse -File | Where-Object {
        $_.Extension -in @(".pdb", ".trx", ".py", ".ps1", ".iss") -or $_.Name -match "(?i)testhost|\.Tests\."
    }
    if ($forbidden) {
        throw "Development-only files were found in publish output: $($forbidden.FullName -join ', ')"
    }

    $isccCandidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe"
    )
    $iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) } | Select-Object -First 1
    if (-not $iscc) {
        throw "Inno Setup compiler was not found."
    }
    $isccArguments = @($innoScript)
    $artifactLabel = "unsigned release candidate"
    $setupBaseName = "MHC-Invoices-V4-Setup-x64-Unsigned"
    if ($UseDevelopmentSigning) {
        $isccArguments = @("/DLocalQaArtifact=1", $innoScript)
        $artifactLabel = "development-signed local-QA artifact (not production releasable)"
        $setupBaseName = "MHC-Invoices-V4-Setup-x64-LocalQA"
    }
    Invoke-Checked $iscc $isccArguments

    $setup = Join-Path $installerDirectory "$setupBaseName.exe"
    if (-not (Test-Path $setup -PathType Leaf)) {
        throw "Installer output is missing."
    }
    if ($UseDevelopmentSigning) {
        & $developmentSigningScript -Paths @($setup) -IncludeAll
    }
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = "$setup.sha256"
    [IO.File]::WriteAllText($checksumPath, "$hash  $([IO.Path]::GetFileName($setup))`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "Installer ($artifactLabel): $setup"
    if ($UseDevelopmentSigning) {
        Write-Warning "-UseDevelopmentSigning produced a local-QA artifact. It is not a production release and must not be distributed as one."
    }
    Write-Host "SHA-256: $hash"
}
finally {
    Pop-Location
}
