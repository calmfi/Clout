# Registers the sample function and schedules it to run every 10 seconds.
# Usage: ./scripts/register-sample-function.ps1 [-Api http://localhost:5000]

param(
    [string]$Api = "http://localhost:5000"
)

$ErrorActionPreference = 'Stop'

Write-Host "Building sample function..." -ForegroundColor Cyan
dotnet build ./samples/Sample.Function/Sample.Function.csproj | Out-Null

$dll = Join-Path $PSScriptRoot "..\samples\Sample.Function\bin\Debug\net10.0\Sample.Function.dll"
if (!(Test-Path $dll)) {
    throw "Sample function DLL not found at $dll"
}

Write-Host "Registering and scheduling (*/10 * * * * ?)..." -ForegroundColor Cyan
dotnet run --project ./Clout.Client -- --api $Api functions register $dll Ping dotnet --cron "*/10 * * * * ?"

Write-Host "Done. The functions should run roughly every 10 seconds." -ForegroundColor Green

