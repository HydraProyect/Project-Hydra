#!/usr/bin/env python3
"""Métricas de proceso M1..M21 (S15 del análisis de causas raíz de 2026-10-02).

Sin este instrumento «mejora» no es medible: saca solas, del repositorio y de GitHub, las métricas del
§ 6 del análisis, para imprimir la línea base de hoy y compararla con la de mañana.

    python3 scripts/metricas-proceso.py                       # tabla en Markdown, repo + GitHub
    python3 scripts/metricas-proceso.py --solo repo           # sin llamar a GitHub (M11-M19, M21)
    python3 scripts/metricas-proceso.py --json > hoy.json     # para comparar
    python3 scripts/metricas-proceso.py --comparar base.json  # añade la columna «Antes» y el delta

Convenciones, para no engañarse (§ 3 del protocolo):
  * Cada fila lleva su CLASE: INSTRUMENTO (lo cuenta este guion sobre un dato que el guion ve entero),
    ESTIMACION (regex o heurística; orden de magnitud, no cifra fina) o NO_MEDIDA (el instrumento no
    existe o el dato no estaba disponible: se dice, no se rellena con un cero).
  * Una métrica que no puede calcularse devuelve NO_MEDIDA con el motivo. Un valor vacío NUNCA es un 0.
  * La ventana del lote UX (PR #1015..#1036) es explícita y editable (--pr-min, --pr-max): sin ella
    «el lote» no sería reproducible.
  * `gh` se invoca solo a través de gh_json(); METRICAS_GH cambia el ejecutable (lo usan las pruebas).

Códigos de salida: 0 con la tabla impresa (aunque haya filas NO_MEDIDA: se ven), 2 uso incorrecto.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import shlex
import statistics
import subprocess
import sys
from pathlib import Path

NEGOCIO_POR_DEFECTO = os.environ.get("HYDRA_NEGOCIO", r"C:\Users\chris\Project-Hydra-Negocio")
# Citado con el prefijo del repositorio de negocio, como exige EnlacesADocumentosExistentesTests para un
# documento que no vive en este repositorio; la ruta real es la que sigue al prefijo, bajo --negocio.
INFORME_RECORRIDO_CITADO = "Project-Hydra-Negocio/tecnico/docs/ux-audit/RECORRIDO-EN-VIVO-STAGING-2026-10-01.md"
INFORME_RECORRIDO = INFORME_RECORRIDO_CITADO.split("/", 1)[1]
REGEX_DEFECTO = re.compile(r"\bD-(\d{2})\b")
REGEX_CONTINUACION = re.compile(r"(?i)\b(resto|restos|residuos?|2\.?ª parte|segunda parte|continuaci[oó]n|remate)\b")


# ───────────────────────────── acceso a GitHub ─────────────────────────────

class GhNoDisponible(Exception):
    pass


def gh_json(args: list[str]):
    """Ejecuta `gh <args>` y devuelve el JSON parseado. Lanza GhNoDisponible si falla: nunca devuelve
    una lista vacía por un error (un resultado vacío no es una ausencia, § 3)."""
    try:
        exe = [t.strip('"') for t in shlex.split(os.environ.get("METRICAS_GH", "gh"), posix=(os.name != "nt"))]
    except ValueError as e:
        raise GhNoDisponible(f"METRICAS_GH mal formado (comillas sin cerrar): {e}") from e
    try:
        proc = subprocess.run([*exe, *args], capture_output=True, text=True, encoding="utf-8", timeout=300)
    except (OSError, subprocess.TimeoutExpired) as e:
        raise GhNoDisponible(f"no pude ejecutar gh: {e}") from e
    if proc.returncode != 0:
        raise GhNoDisponible(f"gh {' '.join(args[:3])}… salió con {proc.returncode}: {proc.stderr.strip()[:200]}")
    try:
        return json.loads(proc.stdout or "null")
    except json.JSONDecodeError as e:
        raise GhNoDisponible(f"gh devolvió algo que no es JSON: {e}") from e


def repo_slug() -> str:
    if os.environ.get("METRICAS_REPO"):
        return os.environ["METRICAS_REPO"]
    return gh_json(["repo", "view", "--json", "nameWithOwner"])["nameWithOwner"]


# ───────────────────────────── utilidades ─────────────────────────────

def fila(codigo, nombre, valor, clase, detalle="", numerico=None):
    return {"id": codigo, "metrica": nombre, "valor": valor, "clase": clase, "detalle": detalle, "numerico": numerico}


def no_medida(codigo, nombre, motivo):
    return fila(codigo, nombre, "NO_MEDIDA", "NO_MEDIDA", motivo)


def percentil(valores, p):
    if not valores:
        return None
    v = sorted(valores)
    k = (len(v) - 1) * p / 100
    f = int(k)
    c = min(f + 1, len(v) - 1)
    return v[f] + (v[c] - v[f]) * (k - f)


def minutos(creado: str, fusionado: str) -> float:
    a = dt.datetime.fromisoformat(creado.replace("Z", "+00:00"))
    b = dt.datetime.fromisoformat(fusionado.replace("Z", "+00:00"))
    return (b - a).total_seconds() / 60


def primera_seccion(cuerpo: str) -> str:
    """El cuerpo de la PR hasta el segundo encabezado «## »: la sección de defectos corregidos."""
    partes = re.split(r"(?m)^## ", cuerpo or "")
    return "## ".join(partes[:2])


