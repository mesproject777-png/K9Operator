$ErrorActionPreference = "Stop"

$port = 4300
$connections = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue

foreach ($connection in $connections) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $($connection.OwningProcess)"
    if ($null -eq $process) {
        continue
    }

    Write-Host "Stopping process $($process.ProcessId) using frontend port $port..."
    Stop-Process -Id $process.ProcessId -Force
}
