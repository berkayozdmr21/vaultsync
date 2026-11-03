$ErrorActionPreference='Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet build backend
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
dotnet run --project backend --no-build --urls http://127.0.0.1:9251