def seccion(cuerpo: str, titulo_regex: str) -> str:
    m = re.search(rf"(?ms)^##\s+{titulo_regex}[^\n]*\n(.*?)(?=^## |\Z)", cuerpo or "")
    return m.group(1) if m else ""


# ───────────────────────────── M1: defectos del recorrido ─────────────────────────────

def m1_defectos(texto_informe: str):
    """Filas `| D-NN | … |` por severidad y por tanda. La tanda sale del encabezado `## … tanda N`."""
    sev = {"ALTA": 0, "MEDIA": 0, "BAJA": 0}
    por_tanda = {}
    ids = []
    tanda = None
    for linea in texto_informe.splitlines():
        if linea.startswith("## "):
            m = re.search(r"(?i)tanda\s*(\d)", linea)
            tanda = m.group(1) if m else tanda
        m = re.match(r"\|\s*(D-\d{2})\s*\|(.*)", linea)
        if not m:
            continue
        celdas = [c.strip() for c in m.group(2).split("|")]
        severidad = next((c for c in celdas if c in sev), None)
        if severidad is None:
            continue
        sev[severidad] += 1
        ids.append(m.group(1))
        por_tanda[tanda] = por_tanda.get(tanda, 0) + 1
    return ids, sev, por_tanda


def metrica_m1(negocio: Path):
    ruta = negocio / INFORME_RECORRIDO
    if not ruta.exists():
        return no_medida("M1", "Defectos por recorrido UX", f"no existe {ruta} (HYDRA_NEGOCIO)"), []
    ids, sev, por_tanda = m1_defectos(ruta.read_text(encoding="utf-8"))
    total = len(ids)
    if total == 0:
        return no_medida("M1", "Defectos por recorrido UX", "el informe existe pero no se reconoció ninguna fila D-NN"), []
    tandas = " · ".join(f"tanda {t}: {n}" for t, n in sorted(por_tanda.items(), key=lambda p: str(p[0])))
    return fila("M1", "Defectos por recorrido UX", f"{total} (ALTA {sev['ALTA']} / MEDIA {sev['MEDIA']} / BAJA {sev['BAJA']})",
                "INSTRUMENTO", tandas, total), ids


# ───────────────────────────── métricas de PR (M2, M3, M6..M10) ─────────────────────────────

def defectos_por_pr(prs):
    """{D-NN: {n_pr…}} usando título + primera sección del cuerpo."""
    mapa = {}
    for pr in prs:
        texto = (pr.get("title") or "") + "\n" + primera_seccion(pr.get("body") or "")
        for d in set(REGEX_DEFECTO.findall(texto)):
            mapa.setdefault(f"D-{d}", set()).add(pr["number"])
    return mapa


def calcular_m2(prs, total_defectos):
    mapa = defectos_por_pr(prs)
    multiples = sorted(d for d, ps in mapa.items() if len(ps) >= 2)
    denom = total_defectos if total_defectos else len(mapa)
    return len(multiples), denom, multiples, len(mapa)


# «sin hallazgos altos», «ningún medio», «0 altos», «sin hallazgos altos ni medios»: nombran la gravedad para
# NEGARLA. Contarlas como hallazgo daba un 95 % de PR con hallazgo ≥ medio frente al 77 % medido a mano.
NEGACION_DE_GRAVEDAD = re.compile(
    r"(?i)\b(?:sin|ning[uú]n[oa]?|cero|0|no hay|no se hallaron)\s+(?:hallazgos?\s+)?(?:altos?|altas?|medios?|medias?|graves?)"
    r"(?:\s*(?:,|/|y|ni|o)\s*(?:hallazgos?\s+)?(?:altos?|altas?|medios?|medias?|graves?))*")


