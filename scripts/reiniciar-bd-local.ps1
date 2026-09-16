<#
.SYNOPSIS
    Recrea vacia la base de datos local de desarrollo y la deja al dia de migraciones.

.DESCRIPTION
    Motivo del script: una base local que no se toca durante dias acumula migraciones
    pendientes, y entre ellas puede haber alguna con una comprobacion de una sola vez
    sobre datos historicos (p. ej. 20260828171712_F3cRetiradaClientesSubcontratasLegacy,
    que aborta si encuentra filas legacy sin reconciliar). Esa comprobacion no es un bug:
    hace exactamente lo que debe. Lo que sobra es el coste de diagnosticarlo cada vez a
    mano. Este script asume que los datos locales son de prueba y prescindibles -- nunca
    apuntarlo a una cadena de conexion de staging o produccion.

    No parchea ninguna migracion ni la vuelve permisiva: las migraciones ya aplicadas en
    produccion son un artefacto historico y no se tocan (ver el propio comentario de F3c).
    Recrea la base desde cero y dotnet ef aplica la cadena completa sobre datos que no
    tienen nada que reconciliar -- por eso el gate pasa limpio.

    Antes de soltar la base comprueba que no haya conexiones activas (pg_stat_activity):
    esta maquina corre un Postgres nativo compartido por varios worktrees de este
    repositorio y, en al menos un caso observado, tambien por proyectos ajenos a Hydra
    (rol "jobhunter"). Abortar en vez de forzar evita tirar el trabajo de otra sesion.

.PARAMETER Database
    Nombre de la base a reiniciar. Por defecto "caemanager", el nombre que usa
    ConnectionStrings:CaeManagerDb en appsettings.json.

.PARAMETER PgHost
    Host de PostgreSQL. Por defecto "localhost".

.PARAMETER PgPort
    Puerto de PostgreSQL. Por defecto 5432.

.PARAMETER PgUser
    Rol propietario que ejecuta las migraciones (el mismo que ConnectionStrings:CaeManagerDb).
    Por defecto "postgres".

.PARAMETER PgPassword
    Contrasena del rol. Por defecto "postgres" (el valor de desarrollo en appsettings.json).
    Pasala explicita si tu entorno usa otra.

.PARAMETER SoloMigrar
    No borra nada: solo aplica las migraciones pendientes sobre la base ya existente.
    Utilizalo primero si quieres ver si el problema es simplemente "estaba atrasada" antes
    de decidir recrearla.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\reiniciar-bd-local.ps1
    powershell -ExecutionPolicy Bypass -File scripts\reiniciar-bd-local.ps1 -SoloMigrar
    powershell -ExecutionPolicy Bypass -File scripts\reiniciar-bd-local.ps1 -Database caemanager_dev2
#>
[CmdletBinding()]
param(
    [string]$Database = "caemanager",
    [string]$PgHost = "localhost",
    [int]$PgPort = 5432,
    [string]$PgUser = "postgres",
    [string]$PgPassword = "postgres",
    [switch]$SoloMigrar
)

$ErrorActionPreference = "Stop"

function Escribir($mensaje) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $mensaje"
}

# Localiza las herramientas nativas de PostgreSQL. Prueba el PATH primero (por si
# hay una instalacion via Chocolatey/Scoop) y si no, la ruta tipica del instalador
# oficial en Windows, cogiendo la version mas alta si hay varias.
function Ruta-HerramientaPg([string]$Nombre) {
    $enPath = Get-Command $Nombre -ErrorAction SilentlyContinue
    if ($enPath) { return $enPath.Source }

    $candidatas = Get-ChildItem "C:\Program Files\PostgreSQL\*\bin\$Nombre" -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.Directory.Parent.Name) } -Descending
    if ($candidatas) { return $candidatas[0].FullName }

    throw "No se encuentra $Nombre ni en PATH ni en C:\Program Files\PostgreSQL\*\bin. ?Esta instalado el cliente de PostgreSQL?"
}

$psql = Ruta-HerramientaPg "psql.exe"
$dropdb = Ruta-HerramientaPg "dropdb.exe"
$createdb = Ruta-HerramientaPg "createdb.exe"

$env:PGPASSWORD = $PgPassword
$raiz = Split-Path -Parent $PSScriptRoot
$proyectoMigraciones = Join-Path $raiz "src\CaeManager.Migrations.PostgreSQL"
$proyectoInicio = Join-Path $raiz "src\CaeManager.Web"
$cadenaConexion = "Host=$PgHost;Port=$PgPort;Database=$Database;Username=$PgUser;Password=$PgPassword"

if (-not $SoloMigrar) {
    Escribir "Comprobando conexiones activas a '$Database'..."
    # Sin 2>&1 aqui: psql escribe el aviso de contrasena por stderr aunque termine
    # bien, y con $ErrorActionPreference = "Stop" eso abortaria el script en Windows
    # PowerShell 5.1 aunque el comando haya funcionado.
    $activas = & $psql -U $PgUser -h $PgHost -p $PgPort -d postgres -t -A -c `
        "SELECT count(*) FROM pg_stat_activity WHERE datname = '$Database';"
    if ($LASTEXITCODE -ne 0) { throw "psql fallo comprobando conexiones activas (codigo $LASTEXITCODE)." }

    $numActivas = [int]($activas.Trim())
    if ($numActivas -gt 0) {
        throw "'$Database' tiene $numActivas conexion(es) activa(s) -- probablemente otra sesion o worktree la esta usando. Cierra esa sesion antes de reiniciar, o usa -SoloMigrar si solo quieres ponerla al dia sin borrarla."
    }

    Escribir "Sin conexiones activas. Recreando '$Database' vacia..."
    & $dropdb -U $PgUser -h $PgHost -p $PgPort --if-exists $Database
    if ($LASTEXITCODE -ne 0) { throw "dropdb fallo (codigo $LASTEXITCODE)." }

    & $createdb -U $PgUser -h $PgHost -p $PgPort $Database
    if ($LASTEXITCODE -ne 0) { throw "createdb fallo (codigo $LASTEXITCODE)." }
}

Escribir "Aplicando migraciones sobre '$Database'..."
Push-Location $proyectoInicio
try {
    dotnet ef database update `
        --project $proyectoMigraciones `
        --startup-project $proyectoInicio `
        --connection $cadenaConexion
    if ($LASTEXITCODE -ne 0) { throw "dotnet ef database update fallo (codigo $LASTEXITCODE). Revisa el mensaje: si es una comprobacion de reconciliacion de datos, la base no estaba realmente vacia -- confirma que apuntabas a la base correcta." }
}
finally {
    Pop-Location
}

Escribir "'$Database' al dia. Arranca la app (dotnet run) para que los seeders de demo la pueblen."
