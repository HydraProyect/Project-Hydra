<#
.SYNOPSIS
    Preflight de PR: lo que mas rojos da en el CI, en el orden en que conviene verlo, parando en el primer fallo.

.DESCRIPTION
    Se ejecuta en local ANTES de abrir cualquier PR. Medido en las ultimas 60 ejecuciones rojas de CI,
    fallan sobre todo formato, compilacion, tests unitarios y la gobernanza del cuerpo de la PR.
    Pasos, en este orden; el primero que falla corta la ejecucion con un mensaje claro:
      0. Frescura (solo aviso): si la rama no contiene el ultimo origin/main, recomienda integrarlo antes de encolar.
      1. dotnet restore CaeManager.slnx                                   (job "Compilacion y tests unitarios")
      2. dotnet build CaeManager.slnx --no-restore -warnaserror           (los mismos flags que el CI)
      3. dotnet format CaeManager.slnx --no-restore --verify-no-changes   (job "Verificar formato"). Con -Corregir
         se aplica el formato y se vuelve a verificar.
      4. Tests de Domain, Application, Architecture (incluye los ratchets) y Web (bUnit).
      5. Solo con -CuerpoPr: la gobernanza del cuerpo de la PR (ver abajo).
    NUNCA ejecuta IntegrationTests ni E2ETests: necesitan PostgreSQL y navegadores y solo corren en CI. El script
    lo impide de forma explicita: cada `dotnet test` pasa por Assert-ProyectoDeTestPermitido, una lista cerrada
    de cuatro proyectos; cualquier otra ruta (y en particular Integration o E2E) lanza una excepcion.
    (La compilacion de la solucion SI compila esos proyectos, como el CI; compilar no es ejecutar.)

    Gobernanza del cuerpo (-CuerpoPr <fichero.md>): reimplementa en PowerShell, sin llamar a bash, las reglas
    de scripts/verificar-gobernanza-pr.sh, que sigue siendo la AUTORIDAD (lo ejecuta el CI):
      - existe una seccion "## Revision Codex" (o una de las tres variantes que acepta el .sh) con contenido real;
      - esa seccion no lleva marcadores provisionales: "resultado(s) abajo", la palabra "pendiente" (tambien el
        nombre de estado "Pendiente"; "pendientes" no cuenta) ni TODO/TODOs en mayusculas;
      - si el diff contra origin/main toca deploy/bootstrap/roles-de-cluster.sql, existe la seccion
        "## Paso operativo en servidores" y nombra staging y produccion.
    La paridad con el .sh la comprueba scripts/preflight-pr.tests.ps1 -ContraSh, que corre en CI
    (gobernanza-pr-tests.yml) con los mismos casos que verificar-gobernanza-pr.tests.sh. Si el .sh cambia, el
    cambio se replica aqui en el mismo PR o ese test se pone en rojo.
    Las etiquetas y el milestone de la PR no se pueden comprobar antes de crearla: `gh pr create` con
    --label "Type: ..." --label "Priority: ..." --milestone "...".

    Salida: PASA / FALLA / OMITIDO por paso, tiempo total y resumen final; codigo de salida 0 en verde, 1 en rojo,
    2 por uso incorrecto. El log de cada paso queda en %TEMP%\preflight-pr\<worktree>-<fecha>-<PID>\ (carpeta
    propia de cada ejecucion, para poder correr varios worktrees a la vez).
    Para la comprobacion rapida por proyectos tocados y la pista de E2E existe scripts/preflight.ps1; este
    script es el recorrido completo y ordenado previo a la PR.

.PARAMETER Corregir
    Aplica `dotnet format` (en vez de solo verificarlo) y vuelve a verificar. Deja cambios sin commitear.

.PARAMETER CuerpoPr
    Ruta de un fichero (UTF-8) con el cuerpo de la PR. Activa la validacion de gobernanza del cuerpo.

.PARAMETER Autor
    Login de quien abrira la PR. Solo "dependabot[bot]" exime de la seccion de revision (como en el .sh);
    vacio o cualquier otro valor la exige.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\preflight-pr.ps1
    powershell -ExecutionPolicy Bypass -File scripts\preflight-pr.ps1 -Corregir
    powershell -ExecutionPolicy Bypass -File scripts\preflight-pr.ps1 -CuerpoPr $env:TEMP\cuerpo-pr.md