def calcular_m3(prs):
    con_seccion = 0
    medios_o_altos = 0
    altos = 0
    for pr in prs:
        sec = seccion(pr.get("body") or "", r"Revisi[oó]n Codex")
        if not sec.strip():
            continue
        con_seccion += 1
        sec = NEGACION_DE_GRAVEDAD.sub(" ", sec)
        alto = re.search(r"(?i)\b(alt[ao]s?|high)\b", sec) is not None
        medio = re.search(r"(?i)\b(medi[oa]s?|medium)\b", sec) is not None
        if alto:
            altos += 1
        if alto or medio:
            medios_o_altos += 1
    return con_seccion, medios_o_altos, altos


def calcular_m9(prs):
    cont = [pr["number"] for pr in prs if REGEX_CONTINUACION.search(pr.get("title") or "")]
    return cont


def ficheros_de_pr(slug, numero):
    datos = gh_json(["api", f"repos/{slug}/pulls/{numero}/files", "--paginate"])
    return [f["filename"] for f in datos]


def metricas_de_pr(slug, args, total_defectos):
    filas = []
    try:
        prs = gh_json(["pr", "list", "--state", "merged", "--limit", str(args.limite_pr),
                       "--json", "number,title,body,createdAt,mergedAt,changedFiles,author,headRefName"])
    except GhNoDisponible as e:
        motivo = str(e)
        return [no_medida(c, n, motivo) for c, n in [
            ("M2", "Defectos que necesitan ≥2 PR"), ("M3", "PR con hallazgo ≥ medio del revisor"),
            ("M6", "Ficheros tocados por defecto corregido"), ("M7", "Mediana de ficheros por PR"),
            ("M8", "Tiempo PR → fusión"), ("M9", "Fracción de PR de continuación"),
            ("M10", "Ficheros tocados en ≥2 PR del lote")]], []
    if prs is None:
        prs = []
    lote = [p for p in prs if args.pr_min <= p["number"] <= args.pr_max]
    humanos = [p for p in prs if (p.get("author") or {}).get("login", "") not in ("dependabot[bot]", "app/dependabot")
               and (p.get("createdAt") or "") >= args.desde]
    ventana = f"lote #{args.pr_min}..#{args.pr_max} ({len(lote)} PR)"
    # El tope solo recorta algo si la PR más antigua devuelta es MÁS NUEVA que el inicio de lo que se mide.
    # Con el tope alcanzado pero cubriendo la ventana entera no hay nada que avisar.
    tope = bool(prs) and len(prs) >= args.limite_pr
    lote_recortado = tope and min(p["number"] for p in prs) > args.pr_min
    desde_recortado = tope and min((p.get("createdAt") or "") for p in prs) > args.desde
    if lote_recortado:
        ventana += (f"; ATENCIÓN: el tope de --limite-pr ({args.limite_pr}) corta antes del inicio del lote "
                    f"(la PR más antigua leída es #{min(p['number'] for p in prs)}): súbelo")
    aviso_desde = (f"; ATENCIÓN: el tope de --limite-pr ({args.limite_pr}) corta después de {args.desde}: la ventana está recortada, súbelo"
                   if desde_recortado else "")

    if not lote:
        for c, n in [("M2", "Defectos que necesitan ≥2 PR"), ("M3", "PR con hallazgo ≥ medio del revisor"),
                     ("M6", "Ficheros tocados por defecto corregido"), ("M9", "Fracción de PR de continuación"),
                     ("M10", "Ficheros tocados en ≥2 PR del lote")]:
            filas.append(no_medida(c, n, f"no hay PR fusionadas en la ventana {args.pr_min}..{args.pr_max} dentro de las últimas {args.limite_pr}"))
    else:
        mult, denom, lista, distintos = calcular_m2(lote, total_defectos)
        nota_denominador = "" if total_defectos else "; DENOMINADOR = defectos distintos citados (sin el informe del recorrido no hay total)"
        filas.append(fila("M2", "Defectos que necesitan ≥2 PR", f"{mult} de {denom}", "ESTIMACION",
                          f"{ventana}; D-NN en título + primera sección; {distintos} defectos distintos citados; con ≥2: {', '.join(lista) or '—'}{nota_denominador}", mult))

        con_sec, medios, altos = calcular_m3(lote)
        if con_sec == 0:
            filas.append(no_medida("M3", "PR con hallazgo ≥ medio del revisor", "ninguna PR del lote trae sección «## Revisión Codex»"))
        else:
            filas.append(fila("M3", "PR con hallazgo ≥ medio del revisor", f"{medios} de {con_sec} ({100 * medios / con_sec:.0f} %); altos {altos}",
                              "ESTIMACION", f"{ventana}; autoinforme del autor de la PR, regex sobre la sección que descarta las negaciones "
                              "(«sin hallazgos altos»); sobrecuenta frente a la lectura a mano del 2026-10-02 (17 de 22, altos 4)", medios))

        mapa = defectos_por_pr(lote)
        num_con_d = {n for ps in mapa.values() for n in ps}
        if num_con_d and mapa:
            try:
                por_pr = {n: ficheros_de_pr(slug, n) for n in sorted(num_con_d)}
                toques = sum(len(v) for v in por_pr.values())
                unicos = len({f for v in por_pr.values() for f in v})
                filas.append(fila("M6", "Ficheros tocados por defecto corregido",
                                  f"{toques / len(mapa):.1f} toques ({unicos / len(mapa):.1f} únicos)", "ESTIMACION",
                                  f"{toques} toques ({unicos} únicos) en {len(num_con_d)} PR con D-NN para {len(mapa)} defectos", round(toques / len(mapa), 1)))
            except GhNoDisponible as e:
                filas.append(no_medida("M6", "Ficheros tocados por defecto corregido", str(e)))
        else:
            filas.append(no_medida("M6", "Ficheros tocados por defecto corregido", "ninguna PR del lote cita D-NN"))

        cont = calcular_m9(lote)
        filas.append(fila("M9", "Fracción de PR de continuación", f"{len(cont)} de {len(lote)} ({100 * len(cont) / len(lote):.0f} %)", "ESTIMACION",
                          f"{ventana}; solo por título (resto/residuos/2ª parte/continuación): {', '.join('#' + str(n) for n in cont) or '—'}; "
                          "no ve las PR que cierran el hueco de otra sin decirlo en el título (la lectura a mano del 2026-10-02 dio 8 de 22)", len(cont)))

        try:
            todos = {p["number"]: ficheros_de_pr(slug, p["number"]) for p in lote}
            cuenta = {}
            for fs in todos.values():
                for f in set(fs):
                    cuenta[f] = cuenta.get(f, 0) + 1
            repetidos = sum(1 for n in cuenta.values() if n >= 2)
            filas.append(fila("M10", "Ficheros tocados en ≥2 PR del lote", f"{repetidos} de {len(cuenta)} ({100 * repetidos / len(cuenta):.0f} %)",
                              "INSTRUMENTO", ventana, repetidos))
        except (GhNoDisponible, ZeroDivisionError) as e:
            filas.append(no_medida("M10", "Ficheros tocados en ≥2 PR del lote", str(e)))

    if humanos:
        med_lote = statistics.median([p["changedFiles"] for p in lote]) if lote else None
        med_todas = statistics.median([p["changedFiles"] for p in humanos])
        filas.append(fila("M7", "Mediana de ficheros por PR",
                          f"{med_todas:g} (lote {med_lote:g})" if med_lote is not None else f"{med_todas:g} (lote sin datos)", "INSTRUMENTO",
                          f"{len(humanos)} PR no-Dependabot desde {args.desde}{aviso_desde}; {ventana}", med_todas))
        tiempos = [minutos(p["createdAt"], p["mergedAt"]) for p in humanos if p.get("mergedAt")]
        tiempos_lote = [minutos(p["createdAt"], p["mergedAt"]) for p in lote if p.get("mergedAt")]
        det = f"{len(tiempos)} PR no-Dependabot desde {args.desde}: p75 {percentil(tiempos, 75):.0f}, p90 {percentil(tiempos, 90):.0f} min{aviso_desde}"
        txt = f"mediana {statistics.median(tiempos):.0f} min"
        if tiempos_lote:
            txt += f" (lote {statistics.median(tiempos_lote):.0f})"
        filas.append(fila("M8", "Tiempo PR → fusión", txt, "INSTRUMENTO", det, round(statistics.median(tiempos), 1)))
    else:
        filas.append(no_medida("M7", "Mediana de ficheros por PR", f"ninguna PR no-Dependabot desde {args.desde} en las últimas {args.limite_pr}"))
        filas.append(no_medida("M8", "Tiempo PR → fusión", f"ninguna PR no-Dependabot desde {args.desde} en las últimas {args.limite_pr}"))
    return filas, lote


