<#
.SYNOPSIS
    Respalda a Google Drive lo que no vive en GitHub, sin exponer .git a la sincronizacion.

.DESCRIPTION
    El repositorio es publico, asi que GitHub no puede ser la copia de seguridad de todo:
    los ficheros locales con valores reales (*.local.md, appsettings.Development.json)
    estan deliberadamente fuera del control de versiones. Y sincronizar la carpeta de
    trabajo entera contra Drive es peor que no hacer nada: el cliente de Drive escribe
    dentro de .git mientras git escribe en el mismo sitio, que es una via conocida de
    corrupcion del historial, y de paso sube 15 GB de .claude/worktrees y de binarios
    de compilacion que se regeneran solos.

    Este script invierte el planteamiento: Drive no toca la carpeta de trabajo. Se le
    entregan dos artefactos cerrados por ejecucion:

      1. hydra-<fecha>.bundle  - el repositorio entero (todas las ramas y etiquetas) en
                                 un solo fichero, verificado antes de publicarse. Se
                                 restaura con `git clone hydra-<fecha>.bundle`.
      2. locales-<fecha>.zip   - los ficheros ignorados que si importan, mas un parche
                                 con el trabajo sin commitear (que el bundle no ve).

    Cada artefacto se escribe con extension .part y solo se renombra al terminar, para
    que Drive nunca sincronice un fichero a medio escribir.

.PARAMETER Destino
    Carpeta de Drive donde se dejan los artefactos.

.PARAMETER Conservar
    Cuantas copias de cada tipo se mantienen. Las mas antiguas se borran.

.PARAMETER IncluirDatosSubidos
    Incluye src/CaeManager.Web/App_Data (documentos subidos). Apagado a proposito: son
    datos personales de trabajadores, y una cuenta personal de Google Drive no figura
    como subencargado en RGPD-TRATAMIENTO-DATOS.md. Actívalo solo si esa carpeta
    contiene unicamente datos de prueba.

.EXAMPLE
    pwsh scripts/respaldo-local.ps1
    powershell -ExecutionPolicy Bypass -File scripts\respaldo-local.ps1 -Conservar 30
#>
[CmdletBinding()]
param(
    [string]$Destino = "G:\Mi unidad\Hydra-Respaldos",
    [int]$Conservar = 14,
    [switch]$IncluirDatosSubidos
)

$ErrorActionPreference = "Stop"

$raiz = Split-Path -Parent $PSScriptRoot
$marca = Get-Date -Format "yyyy-MM-dd_HHmm"
$temporal = Join-Path $env:TEMP "hydra-respaldo-$marca"

function Escribir($mensaje) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $mensaje"
}

if (-not (Test-Path (Join-Path $raiz ".git"))) {
    throw "No parece un repositorio git: $raiz"
}

# Se comprueba la unidad de Drive antes de trabajar: si no esta montada, fallar aqui es
# mejor que generar los artefactos y no poder colocarlos.
$unidad = Split-Path -Qualifier $Destino
if (-not (Test-Path $unidad)) {
    throw "La unidad de Google Drive ($unidad) no esta montada. Arranca Drive para escritorio y reintenta."
}
if (-not (Test-Path $Destino)) {
    New-Item -ItemType Directory -Path $Destino -Force | Out-Null
    Escribir "Creada la carpeta de destino: $Destino"
}
New-Item -ItemType Directory -Path $temporal -Force | Out-Null

