$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\BoardScope\BoardScope.csproj'
$serviceReady = $false
try { $null = Invoke-RestMethod 'http://127.0.0.1:5274/api/state' -TimeoutSec 2; $serviceReady = $true } catch {}
if (-not $serviceReady) {
    & dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $exe = Join-Path $PSScriptRoot 'src\BoardScope\bin\Release\net10.0-windows\win-x64\BoardScope.exe'
    $content = Join-Path $PSScriptRoot 'src\BoardScope'
    $taskProcess = Start-Process -FilePath $exe -ArgumentList @('--contentRoot', ('"' + $content + '"')) -WorkingDirectory $content -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        try { $null = Invoke-RestMethod 'http://127.0.0.1:5274/api/state' -TimeoutSec 1; $serviceReady = $true; break } catch {}
        if ($taskProcess.HasExited) { throw 'BoardScope exited before it could start.' }
    }
    if (-not $serviceReady) { throw 'BoardScope did not start on port 5274.' }
}
Start-Process 'http://127.0.0.1:5274/'