# ───────────────────────────── M4 y M5: CI ─────────────────────────────

def calcular_ci(runs, ramas_lote):
    por_evento = {}
    for r in runs:
        por_evento.setdefault(r["event"], []).append(r)
    resultado = {}
    for ev, lista in por_evento.items():
        fallos = [r for r in lista if r.get("conclusion") == "failure"]
        resultado[ev] = (len(fallos), len(lista))
    pr = por_evento.get("pull_request", [])
    ramas = {r["headBranch"] for r in pr}
    ramas_rojas = {r["headBranch"] for r in pr if r.get("conclusion") == "failure"}
    lote_runs = [r for r in pr if r["headBranch"] in ramas_lote]
    lote_rojos = [r for r in lote_runs if r.get("conclusion") == "failure"]
    return resultado, (len(ramas_rojas), len(ramas)), (len(lote_rojos), len(lote_runs), len(ramas_lote))


def metricas_de_ci(args, ramas_lote):
    try:
        runs = gh_json(["run", "list", "--workflow", "ci.yml", "--limit", str(args.limite_runs),
                        "--json", "databaseId,conclusion,event,headBranch,createdAt"])
    except GhNoDisponible as e:
        return [no_medida("M4", "Rojos de CI por PR", str(e)), no_medida("M5", "Rojos en main", str(e))]
    if not runs:
        return [no_medida("M4", "Rojos de CI por PR", "gh run list devolvió 0 runs de ci.yml"),
                no_medida("M5", "Rojos en main", "gh run list devolvió 0 runs de ci.yml")]
    por_evento, ramas, lote = calcular_ci(runs, ramas_lote)
    desde = min(r["createdAt"] for r in runs)[:10]
    hasta = max(r["createdAt"] for r in runs)[:10]
    ventana = f"{len(runs)} runs, {desde}..{hasta} (el tope de --limit corta la ventana)"
    pr_f, pr_n = por_evento.get("pull_request", (0, 0))
    mg_f, mg_n = por_evento.get("merge_group", (0, 0))
    txt = f"ramas con ≥1 rojo {ramas[0]}/{ramas[1]}" + (f" ({100 * ramas[0] / ramas[1]:.1f} %)" if ramas[1] else "")
    txt += f"; merge_group {mg_f}/{mg_n}" + (f" ({100 * mg_f / mg_n:.1f} %)" if mg_n else "")
    if lote[1]:
        txt += f"; lote: {lote[0]} rojos en {lote[1]} runs de {lote[2]} ramas"
    f4 = fila("M4", "Rojos de CI por PR", txt, "INSTRUMENTO",
              ventana + "; no ve rojos locales previos al push ni un rojo seguido de un re-run verde (gh da la conclusión final)",
              round(100 * ramas[0] / ramas[1], 1) if ramas[1] else None)
    pu_f, pu_n = por_evento.get("push", (0, 0))
    runs_main = [r for r in runs if r["event"] == "push" and r["headBranch"] == "main"]
    mf = sum(1 for r in runs_main if r.get("conclusion") == "failure")
    f5 = fila("M5", "Rojos en main", f"{mf} de {len(runs_main)}" + (f" ({100 * mf / len(runs_main):.1f} %)" if runs_main else ""),
              "INSTRUMENTO", ventana, mf) if runs_main else no_medida("M5", "Rojos en main", "ningún run de push a main en la ventana")
    return [f4, f5]


