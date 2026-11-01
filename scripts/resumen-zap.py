"""Resumen publicable de un informe JSON de ZAP.

Uso: resumen-zap.py INFORME SALIDA TITULO

El repositorio es público y, con él, los artefactos, los logs y los resúmenes de
sus runs. Este guion es lo único que sale del escaneo: recuentos por riesgo y por
nombre de alerta, en Markdown. De cada alerta solo lee riesgo, confianza, nombre,
regla y número de instancias, nunca URL, parámetro, ataque, evidencia, cuerpo ni
cabecera. Por eso el filtro es de lo que se copia y no de lo que se quita: un
campo nuevo en el informe no llega a la salida.

Cuenta como el paso «Evaluar hallazgos» del job de ZAP: `confidence` "0" es un
falso positivo marcado en el plan y no cuenta.

El nombre de una alerta viene del catálogo de reglas, pero el informe lo escribe
ZAP tras atacar la aplicación. Si no parece un nombre de catálogo (trae una URL,
un valor o marcado), sale el número de regla en su lugar.

Sale siempre con 0: el umbral lo decide el job, y un informe ausente o ilegible
ya tiene su propio error allí. Aquí solo se deja dicho en el resumen.
"""

import collections
import json
import re
import sys


RIESGOS = {"3": "Alto", "2": "Medio", "1": "Bajo", "0": "Informativo"}
SIN_CLASIFICAR = "Sin clasificar"
ORDEN = [*RIESGOS.values(), SIN_CLASIFICAR]

NOMBRE_DE_CATALOGO = re.compile(r"[\w .,:;()'\"/&+-]{1,120}")
AVISO = (
    "Solo recuentos: el repositorio es público. La URL, el parámetro y la evidencia "
    "de cada hallazgo no se publican; se obtienen repitiendo el escaneo en local."
)


def alertas(informe):
    sitios = informe.get("site") or []
    if isinstance(sitios, dict):
        sitios = [sitios]
    for sitio in sitios:
        yield from sitio.get("alerts") or []


def nombre_publicable(alerta):
    nombre = str(alerta.get("name") or alerta.get("alert") or "")
    if NOMBRE_DE_CATALOGO.fullmatch(nombre) and "://" not in nombre:
        return nombre
    regla = str(alerta.get("pluginid") or "")
    return f"Regla {regla}" if regla.isascii() and regla.isdigit() else "Regla sin identificar"


def instancias(alerta):
    cuenta = str(alerta.get("count") or "")
    return int(cuenta) if cuenta.isascii() and cuenta.isdigit() else 0


def recontar(informe):
    por_riesgo = collections.Counter()
    instancias_por_riesgo = collections.Counter()
    por_nombre = collections.Counter()
    for alerta in alertas(informe):
        if str(alerta.get("confidence")) == "0":
            continue
        riesgo = RIESGOS.get(str(alerta.get("riskcode")), SIN_CLASIFICAR)
        por_riesgo[riesgo] += 1
        instancias_por_riesgo[riesgo] += instancias(alerta)
        por_nombre[riesgo, nombre_publicable(alerta)] += instancias(alerta)
    return por_riesgo, instancias_por_riesgo, por_nombre


def markdown(titulo, por_riesgo, instancias_por_riesgo, por_nombre):
    lineas = [f"### {titulo}: hallazgos", "", AVISO, "", "| Riesgo | Alertas | Instancias |", "| --- | ---: | ---: |"]
    for riesgo in ORDEN:
        if riesgo != SIN_CLASIFICAR or por_riesgo[riesgo]:
            lineas.append(f"| {riesgo} | {por_riesgo[riesgo]} | {instancias_por_riesgo[riesgo]} |")
    if por_nombre:
        lineas += ["", "| Riesgo | Alerta | Instancias |", "| --- | --- | ---: |"]
        for riesgo, nombre in sorted(por_nombre, key=lambda clave: (ORDEN.index(clave[0]), clave[1])):
            lineas.append(f"| {riesgo} | {nombre} | {por_nombre[riesgo, nombre]} |")
    return "\n".join(lineas) + "\n"


def resumir(ruta, titulo):
    try:
        with open(ruta, encoding="utf-8", errors="replace") as fichero:
            return markdown(titulo, *recontar(json.load(fichero)))
    except (OSError, ValueError, AttributeError, TypeError):
        return f"### {titulo}: hallazgos\n\nZAP no dejó un informe legible.\n"


def main(argumentos):
    if len(argumentos) != 3:
        print("Uso: resumen-zap.py INFORME SALIDA TITULO", file=sys.stderr)
        return 2
    informe, salida, titulo = argumentos
    with open(salida, "w", encoding="utf-8") as fichero:
        fichero.write(resumir(informe, titulo))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