#>
[CmdletBinding()]
param(
    [switch]$Corregir,
    [string]$CuerpoPr = '',
    [string]$Autor = ''
)

# ---------------------------------------------------------------------------------------------------------
# Gobernanza del cuerpo: espejo de scripts/verificar-gobernanza-pr.sh (la autoridad). Se define antes de la
# guarda de dot-sourcing para que scripts/preflight-pr.tests.ps1 pueda cargar las funciones sin ejecutar nada.
# ---------------------------------------------------------------------------------------------------------

function Split-Lineas([string]$Texto) {
    # Los registros que ve awk: separados por LF, sin un registro vacio extra tras el ultimo LF.
    if ([string]::IsNullOrEmpty($Texto)) { return @() }
    $partes = $Texto -split "`n"
    if ($Texto.EndsWith("`n")) { $partes = $partes[0..($partes.Length - 2)] }
    return $partes
}

function Get-SeccionCuerpo([string[]]$Lineas, [string]$Titulo) {
    # extraer_seccion del .sh: desde la linea "## <titulo>" (comparada sin espacios finales y con igualdad exacta)
    # hasta el siguiente encabezado de nivel 2 ("##" solo, o "##" + espacio/tabulador) o el final.
    $encontrada = $false
    $acum = New-Object System.Collections.Generic.List[string]
    foreach ($l in $Lineas) {
        $linea = $l -replace '[ \t\r\n\f\v]+$', ''
        if (-not $encontrada -and $linea -ceq $Titulo) { $encontrada = $true; continue }
        if ($encontrada -and $l -match '^##([ \t]|$)') { break }
        if ($encontrada) { $acum.Add($l) }
    }
    # $(...) de bash quita los saltos de linea finales.
    return (($acum.ToArray() -join "`n").TrimEnd("`n"))
}

function Get-SeccionRevision([string[]]$Lineas) {
    # extraer_seccion_variantes del .sh: la primera variante del titulo con contenido no vacio.
    $o = [string][char]0x00F3
    $titulos = @("## Revisi${o}n Codex", "## Revisi${o}n de Codex", '## Revision Codex', '## Revision de Codex')
    foreach ($t in $titulos) {
        $r = Get-SeccionCuerpo $Lineas $t
        if ($r -ne '') { return $r }
    }
    return ''
}

function Test-SeccionConContenido([string]$Seccion) {
    # seccion_no_vacia del .sh: quita comentarios HTML de una sola linea y exige algun caracter visible.
    foreach ($l in @(Split-Lineas $Seccion)) {
        $sin = $l -replace '<!--.*-->', ''
        if ($sin -match '[^ \t\r\n\f\v]') { return $true }
    }
    return $false
}

function Test-LineaCoincide([string]$Texto, [string]$Patron, [bool]$DistingueMayusculas) {
    # grep -E sobre texto de varias lineas: el patron se aplica linea a linea, nunca a traves de saltos de linea.
    foreach ($l in @(Split-Lineas $Texto)) {
        if ($DistingueMayusculas) { if ($l -cmatch $Patron) { return $true } }
        else { if ($l -match $Patron) { return $true } }
    }
    return $false
}