# ───────────────────────────── métricas del repositorio (M11..M19, M21) ─────────────────────────────

def etiquetas(texto: str, nombre: str):
    """Devuelve el texto de cada etiqueta `<nombre …>` (no `<nombrePlus`), escaneando comillas,
    llaves y paréntesis: un `>` dentro de `@(…)`, de una lambda `=>` o de una cadena no cierra la etiqueta."""
    res = []
    patron = re.compile(r"<" + re.escape(nombre) + r"(?![\w.])")
    for m in patron.finditer(texto):
        i = m.end()
        prof = 0
        comilla = None
        n = len(texto)
        while i < n:
            c = texto[i]
            if comilla:
                if c == comilla:
                    comilla = None
            elif c in "\"'":
                comilla = c
            elif c in "({[":
                prof += 1
            elif c in ")}]":
                prof -= 1
            elif c == ">" and prof <= 0:
                res.append(texto[m.start():i + 1])
                break
            i += 1
    return res


def quitar_comentarios_razor(texto: str) -> str:
    texto = re.sub(r"(?s)@\*.*?\*@", "", texto)
    return re.sub(r"(?s)<!--.*?-->", "", texto)


def ficheros(raiz: Path, patron: str, excluir=("obj", "bin", "Migrations")):
    for p in raiz.rglob(patron):
        if any(parte in excluir for parte in p.parts):
            continue
        yield p


CAMPOS = re.compile(r"<(CampoTexto|CampoSelect|CampoTextarea|SelectorEntidad|SelectorMultiple|input)\b")


