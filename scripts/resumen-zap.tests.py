"""Tabla de verdad de resumen-zap.py sobre informes escritos a mano.

Cada caso corre el guion real sobre un informe temporal. Sale con 1 si algún caso
no da el resultado esperado.
"""

import json
import pathlib
import subprocess
import sys
import tempfile


GUION = pathlib.Path(__file__).with_name("resumen-zap.py").resolve()
TITULO = "ZAP de prueba"
FILTRADO = "FILTRADO-NO-PUBLICAR"

# Todo lo que el informe trae de cada instancia y de cada alerta y no debe salir.
INSTANCIA = {
    "uri": f"http://127.0.0.1:5080/cuenta/{FILTRADO}?clave=valor",
    "method": "POST",
    "param": FILTRADO,
    "attack": f"' OR 1=1 -- {FILTRADO}",
    "evidence": FILTRADO,
    "otherinfo": FILTRADO,
    "requestheader": f"Cookie: sesion={FILTRADO}",
    "requestbody": FILTRADO,
    "responseheader": f"Set-Cookie: sesion={FILTRADO}",
    "responsebody": FILTRADO,
}


def alerta(nombre, riesgo="2", confianza="2", cuenta="2", regla="10010"):
    return {
        "pluginid": regla,
        "name": nombre,
        "alert": nombre,
        "riskcode": riesgo,
        "confidence": confianza,
        "riskdesc": f"Medium ({FILTRADO})",
        "desc": FILTRADO,
        "solution": FILTRADO,
        "reference": f"http://ejemplo.invalid/{FILTRADO}",
        "otherinfo": FILTRADO,
        "count": cuenta,
        "instances": [INSTANCIA],
    }


def informe(*alertas_del_sitio):
    return json.dumps({"site": [{"@name": f"http://{FILTRADO}", "alerts": list(alertas_del_sitio)}]})


def fila(*celdas):
    return "| " + " | ".join(celdas) + " |"


DOS_SITIOS = json.dumps(
    {
        "site": [
            {"@name": FILTRADO, "alerts": [alerta("Regla repetida", cuenta="2")]},
            {"@name": FILTRADO, "alerts": [alerta("Regla repetida", cuenta="3")]},
        ]
    }
)

