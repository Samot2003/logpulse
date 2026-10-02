# Stop hook: run the unit tests before Claude finishes; block the stop if they fail.
# Integration tests (Testcontainers) are left to CI and explicit runs because they need Docker.
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
if ($payload.stop_hook_active) { exit 0 }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { exit 0 }

$sln = Join-Path $payload.cwd 'LogPulse.sln'
if (-not (Test-Path $sln)) { exit 0 }

$out = dotnet test $sln --nologo --verbosity quiet --filter "Category!=Integration" 2>&1
if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine(($out | Select-Object -Last 40 | Out-String))
    exit 2
}
exit 0