def metricas_de_repo(raiz: Path):
    filas = []
    razor = list((p, quitar_comentarios_razor(p.read_text(encoding="utf-8", errors="replace")))
                 for p in ficheros(raiz / "src", "*.razor"))

    botones = [t for _, tx in razor for t in etiquetas(tx, "Boton")]
    if not botones:
        filas.append(no_medida("M11", "<Boton> sin Variante", "no encontré ninguna etiqueta <Boton> (¿cambió el componente?)"))
        filas.append(no_medida("M12", "Deshabilitados sin motivo", "no encontré ninguna etiqueta <Boton>"))
    else:
        sin_var = sum(1 for t in botones if not re.search(r"\bVariante\s*=", t))
        filas.append(fila("M11", "<Boton> sin Variante", f"{sin_var} de {len(botones)}", "ESTIMACION",
                          "escáner de etiquetas sobre .razor sin comentarios; no ve <Boton> generados en C#", sin_var))
        deshab = [t for t in botones if re.search(r"\bDeshabilitado\s*=", t)]
        sin_motivo = sum(1 for t in deshab if not re.search(r"\b(Motivo|title|Title)\s*=", t))
        filas.append(fila("M12", "Deshabilitados sin motivo", f"{sin_motivo} de {len(deshab)}", "ESTIMACION",
                          "cota superior: un texto explicativo contiguo no cuenta como motivo", sin_motivo))

    sin_razor = "no encontré ningún .razor bajo src/ (¿--raiz equivocada?)"
    if not razor:
        filas.append(no_medida("M13", "Drawer/Modal con campos y sin guardián", sin_razor))
        filas.append(no_medida("M14", 'role="alert" artesanal', sin_razor))
    else:
        con_dm = [(p, tx) for p, tx in razor if re.search(r"<(Drawer|Modal)\b", tx)]
        sin_guardian = [p for p, tx in con_dm if CAMPOS.search(tx) and not re.search(r"\b(HayCambios|SinCambios)\b", tx)]
        con_campos = [p for p, tx in con_dm if CAMPOS.search(tx)]
        filas.append(fila("M13", "Drawer/Modal con campos y sin guardián", f"{len(sin_guardian)} de {len(con_campos)}", "ESTIMACION",
                          "a nivel de fichero: los campos de un componente hijo no se ven", len(sin_guardian)))

        alertas_ficheros = 0
        alertas = 0
        for _, tx in razor:
            n = len(re.findall(r'role\s*=\s*"alert"', tx))
            if n:
                alertas_ficheros += 1
                alertas += n
        filas.append(fila("M14", 'role="alert" artesanal', f"{alertas_ficheros} ficheros / {alertas}", "ESTIMACION",
                          "cuenta marcado, no comportamiento; incluye el componente compartido si existe", alertas))

    e2e = raiz / "tests" / "CaeManager.E2ETests"
    textos = ([p.read_text(encoding="utf-8", errors="replace") for p in e2e.rglob("*.cs") if "obj" not in p.parts and "bin" not in p.parts]
              if e2e.exists() else [])
    if textos:
        porte = sum(len(re.findall(r"\bGetByText\(", t)) for t in textos)
        posic = sum(len(re.findall(r"\.(?:First|Last)\b(?!\s*[(<])|\.Nth\(", t)) for t in textos)
        testid = sum(len(re.findall(r"\bGetByTestId\(", t)) for t in textos)
        filas.append(fila("M15", "Locators por texto o posicionales en E2E", f"GetByText {porte} + posicionales {posic}; GetByTestId {testid}",
                          "ESTIMACION", "grep sobre el texto (comentarios incluidos); el trinquete LocatorsDeE2ECongeladosTests cuenta con Roslyn y es el que manda",
                          porte + posic))
    else:
        filas.append(no_medida("M15", "Locators por texto o posicionales en E2E", "no hay ningún .cs en tests/CaeManager.E2ETests"))

    # M16: «Cliente» a secas en pantalla. S1 (vocabulario ejecutable) no existe; esto es una aproximación.
    patron = re.compile(r"\bClientes?\b(?!\s+(?:empresarial|empresariales|comercial|de servicio|delegante))")
    en_resx = 0
    for p in ficheros(raiz / "src", "*.resx"):
        if re.search(r"\.[a-z]{2}(-[A-Za-z]{2,4})?\.resx$", p.name):
            continue
        for v in re.findall(r"(?s)<value>(.*?)</value>", p.read_text(encoding="utf-8", errors="replace")):
            en_resx += len(patron.findall(v))
    en_marcado = 0
    for _, tx in razor:
        sin_codigo = re.sub(r"(?s)@code\s*\{.*", "", tx)
        sin_codigo = re.sub(r"@\([^)]*\)|@[\w.]+", "", sin_codigo)
        for texto in re.findall(r">([^<>]+)<", sin_codigo):
            en_marcado += len(patron.findall(texto))
    if not razor:
        filas.append(no_medida("M16", "«Cliente» a secas en pantalla", sin_razor))
    else:
        filas.append(fila("M16", "«Cliente» a secas en pantalla", f"{en_resx} valores .resx + {en_marcado} en marcado", "ESTIMACION",
                          "S1 (vocabulario ejecutable) no existe: regex sobre texto de marcado fuera de @code; ni ve texto montado en C#", en_resx + en_marcado))

    # M17: trinquete de deuda terminológica (TerminologiaCanonicaTests)
    arq = raiz / "tests" / "CaeManager.Architecture.Tests"
    terminologia = arq / "TerminologiaCanonicaTests.cs"
    if terminologia.exists():
        t = terminologia.read_text(encoding="utf-8", errors="replace")
        bloque = re.search(r"Dictionary<string, int> Congelado = new\(\)\s*\{(.*?)\n    \};", t, re.S)
        valores = dict(re.findall(r'\["(\w+)"\]\s*=\s*(\d+),', bloque.group(1))) if bloque else {}
        if valores:
            filas.append(fila("M17", "Deuda terminológica por trinquete", " · ".join(f"{k} {v}" for k, v in valores.items()),
                              "INSTRUMENTO", "valores congelados leídos de TerminologiaCanonicaTests.Congelado (que el propio test verifica)",
                              sum(int(v) for v in valores.values())))
        else:
            filas.append(no_medida("M17", "Deuda terminológica por trinquete", "no se encontró el diccionario Congelado"))
    else:
        filas.append(no_medida("M17", "Deuda terminológica por trinquete", "no existe TerminologiaCanonicaTests.cs"))

    # M18: ClienteId — la lista congelada por ubicación (ClienteIdNoSeExtiendeTests)
    lista = arq / "Congelados" / "ClienteId-ubicaciones.txt"
    if lista.exists():
        total = 0
        archivos = set()
        for l in lista.read_text(encoding="utf-8").splitlines():
            m = re.match(r"^(.*) :: (.*) = (\d+)$", l.strip())
            if m:
                archivos.add(m.group(1))
                total += int(m.group(3))
        if not archivos:
            filas.append(no_medida("M18", "ClienteId (lista congelada por ubicación)",
                                   "la lista existe pero ninguna línea casa con «lugar :: símbolo = n» (¿cambió el formato?)"))
        else:
            filas.append(fila("M18", "ClienteId (lista congelada por ubicación)", f"{total} apariciones / {len(archivos)} ficheros",
                              "INSTRUMENTO", "suma de Congelados/ClienteId-ubicaciones.txt (identificadores .cs, texto .razor sin comentarios, valores .resx)", total))
    else:
        filas.append(no_medida("M18", "ClienteId (lista congelada por ubicación)", "no existe la lista (el trinquete aún no está en esta rama)"))

    # M19: textos sin localizar congelados
    sin_localizar = arq / "TextosSinLocalizarCongeladosTests.cs"
    if sin_localizar.exists():
        t = sin_localizar.read_text(encoding="utf-8", errors="replace")
        nums = [int(n) for n in re.findall(r'^\s*\["\w+"\]\s*=\s*(\d+),', t, re.M)]
        if nums:
            filas.append(fila("M19", "Textos sin localizar congelados", f"{sum(nums)} ({len(nums)} entradas)", "ESTIMACION",
                              "suma de las entradas del diccionario del trinquete (heurística del propio trinquete)", sum(nums)))
        else:
            filas.append(no_medida("M19", "Textos sin localizar congelados", "no se reconoció el diccionario del trinquete"))
    else:
        filas.append(no_medida("M19", "Textos sin localizar congelados", "no existe TextosSinLocalizarCongeladosTests.cs"))

    filas.append(no_medida("M20", "Puntaje de mutación sobre el diff", "no existe (S5, Stryker, no implementado)"))

    # M21: cobertura de salvaguardas
    fich = [p for p in arq.rglob("*.cs") if "obj" not in p.parts and "bin" not in p.parts] if arq.exists() else []
    if fich:
        attrs = sum(len(re.findall(r"^\s*\[(?:Fact|Theory)\b", p.read_text(encoding="utf-8", errors="replace"), re.M)) for p in fich)
        filas.append(fila("M21", "Cobertura de salvaguardas", f"{len(fich)} ficheros / {attrs} métodos [Fact]/[Theory]", "INSTRUMENTO",
                          "ficheros .cs y atributos de tests/CaeManager.Architecture.Tests (un Theory cuenta 1, no sus casos)", attrs))
    else:
        filas.append(no_medida("M21", "Cobertura de salvaguardas", "no hay ningún .cs en tests/CaeManager.Architecture.Tests"))
    return filas


