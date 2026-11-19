$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\MediaForge\MediaForge.csproj'
$output = $PSScriptRoot

dotnet publish $project -c Release -r win-x64 --self-contained true -o $output -p:DebugType=None -p:DebugSymbols=false
Write-Host ""
Write-Host "MediaForge build complete." -ForegroundColor Green
Write-Host (Join-Path $output 'MediaForge.exe')
