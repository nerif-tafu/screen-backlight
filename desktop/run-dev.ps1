$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet run --project .\BacklightStreamer\BacklightStreamer.csproj -c Debug
