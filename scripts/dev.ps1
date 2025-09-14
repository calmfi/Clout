param(
  [string]$Project = "Clout.UI"
)

$ErrorActionPreference = 'Stop'

Write-Host "Starting Tailwind watcher and dotnet watch for $Project..." -ForegroundColor Cyan

$repoRoot = Split-Path -Parent $PSScriptRoot

# Start Tailwind watcher in background
$tailwind = Start-Process -FilePath "npm" -ArgumentList "run","watch:css" -WorkingDirectory $repoRoot -PassThru
Write-Host "Tailwind watch PID: $($tailwind.Id)" -ForegroundColor DarkGray

try {
  dotnet watch --project $Project run
}
finally {
  if ($tailwind -and !$tailwind.HasExited) {
    Write-Host "Stopping Tailwind watcher..." -ForegroundColor DarkGray
    try { $tailwind.Kill() } catch { }
  }
}

