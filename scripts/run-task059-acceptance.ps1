$ErrorActionPreference = "Stop"

& dotnet test "$PSScriptRoot\..\tests\Yemekhane.UnitTests\Yemekhane.UnitTests.csproj" `
    --configuration Release `
    --filter "Category=Task059" `
    --logger "console;verbosity=detailed"

exit $LASTEXITCODE
