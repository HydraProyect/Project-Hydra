#!/usr/bin/env python3
"""Verifica la frontera de autoridad documental (docs/README.md).

Un fallo aquí no es un problema de formato: significa que un documento sin autoridad
puede estar gobernando decisiones, que es lo que degradó la documentación antes del
reset de 2026-08.

Uso:  python scripts/validar-gobernanza-docs.py
Salida: 0 si la frontera se mantiene, 1 si hay infracciones.
"""
from __future__ import annotations

import io
import re
import sys
from pathlib import Path

RAIZ = Path(__file__).resolve().parent.parent

NORMATIVOS = sorted(RAIZ.glob("0[1-8]_*.md"))
LOG = RAIZ / "DESIGN_DECISION_LOG.md"
BLUEPRINTS = sorted((RAIZ / "docs" / "blueprints").glob("*.md"))
ARCHIVADOS = sorted((RAIZ / "docs" / "archive").rglob("*.md"))

# Los valores visuales concretos solo pueden vivir donde se especifican.
CON_VALORES = {"02_BRAND_AND_VISUAL_IDENTITY.md", "06_DESIGN_SYSTEM.md"}

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

CABECERA_ARCHIVO = "NO NORMATIVO"

# Solo interesa la autoproclamación **sobre diseño y UX**: que `DOMAIN.md` se declare
# fuente de verdad de su dominio es correcto y no es asunto de esta verificación.
AUTOPROCLAMACION = re.compile(
    r"(fuente de verdad|source of truth|canónic[oa]|normativ[oa])", re.I
)
AMBITO_DISENO = re.compile(
    r"(diseñ|dise�o|\bUX\b|token|component|patrón visual|identidad visual|micro-?interacc)", re.I
)

fallos: list[str] = []
avisos: list[str] = []


def leer(p: Path) -> str:
    # errors="replace": algún .md del repo no es UTF-8 puro y no debe tumbar la verificación
    return io.open(p, encoding="utf-8", errors="replace").read()


def rel(p: Path) -> str:
    return p.relative_to(RAIZ).as_posix()


# 1 · Ningún documento normativo cita un archivado como fuente.
#     Citarlo como evidencia histórica sí está permitido, y se distingue por el contexto.
# README.md es un nombre de índice demasiado genérico para casar por nombre suelto
nombres_archivados = {p.name for p in ARCHIVADOS} - {"README.md"}
for p in NORMATIVOS + BLUEPRINTS + ([LOG] if LOG.exists() else []):
    texto = leer(p)
    for linea in texto.splitlines():
        for nombre in nombres_archivados:
            # (?<![\w-]) evita que `UX_PATTERNS.md` case dentro de `04_UX_PATTERNS.md`
            citado = re.search(r"(?<![\w-])" + re.escape(nombre), linea)
            if citado and "archive" not in linea:
                contexto_ok = any(
                    marca in linea.lower()
                    for marca in ("históric", "evidencia", "sustitu", "reemplaza", "derogad", "anterior")
                )
                if not contexto_ok:
                    fallos.append(
                        f"{rel(p)}: cita `{nombre}` (archivado) sin marcarlo como histórico\n"
                        f"    -> {linea.strip()[:110]}"
                    )

# 2 · El índice del archivo declara la carpeta entera como no normativa, y cada documento
#     de diseño archivado lo declara además en su cabecera: son los que contienen reglas
#     concretas que alguien podría copiar a código sin darse cuenta.
indice = RAIZ / "docs" / "archive" / "README.md"
if not indice.exists() or CABECERA_ARCHIVO not in leer(indice):
    fallos.append("docs/archive/README.md: no declara la carpeta como NO NORMATIVO")
for p in ARCHIVADOS:
    if "design" not in p.parts:
        continue
    if CABECERA_ARCHIVO not in "\n".join(leer(p).splitlines()[:25]):
        fallos.append(f"{rel(p)}: diseño archivado sin cabecera '{CABECERA_ARCHIVO}'")

# 3 · Nadie fuera de la cadena se autodenomina normativo o fuente de verdad.
en_cadena = {rel(p) for p in NORMATIVOS + BLUEPRINTS} | {"DESIGN_DECISION_LOG.md", "docs/README.md"}
for p in RAIZ.rglob("*.md"):
    if any(x in p.parts for x in (".claude", "node_modules", "bin", "obj")):
        continue
    r = rel(p)
    if r in en_cadena or r.startswith("docs/archive/"):
        continue
    for linea in leer(p).splitlines():
        if AUTOPROCLAMACION.search(linea) and AMBITO_DISENO.search(linea):
            avisos.append(
                f"{r}: se atribuye autoridad sobre diseño/UX fuera de la cadena\n"
                f"    -> {linea.strip()[:110]}"
            )

# 4 · Los valores visuales concretos solo en 02 y 06.
for p in NORMATIVOS + BLUEPRINTS:
    if p.name in CON_VALORES:
        continue
    hexes = set(re.findall(r"#[0-9A-Fa-f]{6}\b", leer(p)))
    if hexes:
        fallos.append(f"{rel(p)}: define valores visuales ({', '.join(sorted(hexes))}); van en `02`/`06`")

# 5 · Todo normativo declara estado y límite de implementación (DDL-023).
for p in NORMATIVOS + [b for b in BLUEPRINTS if b.name != "README.md"]:
    texto = leer(p)
    if "**Estado**" not in texto:
        fallos.append(f"{rel(p)}: no declara **Estado**")
    if "**Implementado hasta**" not in texto:
        fallos.append(f"{rel(p)}: no declara **Implementado hasta** (DDL-023)")

# 6 · Toda decisión citada existe en el Decision Log.
if LOG.exists():
    existentes = set(re.findall(r"^### (DDL-\d+)", leer(LOG), re.M))
    for p in NORMATIVOS + BLUEPRINTS + [RAIZ / "CLAUDE.md", RAIZ / "docs" / "README.md"]:
        if not p.exists():
            continue
        for ddl in sorted(set(re.findall(r"DDL-\d+", leer(p)))):
            if ddl not in existentes:
                fallos.append(f"{rel(p)}: cita {ddl}, que no existe en el Decision Log")
else:
    fallos.append("Falta DESIGN_DECISION_LOG.md: no hay autoridad sobre decisiones")

# ---------------------------------------------------------------- salida
print(f"Gobernanza documental — {len(NORMATIVOS)} normativos · "
      f"{len(BLUEPRINTS)} blueprints · {len(ARCHIVADOS)} archivados\n")

for a in avisos:
    print(f"  aviso   {a}")
for f in fallos:
    print(f"  FALLO   {f}")

if fallos:
    print(f"\n{len(fallos)} infracción(es) de la frontera de autoridad. Ver docs/README.md.")
    sys.exit(1)

print("  Frontera de autoridad intacta." if not avisos else "\n  Sin fallos; revisa los avisos.")
sys.exit(0)
