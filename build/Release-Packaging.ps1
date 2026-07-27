Set-StrictMode -Version Latest

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $base = [IO.Path]::GetFullPath($BasePath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$full' is outside '$base'."
    }
    return $full.Substring($base.Length + 1)
}

function Get-ExpectedAppRuntimeAssetPaths {
    param([Parameter(Mandatory = $true)][string]$ProjectDirectory)

    $projectRoot = [IO.Path]::GetFullPath($ProjectDirectory)
    $xamlAssets = @(Get-ChildItem $projectRoot -Recurse -File -Filter "*.xaml" | Where-Object {
        $relative = Get-RelativePath -BasePath $projectRoot -Path $_.FullName
        $segments = $relative -split '[\\/]'
        $segments[0] -ne "bin" -and $segments[0] -ne "obj"
    } | ForEach-Object {
        $relative = Get-RelativePath -BasePath $projectRoot -Path $_.FullName
        [IO.Path]::ChangeExtension($relative, ".xbf")
    })

    return @($xamlAssets + "MHC.Invoicing.pri" | Sort-Object -Unique)
}

function Assert-AppRuntimeAssets {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectDirectory,
        [Parameter(Mandatory = $true)][string]$BuildOutput
    )

    if (-not (Test-Path $BuildOutput -PathType Container)) {
        throw "The x64 build output does not exist: $BuildOutput"
    }

    $expected = @(Get-ExpectedAppRuntimeAssetPaths -ProjectDirectory $ProjectDirectory)
    $actual = @(Get-ChildItem $BuildOutput -Recurse -File | Where-Object {
        $_.Extension -ieq ".xbf" -or
        ($_.Extension -ieq ".pri" -and $_.Name -notlike "Microsoft*.pri")
    } | ForEach-Object {
        Get-RelativePath -BasePath $BuildOutput -Path $_.FullName
    } | Sort-Object -Unique)

    $difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual)
    if ($difference.Count -ne 0) {
        $missing = @($difference | Where-Object { $_.SideIndicator -eq "<=" } | ForEach-Object { $_.InputObject })
        $unexpected = @($difference | Where-Object { $_.SideIndicator -eq "=>" } | ForEach-Object { $_.InputObject })
        $parts = @()
        if ($missing.Count -ne 0) {
            $parts += "missing: $($missing -join ', ')"
        }
        if ($unexpected.Count -ne 0) {
            $parts += "unexpected/stale: $($unexpected -join ', ')"
        }
        throw "The app-owned XBF/PRI set is not exact ($($parts -join '; '))."
    }

    return $expected
}

function Write-AppPayloadManifest {
    param([Parameter(Mandatory = $true)][string]$PublishDirectory)

    $publishRoot = [IO.Path]::GetFullPath($PublishDirectory)
    if (-not (Test-Path $publishRoot -PathType Container)) {
        throw "The publish directory does not exist: $publishRoot"
    }

    $manifestName = ".mhc-payload-manifest.txt"
    $manifestPath = Join-Path $publishRoot $manifestName
    $relativePaths = @(Get-ChildItem $publishRoot -Recurse -File | Where-Object {
        $_.FullName -ne $manifestPath
    } | ForEach-Object {
        Get-RelativePath -BasePath $publishRoot -Path $_.FullName
    })
    $entries = @($relativePaths + $manifestName | Sort-Object -Unique)
    [IO.File]::WriteAllLines($manifestPath, $entries, (New-Object Text.UTF8Encoding($false)))
}

function Copy-AppRuntimeAssets {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectDirectory,
        [Parameter(Mandatory = $true)][string]$BuildOutput,
        [Parameter(Mandatory = $true)][string]$PublishDirectory
    )

    $assets = @(Assert-AppRuntimeAssets -ProjectDirectory $ProjectDirectory -BuildOutput $BuildOutput)
    foreach ($relativePath in $assets) {
        $source = Join-Path $BuildOutput $relativePath
        $destination = Join-Path $PublishDirectory $relativePath
        New-Item (Split-Path $destination -Parent) -ItemType Directory -Force | Out-Null
        Copy-Item $source $destination -Force
    }

    $publishedAssets = @(Get-ChildItem $PublishDirectory -Recurse -File | Where-Object {
        $_.Extension -ieq ".xbf" -or
        ($_.Extension -ieq ".pri" -and $_.Name -notlike "Microsoft*.pri")
    } | ForEach-Object {
        Get-RelativePath -BasePath $PublishDirectory -Path $_.FullName
    } | Sort-Object -Unique)
    $difference = @(Compare-Object -ReferenceObject $assets -DifferenceObject $publishedAssets)
    if ($difference.Count -ne 0) {
        throw "Published app-owned XBF/PRI assets do not match the freshly built set."
    }
}