function Invoke-GobernanzaCuerpoPr {
    # Devuelve @{ Problemas; Lineas } con el mismo esquema de salida que el .sh: lineas "OK ..." / "PROBLEMA ...",
    # una linea vacia y "Problemas: N". La prosa va sin tildes (script en ASCII); la paridad con el .sh se
    # comprueba por veredicto, etiquetas y contador, no por texto.
    param([string]$Cuerpo, [string[]]$Ficheros = @(), [string]$Autor = '')

    $ficheroRoles = 'deploy/bootstrap/roles-de-cluster.sql'
    $limitePaginacion = 3000
    $lineas = New-Object System.Collections.Generic.List[string]
    $problemas = 0
    $lineasCuerpo = @(Split-Lineas $Cuerpo)
    $numFicheros = @($Ficheros | Where-Object { $_ -ne '' }).Count

    # Regla 1: paso operativo de roles de cluster.
    if ($numFicheros -ge $limitePaginacion) {
        $lineas.Add("PROBLEMA  Esta PR toca $numFicheros ficheros o mas: la API de GitHub trunca /pulls/N/files en $limitePaginacion y no se puede confirmar si toca o no $ficheroRoles. Revisar el diff a mano.")
        $problemas++
    }
    elseif ($Ficheros -ccontains $ficheroRoles) {
        $seccion = Get-SeccionCuerpo $lineasCuerpo '## Paso operativo en servidores'
        if (-not (Test-SeccionConContenido $seccion)) {
            $lineas.Add("PROBLEMA  Esta PR toca $ficheroRoles pero el cuerpo no tiene la seccion '## Paso operativo en servidores' (o esta vacia): ningun adaptador de despliegue ejecuta roles-de-cluster.sql, el paso manual hay que declararlo.")
            $problemas++
        }
        elseif ($seccion -notmatch 'staging') {
            $lineas.Add("PROBLEMA  La seccion 'Paso operativo en servidores' no menciona 'staging'.")
            $problemas++
        }
        elseif ($seccion -notmatch 'producci') {
            $lineas.Add("PROBLEMA  La seccion 'Paso operativo en servidores' no menciona 'produccion'.")
            $problemas++
        }
        else {
            $lineas.Add('OK        Paso operativo de roles de cluster: seccion presente, nombra staging y produccion.')
        }
    }
    else {
        $lineas.Add("OK        No toca $ficheroRoles : la regla no aplica.")
    }

    # Regla 2: revision Codex en el cuerpo.
    if ($Autor -ceq 'dependabot[bot]') {
        $lineas.Add('OK        Autor dependabot[bot]: regla de revision Codex no aplica.')
    }
    else {
        $seccion = Get-SeccionRevision $lineasCuerpo
        if (-not (Test-SeccionConContenido $seccion)) {
            $lineas.Add("PROBLEMA  Falta la seccion '## Revision Codex' en el cuerpo (o esta vacia): al menos una linea de hallazgos aceptados o rechazados, o 'sin hallazgos'.")
            $problemas++
        }
        elseif (Test-LineaCoincide $seccion 'resultados?[\s\-]+abajo' $false) {
            $lineas.Add("PROBLEMA  La seccion 'Revision Codex' tiene el marcador provisional 'resultado abajo'.")
            $problemas++
        }
        elseif (Test-LineaCoincide $seccion '\bpendiente\b' $false) {
            $lineas.Add("PROBLEMA  La seccion 'Revision Codex' tiene el marcador provisional 'pendiente' (tampoco vale el nombre de estado 'Pendiente').")
            $problemas++
        }
        elseif (Test-LineaCoincide $seccion '\bTODOs?\b' $true) {
            $lineas.Add("PROBLEMA  La seccion 'Revision Codex' tiene el marcador provisional 'TODO'.")
            $problemas++
        }
        else {
            $lineas.Add("OK        Seccion 'Revision Codex' presente, con contenido y sin marcadores provisionales.")
        }
    }

    $lineas.Add('')
    $lineas.Add("Problemas: $problemas")
    return [pscustomobject]@{ Problemas = $problemas; Lineas = $lineas.ToArray() }
}

function Assert-ProyectoDeTestPermitido([string]$Ruta) {
    # Lista cerrada: este preflight solo ejecuta estos cuatro proyectos. IntegrationTests y E2ETests necesitan
    # PostgreSQL / navegadores y solo corren en CI; no hay bandera para saltarse esta guarda.
    $r = $Ruta.Replace('\', '/').TrimEnd('/')
    if ($r -match '(?i)integration|e2e') {
        throw "preflight-pr: '$Ruta' es un proyecto de integracion o E2E; esos tests solo corren en CI y este script nunca los ejecuta."
    }
    if ($r -notmatch '^tests/CaeManager\.(Domain|Application|Architecture|Web)\.Tests$') {
        throw "preflight-pr: '$Ruta' no esta en la lista de proyectos de test permitidos (Domain, Application, Architecture, Web)."
    }
}

# Carga como biblioteca (. .\scripts\preflight-pr.ps1): solo quedan definidas las funciones de arriba.
if ($MyInvocation.InvocationName -eq '.') { return }

# ---------------------------------------------------------------------------------------------------------
# Ejecucion
# ---------------------------------------------------------------------------------------------------------

$ErrorActionPreference = 'Continue'
Set-Location (Split-Path -Parent $PSScriptRoot)
$raiz = (Get-Location).Path
$reloj = [Diagnostics.Stopwatch]::StartNew()

if ($CuerpoPr -ne '' -and -not (Test-Path -LiteralPath $CuerpoPr -PathType Leaf)) {
    Write-Host "USO: -CuerpoPr '$CuerpoPr' no es un fichero." -ForegroundColor Red
    exit 2
}

$raizLogs = Join-Path $env:TEMP 'preflight-pr'
$carpetaWorktree = (Split-Path -Leaf $raiz) -replace '[^\w.-]+', '-'
$logs = Join-Path $raizLogs ('{0}-{1}-{2}' -f $carpetaWorktree, (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID)
New-Item -ItemType Directory -Force $logs | Out-Null
# Limpieza de ejecuciones viejas (>7 dias); nunca las recientes, que pueden estar en uso.
Get-ChildItem -LiteralPath $raizLogs -ErrorAction SilentlyContinue |
    Where-Object { $_.PSIsContainer -and $_.LastWriteTime -lt (Get-Date).AddDays(-7) } |
    ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop } catch {} }

