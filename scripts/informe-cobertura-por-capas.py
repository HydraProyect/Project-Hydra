#!/usr/bin/env python3
"""Informe de cobertura por ensamblado y por zona de riesgo (REC-210).

Lee los dos `Summary.xml` (reporttype `XmlSummary` de reportgenerator, uno por
trinquete — nucleo y Web, ver DEC-cobertura-dos-trinquetes-2026-09-11.md) y
produce una tabla Markdown con:

  1. cobertura de linea por ensamblado (Domain, Application, Infrastructure,
     Web) — el desglose que el JsonSummary agregado nunca mostraba;
  2. cobertura de linea por zona de riesgo (autorizacion, aislamiento de
     tenant/RLS, importacion), calculada agregando linea a linea las clases
     cuyo nombre completo contiene alguna de las palabras clave de la zona;
  3. las clases con mas lineas SIN cubrir de cada ensamblado del nucleo (no
     el peor porcentaje: una clase de 2 lineas al 0% no importa tanto como
     una de 400 al 40%).

Es un INSTRUMENTO MECANICO, no un juicio de riesgo. Match por subcadena en el
nombre completo de la clase (namespace incluido): barato, auditable y
reproducible, pero no distingue "sin cobertura" de "sin importancia" (REC-210
riesgo (b)) ni ve una clase que deberia estar en una zona pero no lleva la
palabra clave en su nombre. Las zonas y sus huecos se declaran en la salida.

Uso:
    python3 informe-cobertura-por-capas.py \
        --nucleo coveragereport/Summary.xml \
        --web coveragereport-web/Summary.xml \
        [--peores N]

Sin --nucleo o sin --web el fichero correspondiente se omite del informe con
un aviso explicito (hueco declarado), en vez de fallar entero: un trinquete
caido no debe apagar la visibilidad del otro.
"""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field

# Zona de riesgo -> palabras clave que se buscan como subcadena en el nombre
# COMPLETO de la clase (namespace + nombre). No son mutuamente excluyentes:
# una clase puede figurar en dos zonas si su nombre lleva las dos palabras.
#
# "autorizacion" y "rls_aislamiento" se mantienen SEPARADAS a proposito
# aunque compartan carpeta en el codigo (AlcanceDatosService vive en
# Infrastructure.Autorizacion): son planos distintos del contrato de
# terminologia — autorizacion decide SI algo esta permitido, aislamiento de
# tenant decide QUE conjunto de datos ve. Mezclarlas en una sola cifra
# escondería una regresión de la una detrás de una mejora de la otra, igual
# que ocurria con Web dentro del universo unico (DEC 2026-09-11).
ZONAS_DE_RIESGO: dict[str, list[str]] = {
    "autorizacion": ["Autorizacion"],
    "rls_aislamiento_tenant": [
        "AlcanceDatos",
        "TenantRls",
        "AmbitoTenant",
        "MultiTenan",
        "TenantSellado",
    ],
    "importacion": ["Importacion"],
}

ENSAMBLADOS_NUCLEO = ["CaeManager.Domain", "CaeManager.Application", "CaeManager.Infrastructure"]
ENSAMBLADOS_WEB = ["CaeManager.Web"]


@dataclass
class Clase:
    nombre: str
    cubiertas: int
    total: int

    @property
    def sin_cubrir(self) -> int:
        return self.total - self.cubiertas

    @property
    def porcentaje(self) -> float | None:
        return (100.0 * self.cubiertas / self.total) if self.total else None


@dataclass
class Ensamblado:
    nombre: str
    cubiertas: int
    total: int
    clases: list[Clase] = field(default_factory=list)

    @property
    def porcentaje(self) -> float | None:
        return (100.0 * self.cubiertas / self.total) if self.total else None


def cargar_xml_summary(ruta: str) -> list[Ensamblado]:
    """Parsea un Summary.xml (reporttype XmlSummary) en Ensamblado/Clase.

    Falla explicitamente (excepcion) si la forma esperada no aparece — un
    XML con otro esquema no debe leerse como "cero ensamblados", que un
    informe vacio confundiria con "cobertura cero" en vez de "instrumento
    equivocado" (protocolo-hydra-verificacion § 3).
    """
    root = ET.parse(ruta).getroot()
    cobertura = root.find("Coverage")
    if cobertura is None:
        raise ValueError(f"{ruta}: no tiene el nodo <Coverage> esperado de XmlSummary")
    ensamblados = []
    for nodo_asm in cobertura.findall("Assembly"):
        clases = [
            Clase(
                nombre=nodo_clase.get("name", ""),
                cubiertas=int(nodo_clase.get("coveredlines", "0")),
                total=int(nodo_clase.get("coverablelines", "0")),
            )
            for nodo_clase in nodo_asm.findall("Class")
        ]
        ensamblados.append(
            Ensamblado(
                nombre=nodo_asm.get("name", ""),
                cubiertas=int(nodo_asm.get("coveredlines", "0")),
                total=int(nodo_asm.get("coverablelines", "0")),
                clases=clases,
            )
        )
    return ensamblados


def fmt_pct(valor: float | None) -> str:
    return f"{valor:.1f} %" if valor is not None else "sin líneas medibles"


