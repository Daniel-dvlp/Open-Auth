# Restablece la contraseña del superusuario "postgres" de un PostgreSQL 18 local (Windows) y prepara las bases de Yuma.
# Ejecutar en PowerShell COMO ADMINISTRADOR:
#   & 'C:\Users\Daniel\Documents\Yuma_Pay\escudo-yuma\ops\postgres\restablecer_clave_postgres.ps1'
# Abre temporalmente el acceso local sin contraseña (solo 127.0.0.1 / ::1) y SIEMPRE lo deja como estaba al terminar.
$ErrorActionPreference = 'Stop'

$servicio = 'postgresql-x64-18'
$bin      = 'C:\Program Files\PostgreSQL\18\bin'
$data     = 'C:\Program Files\PostgreSQL\18\data'
$hba      = Join-Path $data 'pg_hba.conf'
$respaldo = "$hba.respaldo"
$script   = Join-Path $PSScriptRoot 'crear_local.sql'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Abre PowerShell como Administrador y vuelve a ejecutar este script.' }

$clave1 = Read-Host 'Nueva contraseña para postgres' -AsSecureString
$clave2 = Read-Host 'Repítela' -AsSecureString
$plano  = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($clave1))
$plano2 = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($clave2))
if ([string]::IsNullOrEmpty($plano) -or $plano -ne $plano2) { throw 'Las contraseñas no coinciden o están vacías.' }

Copy-Item $hba $respaldo -Force
try {
    $original = Get-Content $hba -Raw
    $temporal = "host all postgres 127.0.0.1/32 trust`r`nhost all postgres ::1/128 trust`r`n" + $original
    Set-Content -Path $hba -Value $temporal -Encoding ascii -NoNewline
    Restart-Service $servicio
    Start-Sleep -Seconds 3

    $env:PGPASSWORD = $null
    $escapada = $plano.Replace("'", "''")
    & "$bin\psql.exe" -h 127.0.0.1 -U postgres -d postgres -w -v ON_ERROR_STOP=1 -c "ALTER USER postgres PASSWORD '$escapada';"
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo cambiar la contraseña.' }
    Write-Host 'Contraseña de postgres actualizada.' -ForegroundColor Green

    & "$bin\psql.exe" -h 127.0.0.1 -U postgres -d postgres -w -f $script
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear el rol o las bases.' }
}
finally {
    Copy-Item $respaldo $hba -Force
    Remove-Item $respaldo -Force
    Restart-Service $servicio
    $plano = $null; $plano2 = $null
    Write-Host 'pg_hba.conf restaurado; servicio reiniciado.' -ForegroundColor Cyan
}
