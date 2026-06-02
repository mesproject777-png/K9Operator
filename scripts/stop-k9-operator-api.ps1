$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiExe = [IO.Path]::GetFullPath((Join-Path $repoRoot "backend\K9Api\bin\Debug\net8.0\K9Api.exe"))

Get-CimInstance Win32_Process |
    Where-Object {
        $_.ExecutablePath -and
        ([IO.Path]::GetFullPath($_.ExecutablePath) -ieq $apiExe)
    } |
    ForEach-Object {
        Write-Host "Stopping existing K9 Operator API process $($_.ProcessId)..."
        Stop-Process -Id $_.ProcessId -Force
    }