def tabla_por_ensamblado(titulo: str, nombres_esperados: list[str], ensamblados: list[Ensamblado]) -> str:
    lineas = [f"### {titulo}", "", "| Ensamblado | Líneas cubiertas | Líneas totales | Cobertura |", "|---|---:|---:|---:|"]
    por_nombre = {a.nombre: a for a in ensamblados}
    faltantes = []
    for nombre in nombres_esperados:
        a = por_nombre.get(nombre)
        if a is None:
            faltantes.append(nombre)
            lineas.append(f"| {nombre} | — | — | **ausente del informe** |")
            continue
        lineas.append(f"| {a.nombre} | {a.cubiertas} | {a.total} | {fmt_pct(a.porcentaje)} |")
    if faltantes:
        lineas.append("")
        lineas.append(
            f"⚠️ Ensamblado(s) esperado(s) y no encontrados en el informe: {', '.join(faltantes)}. "
            "Un ausente no es un cero: revisar el universo medido antes de interpretar la tabla."
        )
    return "\n".join(lineas)


def tabla_por_zona_de_riesgo(ensamblados: list[Ensamblado]) -> str:
    lineas = [
        "### Zonas de riesgo (heurística por nombre de clase — ver cabecera del guion)",
        "",
        "| Zona | Clases detectadas | Líneas cubiertas | Líneas totales | Cobertura |",
        "|---|---:|---:|---:|---:|",
    ]
    for zona, palabras in ZONAS_DE_RIESGO.items():
        clases_zona = [c for a in ensamblados for c in a.clases if any(p in c.nombre for p in palabras)]
        cubiertas = sum(c.cubiertas for c in clases_zona)
        total = sum(c.total for c in clases_zona)
        pct = (100.0 * cubiertas / total) if total else None
        lineas.append(f"| {zona} | {len(clases_zona)} | {cubiertas} | {total} | {fmt_pct(pct)} |")
    lineas.append("")
    lineas.append(
        "Zonas sin ninguna clase detectada indican, ANTES que \"está cubierto\", que las "
        "palabras clave no encontraron nada — comprobar el patrón contra el código actual."
    )
    return "\n".join(lineas)


def peores_clases(ensamblados: list[Ensamblado], top: int) -> str:
    lineas = ["### Mayor concentración de líneas sin cubrir, por ensamblado del núcleo", ""]
    for a in ensamblados:
        candidatas = sorted((c for c in a.clases if c.sin_cubrir > 0), key=lambda c: c.sin_cubrir, reverse=True)[:top]
        if not candidatas:
            continue
        lineas.append(f"**{a.nombre}**")
        lineas.append("")
        lineas.append("| Clase | Líneas sin cubrir | Cobertura |")
        lineas.append("|---|---:|---:|")
        for c in candidatas:
            lineas.append(f"| {c.nombre} | {c.sin_cubrir} | {fmt_pct(c.porcentaje)} |")
        lineas.append("")
    return "\n".join(lineas).rstrip()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--nucleo", help="Summary.xml (XmlSummary) del trinquete núcleo (Domain+Application+Infrastructure)")
    parser.add_argument("--web", help="Summary.xml (XmlSummary) del trinquete Web (bUnit)")
    parser.add_argument("--peores", type=int, default=8, help="cuántas clases listar por ensamblado en la sección de concentración (default 8)")
    args = parser.parse_args()

    # Sin esto, en una consola Windows con página de códigos cp1252 (no
    # UTF-8), imprimir "Núcleo" o "—" revienta con UnicodeEncodeError en vez
    # de imprimir el informe. En ubuntu-latest (CI) ya sale en UTF-8; esto
    # solo hace el guion reproducible también en ejecución local.
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass  # Python < 3.7 sin TextIOWrapper.reconfigure: se deja como esté.

    if not args.nucleo and not args.web:
        print("::error::Ni --nucleo ni --web recibidos: nada que informar.", file=sys.stderr)
        return 1

    salida = ["## Cobertura por capas y por zona de riesgo (REC-210)", ""]
    todas_las_clases_riesgo: list[Ensamblado] = []

    if args.nucleo:
        nucleo = cargar_xml_summary(args.nucleo)
        salida.append(tabla_por_ensamblado("Núcleo (Domain + Application + Infrastructure)", ENSAMBLADOS_NUCLEO, nucleo))
        salida.append("")
        salida.append(peores_clases(nucleo, args.peores))
        salida.append("")
        todas_las_clases_riesgo.extend(nucleo)
    else:
        salida.append("### Núcleo: sin datos — no se recibió `--nucleo` (hueco declarado)")
        salida.append("")

    if args.web:
        web = cargar_xml_summary(args.web)
        salida.append(tabla_por_ensamblado("Web (bUnit)", ENSAMBLADOS_WEB, web))
        salida.append("")
        todas_las_clases_riesgo.extend(web)
    else:
        salida.append("### Web: sin datos — no se recibió `--web` (hueco declarado)")
        salida.append("")

    salida.append(tabla_por_zona_de_riesgo(todas_las_clases_riesgo))

    print("\n".join(salida))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