try {
    # --- 1. El repositorio ---------------------------------------------------------
    # --all incluye ramas, etiquetas y notas; HEAD entra aparte porque --all no lo
    # arrastra y sin el un `git clone` del bundle no sabe que rama sacar.
    $bundle = Join-Path $temporal "hydra-$marca.bundle"
    Escribir "Generando el bundle del repositorio..."
    Push-Location $raiz
    try {
        # Sin 2>&1: en Windows PowerShell 5.1 redirigir la salida de error de un
        # ejecutable nativo envuelve cada linea en un ErrorRecord y, con
        # $ErrorActionPreference = "Stop", aborta el script aunque git haya terminado
        # con codigo 0. git escribe su progreso por stderr, asi que aqui pasaria siempre.
        git bundle create $bundle --all HEAD | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git bundle create fallo con codigo $LASTEXITCODE" }

        # Un bundle que no verifica no es una copia de seguridad, es un fichero grande.
        git bundle verify $bundle | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "El bundle generado no supera 'git bundle verify'" }

        $ramas = (git for-each-ref --format="%(refname:short)" refs/heads | Measure-Object).Count
        $commits = (git rev-list --all --count)
    }
    finally { Pop-Location }
    Escribir "Bundle correcto: $ramas ramas, $commits commits, $([math]::Round((Get-Item $bundle).Length / 1MB, 1)) MB"

    # --- 2. Lo que el repositorio no guarda ----------------------------------------
    $areaLocal = Join-Path $temporal "locales"
    New-Item -ItemType Directory -Path $areaLocal -Force | Out-Null

    # Ficheros ignorados a proposito pero que cuesta o es imposible regenerar.
    $patrones = @(
        "*.local.md",
        "src\CaeManager.Web\appsettings.Development.json",
        ".claude\settings.local.json",
        ".claude\launch.json"
    )
    $copiados = 0
    foreach ($patron in $patrones) {
        $encontrados = Get-ChildItem -Path (Join-Path $raiz $patron) -Force -ErrorAction SilentlyContinue
        foreach ($f in $encontrados) {
            $relativa = $f.FullName.Substring($raiz.Length).TrimStart('\')
            $destinoF = Join-Path $areaLocal $relativa
            New-Item -ItemType Directory -Path (Split-Path -Parent $destinoF) -Force | Out-Null
            Copy-Item $f.FullName $destinoF -Force
            $copiados++
        }
    }

    # El bundle solo contiene lo commiteado. Sin esto, una sesion de trabajo sin cerrar
    # se pierde entera: el escenario mas probable de perdida real, no el disco muerto.
    Push-Location $raiz
    try {
        $parche = Join-Path $areaLocal "cambios-sin-commitear.patch"
        git diff HEAD > $parche
        if ((Get-Item $parche).Length -eq 0) { Remove-Item $parche -Force }

        # Los ficheros nuevos todavia sin `git add` no salen en `git diff HEAD`.
        $nuevos = git ls-files --others --exclude-standard
        foreach ($n in $nuevos) {
            $origen = Join-Path $raiz $n
            if (-not (Test-Path $origen)) { continue }
            $destinoN = Join-Path (Join-Path $areaLocal "sin-seguimiento") $n
            New-Item -ItemType Directory -Path (Split-Path -Parent $destinoN) -Force | Out-Null
            Copy-Item $origen $destinoN -Force
            $copiados++
        }
    }
    finally { Pop-Location }

    if ($IncluirDatosSubidos) {
        $datos = Join-Path $raiz "src\CaeManager.Web\App_Data"
        if (Test-Path $datos) {
            Escribir "AVISO: incluyendo App_Data (documentos subidos) por peticion explicita."
            Copy-Item $datos (Join-Path $areaLocal "App_Data") -Recurse -Force
        }
    }

    $zip = Join-Path $temporal "locales-$marca.zip"
    Compress-Archive -Path (Join-Path $areaLocal "*") -DestinationPath $zip -Force
    Escribir "Ficheros locales empaquetados: $copiados elementos"

    # --- 3. Publicar en Drive ------------------------------------------------------
    # .part primero y rename despues: Drive sincroniza lo que ve, y lo que ve tiene que
    # estar completo o no estar.
    foreach ($artefacto in @($bundle, $zip)) {
        $nombre = Split-Path -Leaf $artefacto
        $parcial = Join-Path $Destino "$nombre.part"
        Copy-Item $artefacto $parcial -Force
        Move-Item $parcial (Join-Path $Destino $nombre) -Force
        Escribir "Publicado: $nombre"
    }

    # --- 4. Rotacion ---------------------------------------------------------------
    foreach ($tipo in @("hydra-*.bundle", "locales-*.zip")) {
        $viejos = Get-ChildItem -Path (Join-Path $Destino $tipo) -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending |
                  Select-Object -Skip $Conservar
        foreach ($v in $viejos) {
            Remove-Item $v.FullName -Force
            Escribir "Rotado (borrado): $($v.Name)"
        }
    }

    # --- 5. Instrucciones junto a la copia ------------------------------------------
    # El dia que hagan falta, la maquina donde vivia este script puede no existir. Las
    # instrucciones tienen que estar dentro de Drive, no en el repositorio que se
    # intenta restaurar.
    $comoRestaurar = @"
COMO RESTAURAR ESTA COPIA
=========================
Generado por scripts/respaldo-local.ps1. Ultima actualizacion: $(Get-Date -Format 'yyyy-MM-dd HH:mm')

1. EL REPOSITORIO (hydra-<fecha>.bundle)

   git -c core.longpaths=true clone hydra-<fecha>.bundle C:\Project-Hydra

   El -c core.longpaths=true NO es opcional en Windows: este repositorio tiene rutas
   que pasan de 260 caracteres (los directorios por Command/Query del patron CQRS) y
   sin esa opcion el clon deja el arbol de trabajo a medias con "Filename too long".
   Comprobado: con la opcion, 1.667 archivos y arbol limpio; sin ella, falla.

   Restaura en una ruta CORTA (C:\Project-Hydra, no dentro de Documentos\...\algo).

   El bundle contiene todas las ramas y etiquetas. Para verlo antes de clonar:
     git bundle verify hydra-<fecha>.bundle
     git bundle list-heads hydra-<fecha>.bundle

2. LOS FICHEROS LOCALES (locales-<fecha>.zip)

   Descomprimir SOBRE la carpeta ya restaurada, respetando la estructura:
     - RUNBOOK-GRAPH-M365.local.md ......... identificadores reales del tenant M365
     - src/CaeManager.Web/appsettings.Development.json
     - .claude/ ............................ configuracion de las sesiones de agente
     - cambios-sin-commitear.patch ......... trabajo en curso al hacer la copia:
                                             git apply cambios-sin-commitear.patch
     - sin-seguimiento/ .................... ficheros que aun no estaban en git;
                                             copiar a mano a su ruta equivalente

3. QUE NO ESTA AQUI

   - Los documentos subidos (App_Data): excluidos a proposito, son datos personales
     de trabajadores y esta cuenta de Drive no figura como subencargado en
     RGPD-TRATAMIENTO-DATOS.md. Se respaldan por la via que decida ese documento.
   - Los secretos de produccion (variables de entorno de Railway, secreto de cliente
     de Graph): nunca han estado en disco. Ver DEPLOY.md y RUNBOOK-CLAVES.md.
   - node_modules, bin, obj, .claude/worktrees: se regeneran solos.
"@
    Set-Content -Path (Join-Path $Destino "COMO-RESTAURAR.txt") -Value $comoRestaurar -Encoding utf8

    $linea = "{0}  bundle={1}MB  ramas={2}  commits={3}  locales={4}" -f `
        (Get-Date -Format "yyyy-MM-dd HH:mm"),
        [math]::Round((Get-Item $bundle).Length / 1MB, 1), $ramas, $commits, $copiados
    Add-Content -Path (Join-Path $Destino "historial.log") -Value $linea -Encoding utf8

    Escribir "Respaldo completado en: $Destino"
}
finally {
    Remove-Item $temporal -Recurse -Force -ErrorAction SilentlyContinue
}
