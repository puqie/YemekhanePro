$ErrorActionPreference = "Stop"
$previous = $env:YEMEKHANE_LARGE_DATASET
$env:YEMEKHANE_LARGE_DATASET = "1"

try {
    & dotnet test "$PSScriptRoot\..\tests\Yemekhane.PerformanceTests\Yemekhane.PerformanceTests.csproj" `
        --configuration Release `
        --filter "Category=LargeDataset" `
        --logger "console;verbosity=detailed"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    $env:YEMEKHANE_LARGE_DATASET = $previous
}
