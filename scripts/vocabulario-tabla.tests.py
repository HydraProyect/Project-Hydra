#!/usr/bin/env python3
"""Pruebas de scripts/vocabulario-tabla.py (S1), con un vocabulario sintético de resultado conocido y con el real.

La propiedad que importa no es «el guion corre»: es que la tabla del contrato sale de la fuente única y
de nada más. Cambiar el JSON cambia la tabla; una tabla tocada a mano se detecta; reescribir el contrato
no toca ni un byte fuera de las marcas (ni el fin de línea del documento); y un patrón roto o sin
ejemplos se dice, no se calla.

Se ejecuta con scripts/vocabulario-tabla.tests.sh, que además lo lanza contra copias MUTADAS del guion y
exige que fallen.  VOCABULARIO_SCRIPT apunta al guion bajo prueba (por defecto, el de al lado).
"""
import copy
import importlib.util
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

RUTA = Path(os.environ.get("VOCABULARIO_SCRIPT", Path(__file__).resolve().parent / "vocabulario-tabla.py"))
spec = importlib.util.spec_from_file_location("vocabulario_tabla", RUTA)
vt = importlib.util.module_from_spec(spec)
sys.modules["vocabulario_tabla"] = vt
spec.loader.exec_module(vt)

SINTETICO = {
    "version": 7,
    "descartarAntesDeCasar": [r"https?://\S+"],
    "canonicos": [
        {"id": "a", "forma": "Alfa beta", "plural": "Alfas beta", "formasCortasPermitidas": ["Alfa", "A|B"], "significado": "El alfa | con barra",
         "contrato": "§ 1", "noConfundirCon": "Gamma"},
        {"id": "b", "forma": "Delta", "plural": None, "formasCortasPermitidas": [], "significado": "Otro", "contrato": "§ 2", "noConfundirCon": None},
    ],
    "prohibidos": [
        {"id": "x-a-secas", "termino": "Xi", "patron": r"\bxi\b(?!\s+omicron)", "ignorarMayusculas": True, "sustitucion": "Xi omicron",
         "motivo": "Porque sí", "contrato": "§ 3", "sentido": None, "casa": ["Xi"], "noCasa": ["Xi omicron", "http://xi.example/x"]},
    ],
    "excepciones": [
        {"id": "e1", "prohibidos": ["x-a-secas"], "ficheros": ["src/A/*.resx"], "claves": ["K1", "K2"], "contexto": "plantilla",
         "motivo": "Es el nombre de la columna"},
    ],
}

CONTRATO = "# Contrato\r\nantes de las marcas\r\n<!-- generado: no editar -->\r\nVIEJO\r\n<!-- /generado -->\r\ndespués de las marcas\r\n"


def escribir(carpeta: Path, nombre: str, texto: str, binario=False) -> Path:
    p = carpeta / nombre
    if binario:
        p.write_bytes(texto.encode("utf-8"))
    else:
        p.write_text(texto, encoding="utf-8", newline="")
    return p


