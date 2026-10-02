# PostToolUse hook: format an edited .cs file with dotnet format.
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$file = $payload.tool_input.file_path
if (-not $file -or $file -notmatch '\.cs$') { exit 0 }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { exit 0 }

$sln = Join-Path $payload.cwd 'LogPulse.sln'
if (-not (Test-Path $sln)) { exit 0 }

$out = dotnet format $sln --include $file --no-restore 2>&1
if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine(($out | Out-String))
    exit 2
}
exit 0
