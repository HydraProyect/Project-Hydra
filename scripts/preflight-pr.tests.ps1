<#
.SYNOPSIS
    Pruebas de scripts/preflight-pr.ps1: la gobernanza del cuerpo y la guarda de proyectos de test.

.DESCRIPTION
    Sin red y sin dotnet: cuerpo, ficheros y autor son datos sinteticos. Los casos son los de
    scripts/verificar-gobernanza-pr.tests.sh (los mismos incidentes reales: #674, #673, #678, #688, #828).
      - Siempre: cada caso comprueba el veredicto esperado del espejo PowerShell.
      - Con -ContraSh: ademas ejecuta scripts/verificar-gobernanza-pr.sh (la AUTORIDAD) con el mismo caso y exige
        el mismo codigo de salida, la misma secuencia de etiquetas OK/PROBLEMA y el mismo contador de problemas.
        Si el .sh cambia una regla y el espejo no, este test se pone en rojo. Corre en CI
        (.github/workflows/gobernanza-pr-tests.yml, donde hay bash y pwsh). Sin bash en el PATH, -ContraSh sale
        con codigo 2 en vez de dar una paridad falsa; sin -ContraSh solo comprueba el espejo.
      - La guarda Assert-ProyectoDeTestPermitido rechaza Integration y E2E y solo admite los cuatro proyectos unitarios.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\preflight-pr.tests.ps1
    pwsh -File scripts/preflight-pr.tests.ps1 -ContraSh
#>
[CmdletBinding()]
param([switch]$ContraSh)

$ErrorActionPreference = 'Stop'
if ($ContraSh -and -not (Get-Command bash -ErrorAction SilentlyContinue)) {
    Write-Host 'USO: -ContraSh necesita bash en el PATH (es el verificador autoritativo); sin bash, ejecuta el script sin -ContraSh.' -ForegroundColor Red
    exit 2
}
. (Join-Path $PSScriptRoot 'preflight-pr.ps1')

$raizRepo = Split-Path -Parent $PSScriptRoot
$verificadorSh = Join-Path $PSScriptRoot 'verificar-gobernanza-pr.sh'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('preflight-pr-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$o = [string][char]0x00F3     # o con tilde: el script de pruebas es ASCII
$ap = [string][char]0x00AA    # ordinal femenino (2a pasada)
$raya = [string][char]0x2014  # raya
$fallos = 0
$pruebas = 0
$utf8 = New-Object Text.UTF8Encoding($false)

function Convert-AEtiquetas([string[]]$Lineas) {
    $etiquetas = New-Object System.Collections.Generic.List[string]
    foreach ($l in $Lineas) {
        if ($l -match '^(OK|PROBLEMA)\b') { $etiquetas.Add($Matches[1]) }
        elseif ($l -match '^Problemas: \d+$') { $etiquetas.Add($l.Trim()) }
    }
    return ($etiquetas.ToArray() -join ',')
}

function Invoke-Caso {
    param(
        [string]$Descripcion,
        [int]$Esperado,
        [string[]]$Cuerpo,
        [string[]]$Ficheros,
        [string]$Autor = 'dependabot[bot]',
        [string]$Menciona = ''
    )
    $script:pruebas++
    $textoCuerpo = (($Cuerpo -join "`n") + "`n")
    $r = Invoke-GobernanzaCuerpoPr -Cuerpo $textoCuerpo -Ficheros $Ficheros -Autor $Autor
    $codigo = if ($r.Problemas -gt 0) { 1 } else { 0 }
    $errores = New-Object System.Collections.Generic.List[string]
    if ($codigo -ne $Esperado) { $errores.Add("codigo esperado $Esperado, obtenido $codigo") }
    if ($Menciona -ne '' -and (($r.Lineas -join "`n") -notmatch [regex]::Escape($Menciona))) {
        $errores.Add("la salida no menciona '$Menciona'")
    }
    if ($ContraSh) {
        $id = [Guid]::NewGuid().ToString('N')
        $fCuerpo = Join-Path $tmp "cuerpo-$id.txt"
        $fFich = Join-Path $tmp "ficheros-$id.txt"
        [IO.File]::WriteAllText($fCuerpo, $textoCuerpo, $utf8)
        [IO.File]::WriteAllText($fFich, ((($Ficheros -join "`n") + "`n")), $utf8)
        $args2 = @($verificadorSh, $fCuerpo, $fFich)
        if ($Autor -ne '') { $args2 += $Autor }
        $ErrorActionPreference = 'Continue'   # el stderr de un nativo no debe lanzar excepcion
        $salidaSh = @(& bash @args2 2>&1 | ForEach-Object { "$_" })
        $codigoSh = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($codigoSh -ne $codigo) { $errores.Add("PARIDAD: el .sh sale con $codigoSh y el espejo con $codigo") }
        $etSh = Convert-AEtiquetas $salidaSh
        $etPs = Convert-AEtiquetas $r.Lineas
        if ($etSh -ne $etPs) { $errores.Add("PARIDAD: etiquetas del .sh '$etSh' frente a las del espejo '$etPs'") }
    }
    if ($errores.Count -gt 0) {
        Write-Host "FALLO: $Descripcion" -ForegroundColor Red
        $errores | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        $r.Lineas | ForEach-Object { Write-Host "    | $_" }
        $script:fallos++
    }
    else { Write-Host "OK: $Descripcion" }
}

$roles = 'deploy/bootstrap/roles-de-cluster.sql'
$neutros = @('src/Foo.cs')
$rev = "## Revisi${o}n Codex"

Write-Host '=== Regla 1: paso operativo de roles de cluster (aislada de la regla 2) ==='
Invoke-Caso 'toca roles-de-cluster.sql sin seccion: falla' 1 @('## Resumen', 'Anade un rol nuevo.') @($roles, 'src/Foo.cs') -Menciona 'Paso operativo en servidores'
Invoke-Caso 'seccion presente pero solo nombra staging: falla' 1 @('## Paso operativo en servidores', 'Aplicar en staging con psql.') @($roles)
Invoke-Caso 'seccion presente pero solo nombra produccion: falla' 1 @('## Paso operativo en servidores', "Aplicar en producci${o}n con psql.") @($roles)
Invoke-Caso 'seccion presente pero vacia: falla' 1 @('## Paso operativo en servidores', '', '## Otra seccion', 'texto') @($roles)
Invoke-Caso 'seccion completa (staging y produccion): pasa' 0 @('## Resumen', 'cambio', '', '## Paso operativo en servidores', "Ejecutar el GRANT en staging y en producci${o}n antes de desplegar.") @($roles, 'deploy/bootstrap/otro.sql')
Invoke-Caso "'produccion' sin tilde tambien cuenta" 0 @('## Paso operativo en servidores', 'Aplicar en staging y produccion.') @($roles)
Invoke-Caso 'no toca roles-de-cluster.sql: la regla no aplica' 0 @('## Resumen', 'Cambio cualquiera.') @('src/Foo.cs', 'tests/FooTests.cs')
Invoke-Caso 'fichero de nombre parecido no dispara la regla' 0 @('## Resumen', 'Cambio cualquiera.') @('deploy/bootstrap/roles-de-cluster-otro.sql')

Write-Host '=== Rename, limite de paginacion, tabulador ==='
Invoke-Caso 'rename de roles-de-cluster.sql (visto por previous_filename) si dispara' 1 @('## Resumen', 'Renombra el fichero de roles.') @('deploy/bootstrap/roles-de-cluster-2027.sql', $roles)
$muchos3000 = 0..2999 | ForEach-Object { "src/Archivo$_.cs" }
$muchos2999 = 0..2998 | ForEach-Object { "src/Archivo$_.cs" }
Invoke-Caso '3000 ficheros o mas: falla cerrado' 1 @('## Resumen', 'PR enorme.') $muchos3000 -Menciona 'trunca'
Invoke-Caso '2999 ficheros: no dispara el limite' 0 @('## Resumen', 'PR grande pero no al limite.') $muchos2999
Invoke-Caso "'##<TAB>Otra seccion' cierra la seccion objetivo (queda vacia): falla" 1 @('## Paso operativo en servidores', "##`tOtra secci${o}n", 'Aplicar en staging y en produccion.') @($roles)
Invoke-Caso 'titulo con espacios finales y CRLF se reconoce' 0 @("## Paso operativo en servidores  `r", "Aplicar en staging y produccion.`r") @($roles)

Write-Host '=== Regla 2: revision Codex en el cuerpo ==='
Invoke-Caso 'sin seccion de revision Codex: falla' 1 @('## Resumen', 'Cambio cualquiera.') $neutros -Autor '' -Menciona 'Revision Codex'
Invoke-Caso "marcador 'resultado abajo' sin rellenar: falla" 1 @($rev, "2$ap pasada $raya (resultado abajo)") $neutros -Autor '' -Menciona 'resultado abajo'
Invoke-Caso "'resultado' y 'abajo' con varios espacios: sigue fallando" 1 @($rev, 'resultado    abajo') $neutros -Autor ''
Invoke-Caso "marcador 'pendiente': falla" 1 @($rev, 'pendiente de ejecutar') $neutros -Autor ''
Invoke-Caso "nombre de estado 'Pendiente' con inicial mayuscula: falla" 1 @($rev, 'Estado: Pendiente') $neutros -Autor ''
Invoke-Caso "marcador 'TODO': falla" 1 @($rev, 'TODO: pedirle a Codex que revise esto') $neutros -Autor ''
Invoke-Caso "'todo' en minusculas no es un marcador" 0 @($rev, 'Codex reviso todo el diff, sin hallazgos.') $neutros -Autor ''
Invoke-Caso "'independiente' no dispara 'pendiente'" 0 @($rev, 'El hallazgo es independiente del diseno actual; sin accion.') $neutros -Autor ''
Invoke-Caso "'pendientes' dentro de una frase real no es marcador (#828)" 0 @($rev, 'Sin hallazgos nuevos pendientes de esta pasada.') $neutros -Autor ''
Invoke-Caso 'hallazgos aceptados y rechazados: pasa' 0 @($rev, '', '- Aceptado: falta validar el tenant objetivo en el comando.', '- Rechazado: el guardarrail de RLS ya cubre ese camino (falso positivo).') $neutros -Autor ''
Invoke-Caso "'sin hallazgos' sola: pasa" 0 @($rev, 'sin hallazgos') $neutros -Autor ''
Invoke-Caso 'seccion de revision vacia: falla' 1 @($rev, '', '## Otra seccion', 'texto') $neutros -Autor ''
Invoke-Caso 'dependabot[bot] exime de la revision' 0 @('## Resumen', 'Bump de Serilog 4.4.0 a 4.4.1.') $neutros -Autor 'dependabot[bot]'
Invoke-Caso 'autor vacio no exime' 1 @('## Resumen', 'Cambio cualquiera.') $neutros -Autor ''
Invoke-Caso "bot con nombre parecido no exime ('dependabot')" 1 @('## Resumen', 'Cambio cualquiera.') $neutros -Autor 'dependabot'
Invoke-Caso 'solo un comentario HTML: falla' 1 @($rev, '<!-- -->') $neutros -Autor ''
Invoke-Caso 'comentario HTML junto a contenido real: pasa' 0 @($rev, '<!-- nota interna -->sin hallazgos') $neutros -Autor ''
Invoke-Caso "marcador 'TODOs' (plural): falla" 1 @($rev, 'TODOs: repasar con Codex') $neutros -Autor ''
Invoke-Caso "marcador 'resultados abajo' (plural): falla" 1 @($rev, "2$ap pasada $raya resultados abajo") $neutros -Autor ''
Invoke-Caso "marcador 'resultado-abajo' (con guion): falla" 1 @($rev, 'resultado-abajo') $neutros -Autor ''
Invoke-Caso "'Todo' con inicial mayuscula no es marcador" 0 @($rev, 'Todo el equipo reviso esto, sin hallazgos.') $neutros -Autor ''
Invoke-Caso "'resultado' y 'abajo' en lineas distintas no es el marcador" 0 @($rev, 'resultado final', 'abajo el detalle: sin hallazgos') $neutros -Autor ''

Write-Host '=== Variantes del titulo de la regla 2 ==='
Invoke-Caso "'## Revision de Codex' (con acento y 'de'): pasa" 0 @("## Revisi${o}n de Codex", 'sin hallazgos') $neutros -Autor ''
Invoke-Caso "'## Revision Codex' (sin tilde): pasa" 0 @('## Revision Codex', 'sin hallazgos') $neutros -Autor ''
Invoke-Caso "'## Revision de Codex' (sin tilde, con 'de'): pasa" 0 @('## Revision de Codex', 'sin hallazgos') $neutros -Autor ''
Invoke-Caso "variante con marcador 'pendiente': sigue fallando" 1 @("## Revisi${o}n de Codex", 'pendiente de ejecutar') $neutros -Autor ''
Invoke-Caso 'variante vacia: sigue fallando' 1 @("## Revisi${o}n de Codex", '', '## Otra seccion', 'texto') $neutros -Autor ''
Invoke-Caso "'## Revision por Codex' (no aceptada): falla" 1 @("## Revisi${o}n por Codex", 'sin hallazgos') $neutros -Autor ''

Write-Host '=== Combinacion de las dos reglas ==='
Invoke-Caso 'faltan las dos secciones a la vez: falla con 2 problemas' 1 @('## Resumen', 'Cambio grande.') @($roles) -Autor '' -Menciona 'Problemas: 2'
Invoke-Caso 'las dos secciones completas: pasa' 0 @($rev, 'sin hallazgos', '', '## Paso operativo en servidores', "GRANT en staging y producci${o}n antes del despliegue.") @($roles, 'src/Foo.cs') -Autor ''

Write-Host '=== Guarda de proyectos de test (nunca Integration ni E2E) ==='
function Test-Guarda([string]$Descripcion, [string]$Ruta, [bool]$DebeLanzar) {
    $script:pruebas++
    $lanzo = $false
    try { Assert-ProyectoDeTestPermitido $Ruta } catch { $lanzo = $true }
    if ($lanzo -ne $DebeLanzar) {
        Write-Host "FALLO: $Descripcion (lanzo=$lanzo, esperado=$DebeLanzar)" -ForegroundColor Red
        $script:fallos++
    }
    else { Write-Host "OK: $Descripcion" }
}
Test-Guarda 'IntegrationTests se rechaza' 'tests/CaeManager.IntegrationTests' $true
Test-Guarda 'E2ETests se rechaza' 'tests/CaeManager.E2ETests' $true
Test-Guarda 'IntegrationTests con barras de Windows se rechaza' 'tests\CaeManager.IntegrationTests' $true
Test-Guarda 'e2e en minusculas se rechaza' 'tests/caemanager.e2etests' $true
Test-Guarda 'un proyecto desconocido se rechaza' 'tests/CaeManager.Otro.Tests' $true
Test-Guarda 'una ruta con subcarpeta se rechaza' 'tests/CaeManager.Domain.Tests/Sub' $true
Test-Guarda 'Domain se admite' 'tests/CaeManager.Domain.Tests' $false
Test-Guarda 'Application se admite' 'tests/CaeManager.Application.Tests' $false
Test-Guarda 'Architecture se admite' 'tests/CaeManager.Architecture.Tests' $false
Test-Guarda 'Web (bUnit) se admite' 'tests/CaeManager.Web.Tests' $false

Write-Host '=== Guarda sobre el comando que se ejecuta ==='
function Test-GuardaComando([string]$Comando, [bool]$DebeLanzar) {
    $script:pruebas++
    $lanzo = $false
    try { Assert-ComandoPermitido $Comando } catch { $lanzo = $true }
    if ($lanzo -ne $DebeLanzar) {
        Write-Host "FALLO: comando '$Comando' (lanzo=$lanzo, esperado=$DebeLanzar)" -ForegroundColor Red
        $script:fallos++
    }
    else { Write-Host "OK: comando '$Comando' $(if ($DebeLanzar) { 'se rechaza' } else { 'se admite' })" }
}
Test-GuardaComando 'dotnet test tests/CaeManager.IntegrationTests --no-build' $true
Test-GuardaComando 'dotnet  test  "tests\CaeManager.E2ETests"' $true
Test-GuardaComando 'dotnet test --no-build tests/CaeManager.Domain.Tests' $true
Test-GuardaComando 'dotnet test' $true
Test-GuardaComando 'dotnet vstest algo.dll' $true
Test-GuardaComando 'dotnet build tests/CaeManager.IntegrationTests' $true
Test-GuardaComando 'dotnet test tests/CaeManager.Otro.Tests --no-restore' $true
Test-GuardaComando 'dotnet test tests/CaeManager.Domain.Tests --no-restore -nodeReuse:false' $false
Test-GuardaComando 'dotnet test tests/CaeManager.Web.Tests --no-restore -nodeReuse:false' $false
Test-GuardaComando 'dotnet restore CaeManager.slnx' $false
Test-GuardaComando 'dotnet build CaeManager.slnx --no-restore -warnaserror -nodeReuse:false' $false
Test-GuardaComando 'dotnet format CaeManager.slnx --no-restore --verify-no-changes' $false

# Los proyectos que el script ejecuta existen de verdad, y son solo esos cuatro.
$pruebas++
$fuente = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'preflight-pr.ps1'))
$proyectosEjecutados = [regex]::Matches($fuente, "'(tests/CaeManager\.[A-Za-z0-9]+\.Tests)'\)") | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$esperados = @('tests/CaeManager.Application.Tests', 'tests/CaeManager.Architecture.Tests', 'tests/CaeManager.Domain.Tests', 'tests/CaeManager.Web.Tests')
$faltan = @($esperados | Where-Object { -not (Test-Path -LiteralPath (Join-Path $raizRepo $_) -PathType Container) })
if (($proyectosEjecutados -join ',') -ne ($esperados -join ',') -or $faltan.Count -gt 0) {
    Write-Host "FALLO: los proyectos que ejecuta el script son '$($proyectosEjecutados -join ',')' y faltan carpetas: '$($faltan -join ',')'" -ForegroundColor Red
    $fallos++
}
else { Write-Host 'OK: el script ejecuta exactamente Domain, Application, Architecture y Web, y existen' }

Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host "Pruebas: $pruebas . Fallos: $fallos$(if ($ContraSh) { ' (con paridad contra el .sh)' } else { ' (sin paridad: use -ContraSh donde haya bash)' })"
if ($fallos -gt 0) { exit 1 }
exit 0