class PruebasDeLaTabla(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def test_la_tabla_sintetica_tiene_las_filas_y_cifras_exactas(self):
        t = vt.generar(SINTETICO)
        filas = [l for l in t.splitlines() if l.startswith("| ") and not l.startswith("|---")]
        # 1 cabecera + 2 canónicos; 1 cabecera + 1 prohibido; 1 cabecera + 1 excepción.
        self.assertEqual(len(filas), 3 + 2 + 2)
        self.assertIn("versión 7", t)
        self.assertIn("| **Alfa beta** | Alfas beta | Alfa, A\\|B |", t)
        self.assertIn("| **Delta** | — | — |", t, "plural ausente y sin formas cortas se rotulan «—», no se dejan vacíos")
        self.assertIn("| «Xi» | Xi omicron | Porque sí | — | § 3 |", t)
        self.assertIn("`e1`", t)
        self.assertIn("claves: `K1`, `K2`", t)
        self.assertIn("«Xi»", t.split("**Excepciones por contexto**")[1], "la excepción nombra el término, no el id")

    def test_la_barra_vertical_de_una_celda_se_escapa_y_no_rompe_la_tabla(self):
        t = vt.generar(SINTETICO)
        linea = next(l for l in t.splitlines() if "Alfa beta" in l and l.startswith("| **"))
        self.assertIn("El alfa \\| con barra", linea)
        self.assertNotIn("\\\\|", linea, "una barra ya escapada no se vuelve a escapar (lista() y fila() no deben duplicar el escapado)")
        # Tras quitar las barras escapadas, quedan exactamente 7 separadores (6 celdas).
        self.assertEqual(linea.replace("\\|", "").count("|"), 7)

    def test_cambiar_el_json_cambia_la_tabla(self):
        otro = copy.deepcopy(SINTETICO)
        otro["canonicos"][0]["forma"] = "Alfa gamma"
        self.assertNotEqual(vt.generar(SINTETICO), vt.generar(otro))
        self.assertIn("Alfa gamma", vt.generar(otro))
        sin_excepciones = copy.deepcopy(SINTETICO)
        sin_excepciones["excepciones"] = []
        self.assertIn("Ninguna.", vt.generar(sin_excepciones))

    def test_aplicar_solo_toca_lo_que_hay_entre_las_marcas_y_conserva_el_crlf(self):
        doc = escribir(self.dir, "c.txt", CONTRATO)
        self.assertTrue(vt.aplicar(doc, SINTETICO))
        nuevo = doc.read_bytes().decode("utf-8")
        self.assertTrue(nuevo.startswith("# Contrato\r\nantes de las marcas\r\n<!-- generado: no editar -->"))
        self.assertTrue(nuevo.endswith("<!-- /generado -->\r\ndespués de las marcas\r\n"))
        self.assertNotIn("VIEJO", nuevo)
        self.assertNotIn("\n", nuevo.replace("\r\n", ""), "todo fin de línea del documento sigue siendo CRLF")
        self.assertIn("Alfa beta", nuevo)

    def test_aplicar_es_idempotente(self):
        doc = escribir(self.dir, "c.txt", CONTRATO)
        vt.aplicar(doc, SINTETICO)
        antes = doc.read_bytes()
        self.assertFalse(vt.aplicar(doc, SINTETICO), "una segunda aplicación no cambia nada")
        self.assertEqual(doc.read_bytes(), antes)

    def test_aplicar_respeta_un_documento_con_lf(self):
        doc = escribir(self.dir, "c.txt", CONTRATO.replace("\r\n", "\n"))
        vt.aplicar(doc, SINTETICO)
        self.assertNotIn(b"\r", doc.read_bytes())

    def test_verificar_ok_tras_aplicar_y_rojo_si_se_toca_la_tabla_a_mano(self):
        doc = escribir(self.dir, "c.txt", CONTRATO)
        vt.aplicar(doc, SINTETICO)
        self.assertEqual(vt.verificar(doc, SINTETICO), [])
        doc.write_bytes(doc.read_bytes().replace(b"Alfas beta", b"Alfas BETA"))
        d = vt.verificar(doc, SINTETICO)
        self.assertTrue(d, "una celda cambiada a mano debe dar diferencias")
        self.assertTrue(any("BETA" in l for l in d))

    def test_verificar_rojo_si_cambia_el_json_y_el_contrato_no(self):
        doc = escribir(self.dir, "c.txt", CONTRATO)
        vt.aplicar(doc, SINTETICO)
        otro = copy.deepcopy(SINTETICO)
        otro["prohibidos"][0]["sustitucion"] = "Otra cosa"
        self.assertTrue(vt.verificar(doc, otro))

    def test_las_marcas_ausentes_o_repetidas_son_entrada_incorrecta(self):
        for texto in ("sin marcas\n", CONTRATO + CONTRATO, "<!-- /generado -->\n<!-- generado: no editar -->\n"):
            doc = escribir(self.dir, "m.txt", texto)
            with self.assertRaises(vt.EntradaIncorrecta):
                vt.aplicar(doc, SINTETICO)
            with self.assertRaises(vt.EntradaIncorrecta):
                vt.verificar(doc, SINTETICO)

    def test_un_json_sin_campos_obligatorios_o_ausente_es_entrada_incorrecta(self):
        p = escribir(self.dir, "j.json", json.dumps({"version": 1}))
        with self.assertRaises(vt.EntradaIncorrecta):
            vt.cargar(p)
        with self.assertRaises(vt.EntradaIncorrecta):
            vt.cargar(self.dir / "no-existe.json")
        p2 = escribir(self.dir, "roto.json", "{ no es json")
        with self.assertRaises(vt.EntradaIncorrecta):
            vt.cargar(p2)

    def test_los_ejemplos_detectan_un_patron_ciego_uno_demasiado_ancho_y_uno_sin_ejemplos(self):
        self.assertEqual(vt.comprobar_ejemplos(SINTETICO), [])
        ciego = copy.deepcopy(SINTETICO)
        ciego["prohibidos"][0]["patron"] = r"\bnunca\b"
        self.assertTrue(any("debía cazar" in f for f in vt.comprobar_ejemplos(ciego)))
        sin_descarte = copy.deepcopy(SINTETICO)
        sin_descarte["descartarAntesDeCasar"] = []
        self.assertTrue(any("http://xi.example" in f for f in vt.comprobar_ejemplos(sin_descarte)),
                        "el ejemplo con URL solo es legítimo porque la URL se descarta antes de casar")
        ancho = copy.deepcopy(SINTETICO)
        ancho["prohibidos"][0]["patron"] = r"\bxi\b"
        self.assertTrue(any("no debía cazar" in f for f in vt.comprobar_ejemplos(ancho)))
        sin = copy.deepcopy(SINTETICO)
        sin["prohibidos"][0]["casa"] = []
        self.assertTrue(any("sin ejemplos" in f for f in vt.comprobar_ejemplos(sin)))
        roto = copy.deepcopy(SINTETICO)
        roto["prohibidos"][0]["patron"] = "(sin cerrar"
        self.assertTrue(any("no compila" in f for f in vt.comprobar_ejemplos(roto)))

    def test_el_vocabulario_real_pasa_sus_ejemplos_y_su_tabla_nombra_cada_termino(self):
        datos = vt.cargar(vt.RUTA_JSON)
        self.assertEqual(vt.comprobar_ejemplos(datos), [])
        t = vt.generar(datos)
        for c in datos["canonicos"]:
            self.assertIn(c["forma"], t)
        for p in datos["prohibidos"]:
            self.assertIn(f"«{p['termino']}»", t)
        for e in datos["excepciones"]:
            self.assertIn(f"`{e['id']}`", t)
        self.assertGreaterEqual(len(datos["prohibidos"]), 10)
        # Suelo 5 → 4 el 2026-10-09: de ocho excepciones quedan cuatro, al retirarse las de «Cliente» a secas con su término (decisión de rótulo).
        self.assertGreaterEqual(len(datos["excepciones"]), 4)


if __name__ == "__main__":
    unittest.main()
