$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\MediaForge\MediaForge.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained true
Write-Host ""
Write-Host "MediaForge build complete." -ForegroundColor Green
Write-Host (Join-Path $PSScriptRoot 'src\MediaForge\bin\Release\net8.0-windows\win-x64\publish\MediaForge.exe')
