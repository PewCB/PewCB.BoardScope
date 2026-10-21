$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & dotnet run --project tests/BoardScope.Checks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Native checks failed.' }

    foreach ($testFile in Get-ChildItem tests -Filter '*.cjs' | Sort-Object Name) {
        & node $testFile.FullName
        if ($LASTEXITCODE -ne 0) { throw "JavaScript checks failed: $($testFile.Name)" }
    }
} finally {
    Pop-Location
}