$resultados = New-Object System.Collections.ArrayList
$avisos = New-Object System.Collections.ArrayList

function Write-Resumen([int]$Codigo, [string]$Causa) {
    Write-Host "`n=== Resumen preflight-pr ==="
    $resultados | ForEach-Object { '{0,-40} {1,-10} {2,6} s' -f $_.Paso, $_.Estado, $_.Segundos } | Write-Host
    Write-Host ('Tiempo total: {0:mm\:ss} ({1} s)' -f $reloj.Elapsed, [int]$reloj.Elapsed.TotalSeconds)
    Write-Host "Logs de esta ejecucion: $logs"
    foreach ($a in $avisos) { Write-Host "AVISO: $a" -ForegroundColor Yellow }
    if ($Codigo -eq 0) { Write-Host "`nPREFLIGHT-PR: PASA" -ForegroundColor Green }
    else { Write-Host "`nPREFLIGHT-PR: FALLA - $Causa" -ForegroundColor Red }
}

function Stop-Con([string]$Causa) {
    Write-Resumen 1 $Causa
    exit 1
}

function Invoke-Paso([string]$Nombre, [string]$Comando, [string]$Pista = '') {
    # El veredicto es el codigo de salida de ESTE proceso (cmd.exe /s /c con su redireccion al log propio),
    # nunca lo que diga el log. cmd.exe y WaitForExit en vez de Start-Process -Wait: este ultimo espera tambien a
    # los procesos hijos que dejan los servidores de MSBuild y se quedaria colgado.
    $log = Join-Path $logs (($Nombre -replace '[^\w]+', '-') + '.log')
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-Host ">>> $Nombre" -ForegroundColor Cyan
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = 'cmd.exe'
    $psi.Arguments = "/d /s /c `"$Comando > `"$log`" 2>&1`""
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $raiz
    $proc = [Diagnostics.Process]::Start($psi)
    $proc.WaitForExit()
    $codigo = $proc.ExitCode
    $proc.Dispose()
    $seg = [int]$sw.Elapsed.TotalSeconds
    if ($codigo -eq 0) {
        [void]$resultados.Add([pscustomobject]@{ Paso = $Nombre; Estado = 'PASA'; Segundos = $seg })
        Write-Host "OK  $Nombre ($seg s)" -ForegroundColor Green
        return
    }
    [void]$resultados.Add([pscustomobject]@{ Paso = $Nombre; Estado = 'FALLA'; Segundos = $seg })
    Write-Host "--- $Nombre : FALLA (codigo de salida $codigo)" -ForegroundColor Red
    Write-Host "    log completo: $log"
    if (Test-Path -LiteralPath $log) {
        Get-Content -LiteralPath $log | Select-String -Pattern 'error|Failed|Con error' |
            ForEach-Object { $_.Line.Trim() } | Select-Object -Unique -First 25 | ForEach-Object { Write-Host "    $_" }
    }
    if ($Pista -ne '') { Write-Host "    $Pista" -ForegroundColor Yellow }
    Stop-Con "$Nombre (codigo $codigo)"
}

function Add-Omitido([string]$Nombre) {
    [void]$resultados.Add([pscustomobject]@{ Paso = $Nombre; Estado = 'OMITIDO'; Segundos = 0 })
}

