$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot '..\src\BoardScope\wwwroot\vendor'
New-Item -ItemType Directory -Path $target -Force | Out-Null
Invoke-WebRequest -Uri 'https://cdn.jsdelivr.net/npm/openseadragon@6.1.1/build/openseadragon/openseadragon.min.js' -OutFile (Join-Path $target 'openseadragon.min.js')
Invoke-WebRequest -Uri 'https://cdn.jsdelivr.net/npm/openseadragon@6.1.1/LICENSE.txt' -OutFile (Join-Path $target 'LICENSE.txt')
