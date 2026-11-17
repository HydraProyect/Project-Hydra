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

    Plaza de compilacion (solo en la maquina de desarrollo, nunca en CI): si existe el modulo
    %USERPROFILE%\.claude\scripts\PlazaCompilacion.ps1 (o el que diga HYDRA_PLAZA_COMPILACION) y no hay CI
    (GITHUB_ACTIONS=true o CI), los pasos 1 a 4 se hacen con una de las 4 plazas de la maquina (mutex
    Global\HydraCompilacion-1..4) y con -maxcpucount:4 en restore, build y test. Los procesos van en prioridad
    BelowNormal solo si HYDRA_BLD_PRIORIDAD_BAJA=1 (transicion: mientras haya ramas con el preflight antiguo,
    que corre en Normal, BelowNormal dejaria sin CPU a quien lo usa; por defecto, Normal). Imprime
    PLAZA-COMPILACION n/4, o ESPERANDO-PLAZA-COMPILACION mientras espera. Si no hay plaza libre, el arbol esta
    limpio, HEAD esta empujado y la Dell tiene plaza, la compilacion con -warnaserror se hace alli con
    bld -SoloDell mientras se espera; restauracion, formato y tests esperan siempre plaza local. Si la Dell
    falla al compilar (error de codigo en HEAD), el preflight falla; si su resultado no vale (infraestructura
    de la Dell), se compila aqui.
    Sin el modulo, o en CI, el preflight se comporta exactamente como antes.

    Gobernanza del cuerpo (-CuerpoPr <fichero>): reimplementa en PowerShell, sin llamar a bash, las reglas
    de scripts/verificar-gobernanza-pr.sh, que sigue siendo la AUTORIDAD (lo ejecuta el CI):
      - existe una seccion "## Revision Codex" (o una de las tres variantes que acepta el .sh) con contenido real;
      - esa seccion lleva las lineas "Revisor:" (revisor-puente o Codex), "Refutacion:" con contenido y el veredicto
        ("sin hallazgos" o "aceptados N, rechazados M"), como el .sh;
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
    powershell -ExecutionPolicy Bypass -File scripts\preflight-pr.ps1 -CuerpoPr $env:TEMP\cuerpo-pr.txt
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
        # Sin comentarios HTML de una sola linea (SECCION_LIMPIA del .sh).
        $limpia = ((@(Split-Lineas $seccion) | ForEach-Object { $_ -replace '<!--[^>]*-->', '' }) -join "`n")
        if (-not (Test-SeccionConContenido $seccion)) {
            $lineas.Add("PROBLEMA  Falta la seccion '## Revision Codex' en el cuerpo (o esta vacia): lineas 'Revisor:', 'Refutacion:' y el veredicto ('sin hallazgos' o 'aceptados N, rechazados M').")
            $problemas++
        }
        elseif (Test-LineaCoincide $seccion 'resultados?[ \t\r\f\v\-]+abajo' $false) {
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
        # Espacio = clase ASCII explicita (como el [[:space:]] del .sh): `\s` de .NET casaria ademas con NBSP y
        # compania, y el veredicto divergiria del .sh. Mismos patrones que el .sh, sin tildes en la prosa.
        elseif (-not (Test-LineaCoincide $limpia '^[ \t\r\f\v>*_0-9.-]*Revisor[*_]*[ \t\r\f\v]*:[*_]*[ \t\r\f\v]*[`*_]*(revisor-puente|codex)([ \t\r\f\v:;,.()*_`]|$)' $false)) {
            $lineas.Add("PROBLEMA  La seccion 'Revision Codex' no nombra al revisor: falta una linea 'Revisor: revisor-puente' o 'Revisor: Codex'.")
            $problemas++
        }
        elseif (-not (Test-LineaCoincide $limpia '^[ \t\r\f\v>*_0-9.-]*(intentos[ \t\r\f\v]+de[ \t\r\f\v]+)?refutaci[^: \t\r\f\v]{1,3}n[*_]*[ \t\r\f\v]*:[*_]*[ \t\r\f\v]*[^ \t\r\f\v*_]' $false)) {
            $lineas.Add("PROBLEMA  La seccion 'Revision Codex' no dice que se intento refutar: falta una linea 'Refutacion: <que se intento romper>' con contenido.")
            $problemas++
        }
        elseif (-not (Test-LineaCoincide $limpia 'sin[ \t\r\f\v]+hallazgos|aceptad[oa]s?[^0-9]*[0-9]+.*rechazad[oa]s?[^0-9]*[0-9]+|rechazad[oa]s?[^0-9]*[0-9]+.*aceptad[oa]s?[^0-9]*[0-9]+' $false)) {
            $lineas.Add("PROBLEMA  La seccion 'Revision Codex' no da el veredicto contable: falta 'sin hallazgos' o 'aceptados N, rechazados M' (con numeros, en una sola linea).")
            $problemas++
        }
        else {
            $lineas.Add("OK        Seccion 'Revision Codex' presente, con revisor, refutacion y veredicto, y sin marcadores provisionales.")
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

function Assert-ComandoPermitido([string]$Comando) {
    # Guarda sobre el COMANDO que de verdad se va a ejecutar (todo pasa por Invoke-Paso), no sobre un argumento
    # del llamador: nada que mencione Integration o E2E, y todo `dotnet test` apunta a uno de los cuatro proyectos.
    if ($Comando -match '(?i)integration|e2e') {
        throw "preflight-pr: el comando '$Comando' menciona un proyecto de integracion o E2E; esos tests solo corren en CI."
    }
    if ($Comando -match '(?i)\bdotnet\s+(test|vstest|run|exec)\b') {
        $m = [regex]::Match($Comando, '(?i)^\s*dotnet\s+test\s+("?)([^"\s]+)\1(\s|$)')
        if (-not $m.Success) { throw "preflight-pr: el comando '$Comando' no es un 'dotnet test <proyecto>' de la lista permitida." }
        Assert-ProyectoDeTestPermitido $m.Groups[2].Value
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

# Plaza de compilacion de maquina (solo en la maquina de desarrollo). El modulo vive fuera del repositorio
# (%USERPROFILE%\.claude\scripts\PlazaCompilacion.ps1; HYDRA_PLAZA_COMPILACION lo sustituye). Con el modulo:
# como mucho 4 preflights o bld compilando a la vez en la maquina, cada uno con -maxcpucount:4. Sin el modulo,
# o en CI, todo sigue como antes.
# Prioridad BelowNormal de los procesos de compilacion y test: solo con HYDRA_BLD_PRIORIDAD_BAJA=1, la misma
# variable que usa bld (Invoke-ParallelBuild.ps1). Transicion (2026-11-17): mientras queden ramas con el
# preflight antiguo, que corre en Normal, BelowNormal deja sin CPU a quien lo usa (medido: pasos ficticios de
# 18 s a 60 s); por defecto, Normal. Las plazas y -maxcpucount:4 van siempre.
$enCi = ($env:GITHUB_ACTIONS -eq 'true') -or (-not [string]::IsNullOrEmpty($env:CI) -and $env:CI -notin @('false', '0'))
$modoPlaza = $false
$plaza = $null
$remoto = $null
$dellFalla = $false
$moduloPlazas = ''
if (-not $enCi) {
    $moduloPlazas = $env:HYDRA_PLAZA_COMPILACION
    if ([string]::IsNullOrEmpty($moduloPlazas) -and $env:USERPROFILE) {
        $moduloPlazas = Join-Path $env:USERPROFILE '.claude\scripts\PlazaCompilacion.ps1'
    }
    if ($moduloPlazas -and (Test-Path -LiteralPath $moduloPlazas -PathType Leaf)) {
        try { . $moduloPlazas; $modoPlaza = $true }
        catch { [void]$avisos.Add("no se pudo cargar $moduloPlazas ($($_.Exception.Message)): se compila sin plaza.") }
    }
}
$mcpu = if ($modoPlaza) { ' -maxcpucount:4' } else { '' }
$prioridadBaja = $modoPlaza -and ($env:HYDRA_BLD_PRIORIDAD_BAJA -eq '1')

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
    if ($plaza) { Exit-PlazaCompilacion $plaza }
    # Un fallo local con la compilacion de la Dell aun en marcha: se corta bld (y su arbol de procesos) para
    # que no retenga una plaza de la Dell hasta acabar.
    if ($remoto -and $remoto.Proc -and -not $remoto.Proc.HasExited) {
        & taskkill.exe /T /F /PID $remoto.Proc.Id 2>$null | Out-Null
    }
    Write-Resumen 1 $Causa
    exit 1
}

function Invoke-Paso([string]$Nombre, [string]$Comando, [string]$Pista = '', [switch]$EsTest) {
    # El veredicto es el codigo de salida de ESTE proceso (cmd.exe /s /c con su redireccion al log propio);
    # el log solo se lee para mostrar errores y, en los pasos de tests, para exigir que corrieran pruebas.
    # cmd.exe y WaitForExit en vez de Start-Process -Wait: este ultimo espera tambien a
    # los procesos hijos que dejan los servidores de MSBuild y se quedaria colgado.
    Assert-ComandoPermitido $Comando
    $log = Join-Path $logs (($Nombre -replace '[^\w]+', '-') + '.log')
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-Host ">>> $Nombre" -ForegroundColor Cyan
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = 'cmd.exe'
    $psi.Arguments = "/d /s /c `"$Comando > `"$log`" 2>&1`""
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $raiz
    # Con HYDRA_BLD_PRIORIDAD_BAJA=1: el proceso (y cmd -> dotnet -> nodos MSBuild y testhost) nace en BelowNormal.
    $proc = if ($prioridadBaja) { Start-ProcesoPrioridadBaja $psi } else { [Diagnostics.Process]::Start($psi) }
    $proc.WaitForExit()
    $codigo = $proc.ExitCode
    $proc.Dispose()
    $seg = [int]$sw.Elapsed.TotalSeconds
    if ($codigo -eq 0 -and $EsTest) {
        # Un `dotnet test` sin pruebas descubiertas sale con 0: sin esto seria un verde falso (mismo control que gate.sh).
        $total = 0; $vistos = 0
        foreach ($m in (Select-String -LiteralPath $log -Pattern 'Total( de pruebas)?:\s*(\d+)')) {
            $total += [int]$m.Matches[0].Groups[2].Value; $vistos++
        }
        if ($vistos -eq 0) { $codigo = 1; Write-Host "    el resultado no trae el resumen de pruebas: no se sabe cuantas corrieron" -ForegroundColor Red }
        elseif ($total -eq 0) { $codigo = 1; Write-Host "    0 pruebas ejecutadas (descubrimiento roto): un verde sin pruebas no cuenta" -ForegroundColor Red }
        else { $Nombre = "$Nombre [$total pruebas]" }
    }
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

$nombreCompilar = 'Compilar (warnings como errores)'
$pistaCompilar = 'Corrige el primer error de compilacion; el CI usa -warnaserror: un warning tambien tumba.'
$ordenCompilar = 'dotnet build CaeManager.slnx --no-restore -warnaserror -nodeReuse:false'

function Start-CompilacionEnDell {
    # Salto a la Dell cuando no hay plaza local: SOLO la compilacion con warnings como errores, con
    # `bld -SoloDell` (el Dell compila origin/<rama>, por eso exige el arbol limpio y HEAD empujado).
    # Devuelve un objeto con Motivo no vacio si no es viable; entonces se espera plaza local para todo.
    $bld = Join-Path (Split-Path -Parent $moduloPlazas) 'bld.ps1'
    if (-not (Test-Path -LiteralPath $bld -PathType Leaf)) { return [pscustomobject]@{ Motivo = "no existe $bld" } }
    $rama = (git symbolic-ref --quiet --short HEAD 2>$null)
    if (-not $rama) { return [pscustomobject]@{ Motivo = 'HEAD no esta en una rama' } }
    if (@(git status --porcelain 2>$null).Count -gt 0) { return [pscustomobject]@{ Motivo = 'hay cambios sin commitear (la Dell compila lo empujado)' } }
    $head = (git rev-parse HEAD 2>$null)
    $lr = (git ls-remote origin "refs/heads/$rama" 2>$null)
    $remota = if ($lr) { ("$lr" -split '\s+')[0] } else { '' }
    if ($remota -ne $head) { return [pscustomobject]@{ Motivo = "origin/$rama no es HEAD (empuja la rama)" } }
    if (-not (Test-DellAlcanzable)) { return [pscustomobject]@{ Motivo = 'la Dell no responde por ssh' } }
    $pd = Enter-PlazaCompilacion -Plazas 0 -PrefijoAlternativo $script:PlazaPrefijoDell -PlazasAlternativas 2 -NoEsperar -Silencioso
    if (-not $pd) { return [pscustomobject]@{ Motivo = 'las 2 plazas de la Dell estan ocupadas' } }
    Exit-PlazaCompilacion $pd   # la toma bld en su propio proceso
    $ordenFile = Join-Path $logs 'orden-dell.txt'
    # Restaurar aparte y compilar con --no-restore, como el CI (con restauracion implicita, -warnaserror
    # convertiria tambien en error los avisos NU de NuGet).
    $orden = "dotnet restore CaeManager.slnx; if (`$LASTEXITCODE -eq 0) { $ordenCompilar }"
    [IO.File]::WriteAllText($ordenFile, $orden, (New-Object Text.UTF8Encoding($false)))
    $log = Join-Path $logs 'Compilar-en-la-Dell.log'
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = 'cmd.exe'
    $psi.Arguments = "/d /s /c `"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$bld`" -Branch `"$rama`" -SoloDell -CommandFile `"$ordenFile`" -RepoRoot `"$raiz`" > `"$log`" 2>&1`""
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $raiz
    Write-Host ">>> $nombreCompilar en la Dell ($rama @ $($head.Substring(0, 9))), en segundo plano" -ForegroundColor Cyan
    $proc = if ($prioridadBaja) { Start-ProcesoPrioridadBaja $psi } else { [Diagnostics.Process]::Start($psi) }
    return [pscustomobject]@{ Motivo = ''; Proc = $proc; Log = $log; Sha = $head; Reloj = [Diagnostics.Stopwatch]::StartNew() }
}

function Get-ResultadoDell($Remoto) {
    # 'pasa' | 'falla' (error real de compilacion en la Dell, en HEAD) | 'invalido' (infraestructura: no cuenta,
    # se compila en local). Lee la salida de bld.ps1 (Format-List Branch, Dest, Sha, ExitCode, Seconds).
    $Remoto.Proc.WaitForExit()
    $texto = if (Test-Path -LiteralPath $Remoto.Log) { [IO.File]::ReadAllText($Remoto.Log) } else { '' }
    $dest = if ($texto -match '(?m)^\s*Dest\s*:\s*(\S+)') { $Matches[1] } else { '' }
    $sha = if ($texto -match '(?m)^\s*Sha\s*:\s*([0-9a-f]{40})') { $Matches[1] } else { '' }
    $cod = if ($texto -match '(?m)^\s*ExitCode\s*:\s*(-?\d+)') { [int]$Matches[1] } else { $null }
    if ($dest -ne 'dell' -or $sha -ne $Remoto.Sha -or $null -eq $cod) {
        return "invalido (destino '$dest', SHA '$sha', codigo '$cod')"
    }
    # El arbol pudo cambiar durante la espera: el veredicto de la Dell solo vale para el HEAD limpio que compilo.
    if ((git rev-parse HEAD 2>$null) -ne $Remoto.Sha -or @(git status --porcelain 2>$null).Count -gt 0) {
        return 'invalido (HEAD o el arbol de trabajo cambiaron durante la espera)'
    }
    if ($cod -eq 0) { return 'pasa' }
    # Solo un error de codigo (compilador o analizador) es un fallo de la rama. Los de NuGet, MSBuild o disco
    # (NU1301, MSB3027...) son de la Dell: no valen y se compila en local, que dara el veredicto real.
    # CS2012 y CS0016 (no se puede escribir la salida: bloqueo o disco de la Dell) tampoco son de la rama.
    $deCodigo = [regex]::Matches($texto, '(?<![\w])error (CS|CA|IDE|RZ|BL|SYSLIB|ASP|xUnit|NU)(\d+)') |
        Where-Object { $c = $_.Groups[1].Value + $_.Groups[2].Value; ($c -cnotin @('CS2012', 'CS0016')) -and ($_.Groups[1].Value -cne 'NU' -or $c -cin @('NU1605', 'NU1608')) }
    if (@($deCodigo).Count -gt 0) { return 'falla' }
    return "invalido (codigo $cod sin error de compilacion de la rama)"
}

function Close-CompilacionEnDell($Remoto) {
    # Veredicto del paso de compilacion hecho en la Dell. 'falla' corta como un fallo local; 'invalido' compila
    # aqui (con la plaza ya tomada) ANTES de los tests: compilar despues con -warnaserror no veria los warnings
    # de los proyectos que los tests ya hubieran dejado al dia. (La compilacion incremental del worktree
    # reutilizado de la Dell tiene el mismo limite que la local de siempre: un warning en un proyecto que ya
    # estaba al dia no se vuelve a emitir.)
    # Si la espera ya se corto por un 'falla', ese es el veredicto: reevaluarlo podria dar 'invalido' (el arbol
    # cambio en el intervalo) y compilar aqui sin plaza y antes de restaurar.
    $r = if ($script:dellFalla) { 'falla' } else { Get-ResultadoDell $Remoto }
    $seg = [int]$Remoto.Reloj.Elapsed.TotalSeconds
    $paso = "$nombreCompilar [Dell]"
    if ($r -eq 'pasa') {
        [void]$resultados.Add([pscustomobject]@{ Paso = $paso; Estado = 'PASA'; Segundos = $seg })
        Write-Host "OK  $paso ($seg s; log: $($Remoto.Log))" -ForegroundColor Green
        return
    }
    if ($r -eq 'falla') {
        [void]$resultados.Add([pscustomobject]@{ Paso = $paso; Estado = 'FALLA'; Segundos = $seg })
        Write-Host "--- $paso : FALLA" -ForegroundColor Red
        Write-Host "    log completo: $($Remoto.Log)"
        Get-Content -LiteralPath $Remoto.Log | Select-String -Pattern 'error|Failed|Con error' |
            ForEach-Object { $_.Line.Trim() } | Select-Object -Unique -First 25 | ForEach-Object { Write-Host "    $_" }
        Write-Host "    $pistaCompilar" -ForegroundColor Yellow
        Stop-Con "$paso (en la Dell)"
    }
    [void]$avisos.Add("la compilacion en la Dell no vale ($r; log: $($Remoto.Log)): se ha compilado en local.")
    Write-Host "AVISO: la compilacion en la Dell no vale ($r); se compila en local." -ForegroundColor Yellow
    Invoke-Paso $nombreCompilar ($ordenCompilar + $mcpu) $pistaCompilar
}

# Plaza local: sin espera si hay una libre; si no, la compilacion con -warnaserror salta a la Dell (si se
# puede) y lo demas espera la primera plaza local que se libere. Un fallo en la Dell corta la espera.
$remoto = $null
if ($modoPlaza) {
    try {
        $plaza = Enter-PlazaCompilacion -NoEsperar
        if (-not $plaza) {
            $remoto = Start-CompilacionEnDell
            if ($remoto.Motivo) {
                Write-Host "Sin salto a la Dell: $($remoto.Motivo)." -ForegroundColor Yellow
                $remoto = $null
            }
            $cancelar = {
                if ($remoto -and $remoto.Proc.HasExited -and (Get-ResultadoDell $remoto) -eq 'falla') { $script:dellFalla = $true }
                $script:dellFalla
            }
            $plaza = Enter-PlazaCompilacion -Cancelar $cancelar
            if (-not $plaza -and $remoto) { Close-CompilacionEnDell $remoto }
        }
    }
    catch {
        [void]$avisos.Add("no se pudo tomar plaza de compilacion ($($_.Exception.Message)): se compila sin plaza.")
        Write-Host "AVISO: no se pudo tomar plaza de compilacion: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

try {
    # 1-2. Restaurar y compilar toda la solucion (compila tambien Integration y E2E, sin ejecutarlos).
    Invoke-Paso 'Restaurar dependencias' ('dotnet restore CaeManager.slnx' + $mcpu)
    if (-not $remoto) { Invoke-Paso $nombreCompilar ($ordenCompilar + $mcpu) $pistaCompilar }

    # 3. Formato, exactamente como el job "Verificar formato" (dotnet format no admite -maxcpucount).
    if ($Corregir) {
        Invoke-Paso 'Formato: aplicar (dotnet format)' 'dotnet format CaeManager.slnx --no-restore'
        [void]$avisos.Add('-Corregir ha aplicado el formato: hay cambios sin commitear, revisalos con git diff y anadelos al commit.')
    }
    Invoke-Paso 'Formato (verify-no-changes)' 'dotnet format CaeManager.slnx --no-restore --verify-no-changes' `
        'Ejecuta de nuevo con -Corregir para aplicar el formato, revisa el diff y commitea.'

    # Compilacion hecha en la Dell: su veredicto antes de los tests (ver Close-CompilacionEnDell).
    if ($remoto) { Close-CompilacionEnDell $remoto }

    # 4. Tests unitarios (lista cerrada; ver Assert-ProyectoDeTestPermitido).
    foreach ($par in @(
            @('Tests de Domain', 'tests/CaeManager.Domain.Tests'),
            @('Tests de Application', 'tests/CaeManager.Application.Tests'),
            @('Tests de Architecture (ratchets)', 'tests/CaeManager.Architecture.Tests'),
            @('Tests de Web (bUnit)', 'tests/CaeManager.Web.Tests'))) {
        Invoke-Paso $par[0] (("dotnet test {0} --no-restore -nodeReuse:false" -f $par[1]) + $mcpu) -EsTest
    }
}
finally {
    if ($plaza) { Exit-PlazaCompilacion $plaza; $plaza = $null }
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

$sucio = @(git status --porcelain 2>$null)
if ($sucio.Count -gt 0) {
    [void]$avisos.Add("hay $($sucio.Count) fichero/s sin commitear: el preflight ha validado el arbol de trabajo, pero la PR solo lleva lo commiteado.")
}

Write-Resumen 0 ''
exit 0