# 0. Frescura respecto a origin/main (solo aviso: no corta).
git fetch origin main --quiet 2>$null
git rev-parse --verify --quiet origin/main 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    [void]$avisos.Add('no existe origin/main en este clon: no se puede comprobar si la rama esta al dia ni calcular el diff del cuerpo.')
}
else {
    git merge-base --is-ancestor origin/main HEAD 2>$null
    if ($LASTEXITCODE -ne 0) {
        $atras = (git rev-list --count HEAD..origin/main 2>$null)
        $msg = "la rama NO contiene el ultimo origin/main (le faltan $atras commits). Integralo antes de encolar: git merge origin/main (o git rebase origin/main si la rama aun no esta publicada) y repite el preflight."
        [void]$avisos.Add($msg)
        Write-Host "AVISO: $msg" -ForegroundColor Yellow
    }
    else { Write-Host 'OK  La rama contiene el ultimo origin/main.' -ForegroundColor Green }
}

# 1-2. Restaurar y compilar toda la solucion (compila tambien Integration y E2E, sin ejecutarlos).
Invoke-Paso 'Restaurar dependencias' 'dotnet restore CaeManager.slnx'
Invoke-Paso 'Compilar (warnings como errores)' 'dotnet build CaeManager.slnx --no-restore -warnaserror -nodeReuse:false' `
    'Corrige el primer error de compilacion; el CI usa -warnaserror: un warning tambien tumba.'

# 3. Formato, exactamente como el job "Verificar formato".
if ($Corregir) {
    Invoke-Paso 'Formato: aplicar (dotnet format)' 'dotnet format CaeManager.slnx --no-restore'
    [void]$avisos.Add('-Corregir ha aplicado el formato: hay cambios sin commitear, revisalos con git diff y anadelos al commit.')
}
Invoke-Paso 'Formato (verify-no-changes)' 'dotnet format CaeManager.slnx --no-restore --verify-no-changes' `
    'Ejecuta de nuevo con -Corregir para aplicar el formato, revisa el diff y commitea.'

# 4. Tests unitarios (lista cerrada; ver Assert-ProyectoDeTestPermitido).
foreach ($par in @(
        @('Tests de Domain', 'tests/CaeManager.Domain.Tests'),
        @('Tests de Application', 'tests/CaeManager.Application.Tests'),
        @('Tests de Architecture (ratchets)', 'tests/CaeManager.Architecture.Tests'),
        @('Tests de Web (bUnit)', 'tests/CaeManager.Web.Tests'))) {
    Assert-ProyectoDeTestPermitido $par[1]
    Invoke-Paso $par[0] ("dotnet test {0} --no-restore -nodeReuse:false" -f $par[1])
}

# 5. Gobernanza del cuerpo de la PR.
if ($CuerpoPr -ne '') {
    $nombre = 'Gobernanza del cuerpo de la PR'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-Host ">>> $nombre" -ForegroundColor Cyan
    $cuerpo = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $CuerpoPr).Path, (New-Object Text.UTF8Encoding($false)))
    # --no-renames: un renombrado aparece como baja del nombre viejo y alta del nuevo, para ver ambas rutas
    # (como el .sh, que recibe filename y previous_filename).
    $tocados = @(git diff --name-only --no-renames origin/main...HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) {
        [void]$resultados.Add([pscustomobject]@{ Paso = $nombre; Estado = 'FALLA'; Segundos = 0 })
        Write-Host 'No se pudo calcular el diff contra origin/main (git fetch origin main).' -ForegroundColor Red
        Stop-Con "$nombre (sin origin/main)"
    }
    $r = Invoke-GobernanzaCuerpoPr -Cuerpo $cuerpo -Ficheros $tocados -Autor $Autor
    $r.Lineas | ForEach-Object { Write-Host $_ }
    $seg = [int]$sw.Elapsed.TotalSeconds
    if ($r.Problemas -gt 0) {
        [void]$resultados.Add([pscustomobject]@{ Paso = $nombre; Estado = 'FALLA'; Segundos = $seg })
        Stop-Con "$nombre ($($r.Problemas) problema/s)"
    }
    [void]$resultados.Add([pscustomobject]@{ Paso = $nombre; Estado = 'PASA'; Segundos = $seg })
}
else {
    Add-Omitido 'Gobernanza del cuerpo de la PR'
    Write-Host 'Sin -CuerpoPr: la gobernanza del cuerpo no se ha comprobado.' -ForegroundColor Yellow
}

Write-Resumen 0 ''
exit 0
