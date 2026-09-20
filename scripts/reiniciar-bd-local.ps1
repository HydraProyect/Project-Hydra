<#
.SYNOPSIS
    Recrea vacia la base de datos local de desarrollo y la deja al dia de migraciones.

.DESCRIPTION
    Motivo del script: una base local que no se toca durante dias acumula migraciones
    pendientes, y entre ellas puede haber alguna con una comprobacion de una sola vez
    sobre datos historicos (p. ej. 20260828171712_F3cRetiradaClientesSubcontratasLegacy,
    que aborta si encuentra filas legacy sin reconciliar). Esa comprobacion no es un bug:
    hace exactamente lo que debe. Lo que sobra es el coste de diagnosticarlo cada vez a
    mano. Este script asume que los datos locales son de prueba y prescindibles.

    Tres guardas lo impiden apuntar a otra cosa que no sea una base local de desarrollo,
    y las tres fallan cerradas (ante la duda, aborta):
      1. El host debe ser localhost, 127.0.0.1 o ::1. No hay parametro para saltarselo.
      2. El nombre de la base debe ser "caemanager" o "caemanager_" seguido de letras
         minusculas, digitos o guion bajo. Asi no se puede borrar "postgres", una base de
         otro proyecto ni una de staging con otro nombre, y el nombre nunca contiene
         caracteres que alteren el SQL en el que se interpola.
      3. Recrear la base exige teclear su nombre (-Confirmar o pregunta interactiva).
         Sin terminal interactiva y sin -Confirmar, aborta. -SoloMigrar no borra nada y
         no pide confirmacion.

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
    Host de PostgreSQL. Solo se admite localhost, 127.0.0.1 o ::1. Por defecto "localhost".

.PARAMETER PgPort
    Puerto de PostgreSQL. Por defecto 5432.

.PARAMETER PgUser
    Rol propietario que ejecuta las migraciones (el mismo que ConnectionStrings:CaeManagerDb).
    Por defecto "postgres".

.PARAMETER PgPassword
    Contrasena del rol. Por defecto "postgres" (el valor de desarrollo en appsettings.json).
    Pasala explicita si tu entorno usa otra.

.PARAMETER Confirmar
    Nombre de la base que se va a borrar, tecleado de nuevo. Debe coincidir con -Database.
    Si no se pasa, el script lo pregunta; sin terminal interactiva, aborta.

.PARAMETER SoloMigrar
    No borra nada: solo aplica las migraciones pendientes sobre la base ya existente.
    Utilizalo primero si quieres ver si el problema es simplemente "estaba atrasada" antes
    de decidir recrearla.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\reiniciar-bd-local.ps1
    powershell -ExecutionPolicy Bypass -File scripts\reiniciar-bd-local.ps1 -SoloMigrar
    powershell -ExecutionPolicy Bypass -File scripts\reiniciar-bd-local.ps1 -Database caemanager_dev2 -Confirmar caemanager_dev2
#>
[CmdletBinding()]
param(
    [string]$Database = "caemanager",
    [string]$PgHost = "localhost",
    [int]$PgPort = 5432,
    [string]$PgUser = "postgres",
    [string]$PgPassword = "postgres",
    [string]$Confirmar = "",
    [switch]$SoloMigrar
)

$ErrorActionPreference = "Stop"

# Guardas de destino. Van antes de cualquier llamada a psql/dropdb: una cadena de conexion
# de staging o produccion no debe llegar a tocarse ni para comprobar conexiones.
if ($PgHost -notin @("localhost", "127.0.0.1", "::1")) {
    throw "Host '$PgHost' rechazado: este script solo actua sobre PostgreSQL local (localhost, 127.0.0.1 o ::1)."
}
# \z y no $: en .NET, $ tambien casa antes de un salto de linea final.
if ($Database -cnotmatch '^caemanager(_[a-z0-9_]+)?\z') {
    throw "Base '$Database' rechazada: solo se admiten 'caemanager' o 'caemanager_<sufijo>' (minusculas, digitos y guion bajo)."
}
# El rol y la contrasena se interpolan en la cadena de conexion de dotnet ef: un ';' en
# cualquiera de los dos permitiria anadir "Host=..." y el ultimo gana, saltandose la guarda.
if ($PgUser -notmatch '^[A-Za-z0-9_]+\z') {
    throw "Rol '$PgUser' rechazado: solo letras, digitos y guion bajo."
}
if ($PgPassword -match '[;\r\n]') {
    throw "Contrasena rechazada: no puede contener ';' ni saltos de linea (romperian la cadena de conexion)."
}

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
    if ($Confirmar -eq "") {
        # Read-Host falla sin terminal interactiva (agentes, CI): en ese caso aborta, que
        # es lo que se quiere -- borrar una base exige que alguien lo haya escrito.
        $Confirmar = Read-Host "Se va a BORRAR la base '$Database' en ${PgHost}:$PgPort. Escribe su nombre para continuar"
    }
    if ($Confirmar -cne $Database) {
        throw "Confirmacion no coincide con '$Database'. No se ha tocado nada."
    }

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
