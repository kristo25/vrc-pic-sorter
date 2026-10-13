param(
    [string] $Executable = (Join-Path $PSScriptRoot '../publish/VrcPicSorter.exe')
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Executable).Path
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('VrcPicSorter-codec-smoke-' + [Guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $smokeRoot 'app'
$extractionDirectory = Join-Path $smokeRoot 'bundle'
New-Item -ItemType Directory -Path $appDirectory, $extractionDirectory | Out-Null
$portable = Join-Path $appDirectory 'VrcPicSorter.exe'
# An otherwise empty folder ensures loose build-output native dependencies cannot mask
# an incomplete single-file artifact. Each invocation also gets a fresh extraction cache.
Copy-Item -LiteralPath $source -Destination $portable
$previousExtractionDirectory = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
try {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $extractionDirectory
    $process = Start-Process -FilePath $portable -ArgumentList '--verify-codec' -WorkingDirectory $appDirectory -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) {
        $process.Kill()
        throw 'Published codec verification timed out.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Published codec verification failed with exit code $($process.ExitCode)."
    }
    $nativeCodec = @(Get-ChildItem -LiteralPath $extractionDirectory -File -Recurse -Filter '*Magick*.dll')
    if ($nativeCodec.Count -eq 0) {
        throw 'Published app did not extract its bundled native Magick codec into the fresh directory.'
    }
    Write-Host "Published x64 PNG/GIF pixel decode and native single-file extraction passed. Evidence: $smokeRoot"
} finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtractionDirectory
}