CASOS = [
    (
        "Cuenta por riesgo y por nombre",
        informe(
            alerta("SQL Injection", riesgo="3", cuenta="1"),
            alerta("Absence of Anti-CSRF Tokens", riesgo="2", cuenta="4"),
            alerta("Cookie Slack Detector", riesgo="1", cuenta="2"),
            alerta("Modern Web Application", riesgo="0", cuenta="7"),
        ),
        [
            fila("Alto", "1", "1"),
            fila("Medio", "1", "4"),
            fila("Bajo", "1", "2"),
            fila("Informativo", "1", "7"),
            fila("Alto", "SQL Injection", "1"),
            fila("Medio", "Absence of Anti-CSRF Tokens", "4"),
        ],
        [],
    ),
    (
        "Mismo nombre en dos sitios: dos alertas, instancias sumadas",
        DOS_SITIOS,
        [fila("Medio", "2", "5"), fila("Medio", "Regla repetida", "5")],
        [],
    ),
    (
        "Un falso positivo no cuenta, igual que en «Evaluar hallazgos»",
        informe(alerta("Path Traversal", riesgo="3", confianza="0", cuenta="1")),
        [fila("Alto", "0", "0")],
        ["Path Traversal"],
    ),
    (
        "Nombres de catálogo con comillas, barra y paréntesis se conservan",
        informe(
            alerta('Server Leaks Version Information via "Server" HTTP Response Header Field', riesgo="1"),
            alerta("Source Code Disclosure - /WEB-INF Folder", riesgo="3"),
            alerta("Cross Site Scripting (Reflected)", riesgo="3"),
        ),
        ['via "Server" HTTP', "/WEB-INF Folder", "Cross Site Scripting (Reflected)"],
        ["Regla 10010"],
    ),
    (
        "Un nombre con URL sale como número de regla",
        informe(alerta("Fallo en http://127.0.0.1:5080/cuenta", regla="40012")),
        [fila("Medio", "Regla 40012", "2")],
        ["127.0.0.1"],
    ),
    (
        "Un nombre con un valor sale como número de regla",
        informe(alerta("Fallo con clave=valor?x", regla="40012")),
        [fila("Medio", "Regla 40012", "2")],
        ["clave"],
    ),
    (
        "Un nombre con salto de línea no abre una fila nueva",
        informe(alerta("Alerta\n| Alto | 99 | 99 |", regla="40012")),
        [fila("Medio", "Regla 40012", "2")],
        ["99"],
    ),
    (
        "Un nombre con marcado no llega al resumen",
        informe(alerta("<img src=x onerror=alert(1)>", regla="40012")),
        [fila("Medio", "Regla 40012", "2")],
        ["<img", "onerror"],
    ),
    (
        "Una regla no numérica tampoco se copia",
        informe(alerta("Fallo en http://127.0.0.1", regla="1 OR 1=1")),
        [fila("Medio", "Regla sin identificar", "2")],
        ["OR 1=1"],
    ),
    (
        "Un riesgo desconocido no se pierde",
        informe(alerta("Alerta rara", riesgo="9")),
        [fila("Sin clasificar", "1", "2")],
        [],
    ),
    (
        "Una cuenta no numérica vale cero y no se copia",
        informe(alerta("Alerta rara", cuenta="muchas")),
        [fila("Medio", "1", "0")],
        ["muchas"],
    ),
    (
        "Sin alertas: tabla de riesgos a cero y sin tabla de nombres",
        informe(),
        [fila("Alto", "0", "0"), fila("Informativo", "0", "0")],
        ["| Alerta |", "Sin clasificar"],
    ),
    (
        "Una dirección sin esquema sale como número de regla",
        informe(alerta("Fallo en 127.0.0.1:5080/cuenta/tok123", regla="40012")),
        [fila("Medio", "Regla 40012", "2")],
        ["tok123"],
    ),
    (
        "Una ruta de varios tramos sale como número de regla",
        informe(alerta("Fallo en /cuenta/tok123/x", regla="40012")),
        [fila("Medio", "Regla 40012", "2")],
        ["tok123"],
    ),
    (
        "Una carga de ataque sale como número de regla",
        informe(alerta("Reflejo ' OR 1 -- x", regla="40012"), alerta("Reflejo OR 1 -- x", regla="40014")),
        [fila("Medio", "Regla 40012", "2"), fila("Medio", "Regla 40014", "2")],
        ["Reflejo"],
    ),
    ("JSON roto", '{"site": [' + FILTRADO, ["ZAP no dejó un informe legible."], []),
    ("JSON con otra forma", json.dumps(["no", "es", "un", "informe"]), ["ZAP no dejó un informe legible."], []),
    ("Alertas con otra forma", json.dumps({"site": ["texto"]}), ["ZAP no dejó un informe legible."], []),
    ("site como objeto", json.dumps({"site": {"alerts": [alerta("Alerta suelta")]}}), ["ZAP no dejó un informe legible."], []),
    ("Informe ausente", None, ["ZAP no dejó un informe legible."], []),
]


def ejecutar(contenido, carpeta):
    entrada = pathlib.Path(carpeta) / "informe.json"
    salida = pathlib.Path(carpeta) / "resumen.txt"
    if contenido is not None:
        entrada.write_text(contenido, encoding="utf-8")
    proceso = subprocess.run(
        [sys.executable, str(GUION), str(entrada), str(salida), TITULO],
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    return proceso, salida.read_text(encoding="utf-8") if salida.exists() else ""


def fallos_del_caso(contenido, deben_salir, no_deben_salir):
    with tempfile.TemporaryDirectory() as carpeta:
        proceso, resumen = ejecutar(contenido, carpeta)
    fallos = []
    if proceso.returncode != 0:
        fallos.append(f"salió con {proceso.returncode}")
    if proceso.stdout or proceso.stderr:
        fallos.append("escribió por la salida estándar: el log del run es público")
    if not resumen.startswith(f"### {TITULO}: hallazgos\n"):
        fallos.append("el resumen no empieza por su título")
    for texto in deben_salir:
        if texto not in resumen:
            fallos.append(f"falta «{texto}»")
    for texto in [FILTRADO, *no_deben_salir]:
        if texto in resumen:
            fallos.append(f"ha salido «{texto}»")
    return fallos


def main():
    errores = 0
    for nombre, contenido, deben_salir, no_deben_salir in CASOS:
        fallos = fallos_del_caso(contenido, deben_salir, no_deben_salir)
        if fallos:
            errores += 1
            print(f"FALLA: {nombre}: {'; '.join(fallos)}")
        else:
            print(f"ok: {nombre}")
    if errores:
        print(f"{errores} de {len(CASOS)} casos fallan.")
        return 1
    print(f"{len(CASOS)} casos correctos.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