# ───────────────────────────── salida ─────────────────────────────

def comparar(filas, base):
    previo = {f["id"]: f for f in base.get("metricas", [])}
    for f in filas:
        p = previo.get(f["id"])
        f["antes"] = p["valor"] if p else None
        if p and f.get("numerico") is not None and p.get("numerico") is not None:
            f["delta"] = round(f["numerico"] - p["numerico"], 2)
    return filas


def a_markdown(filas, cabecera, con_antes):
    cols = "| # | Métrica | Valor | Clase | Detalle |" + (" Antes | Δ |" if con_antes else "")
    sep = "|---|---|---|---|---|" + ("---|---|" if con_antes else "")
    out = [cabecera, "", cols, sep]
    for f in filas:
        linea = f"| {f['id']} | {f['metrica']} | {f['valor']} | {f['clase']} | {f['detalle']} |"
        if con_antes:
            linea += f" {f.get('antes', '')} | {f.get('delta', '')} |"
        out.append(linea)
    return "\n".join(out)


def git(raiz: Path, *args):
    try:
        return subprocess.run(["git", *args], cwd=raiz, capture_output=True, text=True, encoding="utf-8").stdout.strip()
    except OSError:
        return ""


def main(argv=None):
    # En Windows la consola por defecto es cp1252 y no representa «≥» ni «→» de los rótulos.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description="Métricas de proceso M1..M21 (S15)")
    ap.add_argument("--solo", choices=["repo", "github"], help="limita las fuentes")
    ap.add_argument("--raiz", default=str(Path(__file__).resolve().parent.parent))
    ap.add_argument("--negocio", default=NEGOCIO_POR_DEFECTO)
    ap.add_argument("--pr-min", type=int, default=1015)
    ap.add_argument("--pr-max", type=int, default=1036)
    ap.add_argument("--desde", default="2026-09-12", help="inicio de la ventana de PR no-Dependabot (M7, M8)")
    ap.add_argument("--limite-pr", type=int, default=600)
    ap.add_argument("--limite-runs", type=int, default=1000)
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--comparar", metavar="BASE.json")
    a = ap.parse_args(argv)

    raiz = Path(a.raiz)
    filas = []
    ids_defectos = []
    ramas_lote = set()

    if a.solo != "github":
        f1, ids_defectos = metrica_m1(Path(a.negocio))
        filas.append(f1)
    if a.solo != "repo":
        try:
            slug = repo_slug()
            f_pr, lote = metricas_de_pr(slug, a, len(ids_defectos) or None)
            ramas_lote = {p["headRefName"] for p in lote}
            filas.extend(f_pr)
            filas.extend(metricas_de_ci(a, ramas_lote))
        except GhNoDisponible as e:
            for c, n in [("M2", "Defectos que necesitan ≥2 PR"), ("M3", "PR con hallazgo ≥ medio del revisor"), ("M4", "Rojos de CI por PR"),
                         ("M5", "Rojos en main"), ("M6", "Ficheros tocados por defecto corregido"), ("M7", "Mediana de ficheros por PR"),
                         ("M8", "Tiempo PR → fusión"), ("M9", "Fracción de PR de continuación"), ("M10", "Ficheros tocados en ≥2 PR del lote")]:
                filas.append(no_medida(c, n, str(e)))
    if a.solo != "github":
        filas.extend(metricas_de_repo(raiz))

    filas.sort(key=lambda f: int(f["id"][1:]))
    base = None
    if a.comparar:
        # utf-8-sig: el `>` de PowerShell 5.1 escribe el JSON con BOM.
        base = json.loads(Path(a.comparar).read_text(encoding="utf-8-sig"))
        filas = comparar(filas, base)
        if base.get("ventana_lote") and base["ventana_lote"] != [a.pr_min, a.pr_max]:
            # Las métricas del lote (M2, M3, M6, M9, M10) no son comparables si la ventana cambió.
            print(f"AVISO: la línea base se midió sobre el lote #{base['ventana_lote'][0]}..#{base['ventana_lote'][1]} y esta ejecución "
                  f"sobre #{a.pr_min}..#{a.pr_max}: las métricas del lote no son comparables.", file=sys.stderr)

    sha = git(raiz, "rev-parse", "HEAD")[:8]
    ahora = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%MZ")
    if a.json:
        json.dump({"generado": ahora, "commit": sha, "ventana_lote": [a.pr_min, a.pr_max], "metricas": filas}, sys.stdout, ensure_ascii=False, indent=1)
        print()
    else:
        print(a_markdown(filas, f"Métricas de proceso — {ahora} — commit {sha} — lote #{a.pr_min}..#{a.pr_max}", bool(base)))
        medidas = sum(1 for f in filas if f["clase"] != "NO_MEDIDA")
        print(f"\n{medidas} de {len(filas)} métricas medidas; las NO_MEDIDA llevan su motivo en «Detalle».")
    return 0


if __name__ == "__main__":
    sys.exit(main())
