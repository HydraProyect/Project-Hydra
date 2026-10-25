// Comprobación del empaquetado de la extensión (E3).
//
// Falla si:
//  - el manifiesto pierde `key`, o la clave cambia (el ID de la extensión deja
//    de ser el fijado);
//  - el zip incluye algo que la extensión no necesita (pruebas, herramientas,
//    claves, documentación, ficheros sueltos) o le falta algo que referencia;
//  - el zip deja de ser reproducible byte a byte;
//  - un cambio en el contenido del paquete no sube la versión.
//
// Cada regla tiene su control negativo: se rompe el paquete a propósito y se
// exige que el validador lo vea. Un validador que siempre dijera «bien» pasaría
// el resto de comprobaciones.
//
// Uso: node extension/pruebas/comprobar-empaquetado.mjs extension
import { cpSync, mkdtempSync, rmSync, utimesSync, readdirSync, statSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import {
  construirZip,
  leerZip,
  crc32,
  empaquetarCarpeta,
  validarPaquete,
  idDeExtension,
  versionAumenta,
  comprobarVersionContraBase,
  cierreDeReferencias,
  sha256,
} from "../herramientas/empaquetado.mjs";

// El ID estable de la extensión en desarrollo. Está escrito aquí A PROPÓSITO,
// aparte del que usa el empaquetador: si alguien cambia la clave del manifiesto,
// este valor obliga a hacerlo a conciencia (lo que se haya configurado con el ID
// dejaría de funcionar).
const ID_ESTABLE = "neojcncpbpfocmpfjfefakjledbfgjlj";

const carpeta = process.argv[2];
if (!carpeta) {
  console.error("Uso: node comprobar-empaquetado.mjs <carpeta de la extensión>");
  process.exit(2);
}
const raiz = resolve(carpeta);

let fallos = 0;
let total = 0;
function comprobar(nombre, condicion) {
  total++;
  console.log(`${condicion ? "OK  " : "MAL "} ${nombre}`);
  if (!condicion) fallos++;
}
const contiene = (problemas, texto) => problemas.some((p) => p.includes(texto));

const real = empaquetarCarpeta(raiz);
const validar = (zip) => validarPaquete(zip, { idEsperado: ID_ESTABLE });

// Variante del paquete real con una mutación sobre sus entradas.
function mutado(fn) {
  const entradas = leerZip(real.zip).map((e) => ({ ruta: e.ruta, datos: Buffer.from(e.datos) }));
  const resultado = fn(entradas);
  return construirZip(Array.isArray(resultado) ? resultado : entradas);
}
function conManifiesto(cambiar) {
  return mutado((entradas) => {
    const m = entradas.find((e) => e.ruta === "manifest.json");
    const json = JSON.parse(m.datos.toString("utf8"));
    cambiar(json);
    m.datos = Buffer.from(JSON.stringify(json, null, 2), "utf8");
  });
}

// ---------------------------------------------------------------- paquete real

const problemasReales = validar(real.zip);
comprobar(`el paquete real pasa la validación${problemasReales.length ? ": " + problemasReales.join(" | ") : ""}`, problemasReales.length === 0);

const manifiestoReal = JSON.parse(leerZip(real.zip).find((e) => e.ruta === "manifest.json").datos.toString("utf8"));
comprobar("el manifiesto tiene `key`", typeof manifiestoReal.key === "string" && manifiestoReal.key.length > 300);
comprobar("el ID derivado de `key` es el fijado", idDeExtension(manifiestoReal.key) === ID_ESTABLE);

const rutasReales = real.ficheros;
for (const necesario of ["manifest.json", "background.js", "content.js", "popup.html", "popup.js", "popup.css", "icons/icon16.png", "icons/icon48.png", "icons/icon128.png"]) {
  comprobar(`el zip contiene ${necesario}`, rutasReales.includes(necesario));
}
comprobar("el zip no contiene pruebas, herramientas ni claves", !rutasReales.some((r) => /(^|\/)(pruebas|herramientas)\//.test(r) || /\.(pem|mjs|md|key)$/i.test(r)));

// Completitud, medida contra el disco y NO contra el cierre de referencias (que
// es lo que usa el empaquetador): todo fichero de la carpeta que no sea de
// pruebas ni de herramientas tiene que ir en el zip, o estar aquí excluido a
// conciencia. Así un fichero cargado por un camino que el cierre no ve (ruta
// calculada, _locales, etc.) hace fallar el CI en vez de faltar en el zip.
const EXCLUIDOS_A_CONCIENCIA = [];
function ficherosDe(dir, rel = "") {
  const salida = [];
  for (const nombre of readdirSync(join(dir, rel))) {
    const r = rel ? `${rel}/${nombre}` : nombre;
    if (statSync(join(dir, r)).isDirectory()) salida.push(...ficherosDe(dir, r));
    else salida.push(r);
  }
  return salida;
}
const enDisco = ficherosDe(raiz).filter((r) => !/^(pruebas|herramientas)\//.test(r) && !EXCLUIDOS_A_CONCIENCIA.includes(r));
const sinEmpaquetar = enDisco.filter((r) => !rutasReales.includes(r));
comprobar(`todo fichero de la extensión va en el zip${sinEmpaquetar.length ? " (faltan: " + sinEmpaquetar.join(", ") + ")" : ""}`, sinEmpaquetar.length === 0);

// La exención de .gitleaks.toml está atada a ESTA clave: si la clave cambia y la
// exención no, gitleaks vuelve a marcar el manifiesto; si la exención se ensancha
// o desaparece, lo ve esta comprobación.
{
  const rutaToml = resolve(raiz, "..", ".gitleaks.toml");
  const toml = existsSync(rutaToml) ? readFileSync(rutaToml, "utf8") : "";
  const prefijo = manifiestoReal.key.slice(0, 100);
  comprobar("la exención de gitleaks contiene el comienzo de la `key` del manifiesto", toml.includes(`${"\x27\x27\x27"}${prefijo}${"\x27\x27\x27"}`));
  // gitleaks lo lee como expresión regular: sin metacaracteres, casa con la clave.
  comprobar("ese comienzo es una expresión regular que casa con la `key`", /^[A-Za-z0-9/]+$/.test(prefijo) && new RegExp(prefijo).test(manifiestoReal.key));
}

// ------------------------------------------------------------- reproducibilidad

comprobar("dos construcciones seguidas dan los mismos bytes", sha256(empaquetarCarpeta(raiz).zip) === sha256(real.zip));

const copia = mkdtempSync(join(tmpdir(), "ext-repro-"));
try {
  cpSync(raiz, copia, { recursive: true });
  // Sellos de tiempo distintos y recorrido en otro orden no cambian el zip.
  const antiguo = new Date("2001-02-03T04:05:06Z");
  (function tocar(dir) {
    for (const nombre of readdirSync(dir).reverse()) {
      const ruta = join(dir, nombre);
      utimesSync(ruta, antiguo, antiguo);
      if (statSync(ruta).isDirectory()) tocar(ruta);
    }
  })(copia);
  comprobar("otro sello de tiempo en los ficheros no cambia el zip", sha256(empaquetarCarpeta(copia).zip) === sha256(real.zip));
} finally {
  rmSync(copia, { recursive: true, force: true });
}

// ------------------------------------------------- controles negativos del validador

{
  const p = validar(conManifiesto((m) => delete m.key));
  comprobar("NEG: manifiesto sin `key` -> falla", contiene(p, "no tiene `key`"));
}
{
  const p = validar(conManifiesto((m) => (m.key = "")));
  comprobar("NEG: `key` vacía -> falla", contiene(p, "no tiene `key`"));
}
{
  // Una clave privada PKCS#8 empieza por otro prefijo que una pública SPKI. Se arma desde bytes,\n  // no como literal base64, para que el escáner de secretos no la tome por una real.
  const cabeceraPkcs8 = [0x30, 0x82, 0x04, 0xbd, 0x02, 0x01, 0x00, 0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00, 0x04, 0x82, 0x04, 0xa7];
  const privadaFalsa = Buffer.concat([Buffer.from(cabeceraPkcs8), Buffer.alloc(300, 1)]).toString("base64");
  const p = validar(conManifiesto((m) => (m.key = privadaFalsa)));
  comprobar("NEG: una clave que no es pública SPKI -> falla", contiene(p, "SPKI"));
}
{
  const otra = manifiestoReal.key.slice(0, -8) + "BBBBAQAB";
  const p = validar(conManifiesto((m) => (m.key = otra)));
  comprobar("NEG: `key` distinta (otro ID) -> falla", contiene(p, "la clave ha cambiado"));
}
{
  const p = validar(conManifiesto((m) => (m.version = "v1")));
  comprobar("NEG: versión con formato inválido -> falla", contiene(p, "formato de Chrome"));
}
for (const extra of ["pruebas/comprobar-algo.mjs", "herramientas/empaquetar.mjs", "clave.pem", "LEEME" + ".md", "suelto.js", ".env", "mapas/content.js.map"]) {
  const p = validar(mutado((e) => e.push({ ruta: extra, datos: Buffer.from("x") })));
  comprobar(`NEG: el zip incluye ${extra} -> falla`, p.length > 0 && contiene(p, extra));
}
{
  const p = validar(mutado((e) => e.filter((x) => x.ruta !== "icons/icon48.png")));
  comprobar("NEG: falta un icono que el manifiesto referencia -> falla", contiene(p, "falta en el zip icons/icon48.png"));
}
{
  const p = validar(mutado((e) => e.filter((x) => x.ruta !== "popup.css")));
  comprobar("NEG: falta la hoja de estilos que el popup referencia -> falla", contiene(p, "falta en el zip popup.css"));
}
{
  const p = validar(mutado((e) => e.filter((x) => x.ruta !== "manifest.json")));
  comprobar("NEG: zip sin manifest.json -> falla", contiene(p, "manifest.json"));
}
{
  const corrupto = Buffer.from(real.zip);
  corrupto[30 + Buffer.byteLength(rutasReales[0]) + 1] ^= 0xff; // dentro de los datos del primer fichero
  const p = validar(corrupto);
  comprobar("NEG: zip con un byte alterado -> falla (CRC)", contiene(p, "no se puede leer"));
}
comprobar("CRC-32 conocido: «123456789» -> cbf43926", crc32(Buffer.from("123456789")) === 0xcbf43926);

// --------------------------------------------------------- cierre de referencias

{
  const ficheros = new Map([
    ["manifest.json", JSON.stringify({ background: { service_worker: "sw.js" }, action: { default_popup: "ui/p.html" } })],
    ["sw.js", 'import { a } from "./lib/a.js";\nexport * from "./lib/b.js";\nimport "./lib/c.js";'],
    ["lib/a.js", "export const a = 1;"],
    ["lib/b.js", "export const b = 1;"],
    ["lib/c.js", "// vacío"],
    ["lib/huerfano.js", "// nadie lo importa"],
    ["ui/p.html", '<link rel="stylesheet" href="p.css"><script src="../lib/a.js"></script><a href="https://x.es/y.css">x</a>'],
    ["ui/p.css", "body{}"],
  ]);
  const { ficheros: cierre, faltan } = cierreDeReferencias((r) => (ficheros.has(r) ? Buffer.from(ficheros.get(r)) : null));
  comprobar("cierre: sigue imports estáticos, HTML y CSS", ["lib/a.js", "lib/b.js", "lib/c.js", "ui/p.css", "ui/p.html", "sw.js"].every((r) => cierre.includes(r)));
  const dinamicos = new Map([
    ["manifest.json", JSON.stringify({ background: { service_worker: "sw.js" } })],
    ["sw.js", 'const m = await import("./d/uno.js"); importScripts("d/dos.js"); chrome.runtime.getURL("d/tres.html"); import(ruta);'],
    ["d/uno.js", ""], ["d/dos.js", ""], ["d/tres.html", '<link href="cuatro.css">'],
    ["d/cuatro.css", '@import "cinco.css"; a{background:url("img/seis.png")}'],
    ["d/cinco.css", ""], ["d/img/seis.png", "x"],
  ]);
  const din = cierreDeReferencias((r) => (dinamicos.has(r) ? Buffer.from(dinamicos.get(r)) : null));
  comprobar("cierre: sigue import(), importScripts, getURL, @import y url()", ["d/uno.js", "d/dos.js", "d/tres.html", "d/cuatro.css", "d/cinco.css", "d/img/seis.png"].every((r) => din.ficheros.includes(r)) && din.faltan.length === 0);
  comprobar("cierre: ignora lo que nadie referencia y los enlaces externos", !cierre.includes("lib/huerfano.js") && faltan.length === 0);
}

// -------------------------------------------------------------------- versiones

comprobar("versión: 0.4.2 > 0.4.1", versionAumenta("0.4.1", "0.4.2"));
comprobar("versión: 0.4.10 > 0.4.9 (orden numérico, no de texto)", versionAumenta("0.4.9", "0.4.10"));
comprobar("versión: 1.0 > 0.9.9", versionAumenta("0.9.9", "1.0"));
comprobar("versión: igual no aumenta", !versionAumenta("0.4.2", "0.4.2"));
comprobar("versión: 1.0 y 1.0.0.0 son la misma", !versionAumenta("1.0", "1.0.0.0"));
comprobar("versión: bajar no aumenta", !versionAumenta("0.5.0", "0.4.9"));

const zipA = construirZip([{ ruta: "manifest.json", datos: Buffer.from("a") }]);
const zipB = construirZip([{ ruta: "manifest.json", datos: Buffer.from("b") }]);
comprobar("base: sin cambios de contenido, la versión puede quedar igual", comprobarVersionContraBase({ zipBase: zipA, versionBase: "1.0", zipActual: zipA, versionActual: "1.0" }) === null);
comprobar("base: cambia el contenido y sube la versión -> bien", comprobarVersionContraBase({ zipBase: zipA, versionBase: "1.0", zipActual: zipB, versionActual: "1.1" }) === null);
comprobar("NEG: cambia el contenido y la versión no sube -> falla", comprobarVersionContraBase({ zipBase: zipA, versionBase: "1.0", zipActual: zipB, versionActual: "1.0" }) !== null);
comprobar("NEG: cambia el contenido y la versión baja -> falla", comprobarVersionContraBase({ zipBase: zipA, versionBase: "1.1", zipActual: zipB, versionActual: "1.0" }) !== null);
comprobar("base: no había extensión en la base -> bien", comprobarVersionContraBase({ zipBase: null, versionBase: null, zipActual: zipB, versionActual: "1.0" }) === null);

// ------------------------------------------------------------- cableado del CLI

{
  const cli = join(fileURLToPath(new URL("../herramientas/", import.meta.url)), "empaquetar.mjs");
  const ejecutar = (...args) => spawnSync(process.execPath, [cli, ...args], { encoding: "utf8" });
  const trabajo = mkdtempSync(join(tmpdir(), "ext-cli-"));
  try {
    const actual = join(trabajo, "actual");
    const base = join(trabajo, "base");
    cpSync(raiz, actual, { recursive: true });
    cpSync(raiz, base, { recursive: true });
    const salida = join(trabajo, "salida");

    let r = ejecutar("--origen", actual, "--salida", salida, "--base", base);
    comprobar("CLI: igual que la base -> sale 0 y escribe el zip", r.status === 0 && existsSync(join(salida, `talveg-extension-${real.version}.zip`)));
    comprobar("CLI: el zip que escribe es el del arnés (mismos bytes)", sha256(readFileSync(join(salida, `talveg-extension-${real.version}.zip`))) === sha256(real.zip));

    // El contenido cambia y la versión no.
    writeFileSync(join(actual, "popup.css"), readFileSync(join(actual, "popup.css"), "utf8") + "\n/* cambio */\n");
    r = ejecutar("--origen", actual, "--salida", join(trabajo, "s2"), "--base", base);
    comprobar("NEG CLI: cambia el contenido sin subir versión -> sale 1", r.status === 1 && r.stderr.includes("versión"));
    comprobar("NEG CLI: y no deja zip", !existsSync(join(trabajo, "s2")));

    // Con la versión subida pasa.
    const man = JSON.parse(readFileSync(join(actual, "manifest.json"), "utf8"));
    const partes = man.version.split(".").map(Number);
    partes[partes.length - 1] += 1;
    man.version = partes.join(".");
    writeFileSync(join(actual, "manifest.json"), JSON.stringify(man, null, 2));
    r = ejecutar("--origen", actual, "--salida", join(trabajo, "s3"), "--base", base);
    comprobar("CLI: cambia el contenido y sube la versión -> sale 0", r.status === 0);

    // Una base que no existe no se perdona en silencio.
    r = ejecutar("--origen", raiz, "--salida", join(trabajo, "s4"), "--base", join(trabajo, "no-existe"));
    comprobar("NEG CLI: --base inexistente -> sale 1", r.status === 1);

    // El manifiesto sin key no genera zip.
    const sinKey = join(trabajo, "sin-key");
    cpSync(raiz, sinKey, { recursive: true });
    const m2 = JSON.parse(readFileSync(join(sinKey, "manifest.json"), "utf8"));
    delete m2.key;
    writeFileSync(join(sinKey, "manifest.json"), JSON.stringify(m2, null, 2));
    r = ejecutar("--origen", sinKey, "--salida", join(trabajo, "s5"));
    comprobar("NEG CLI: manifiesto sin key -> sale 1 y no deja zip", r.status === 1 && !existsSync(join(trabajo, "s5")));
  } finally {
    rmSync(trabajo, { recursive: true, force: true });
  }
}

// Control positivo del instrumento: el validador debe poder decir «bien» y «mal».
comprobar("CONTROL: el validador acepta el real y rechaza el roto", validar(real.zip).length === 0 && validar(conManifiesto((m) => delete m.key)).length > 0);

console.log(fallos === 0 ? "\nTODO OK" : `\n${fallos} FALLO(S)`);
console.log(`${total} comprobaciones ejecutadas.`);
process.exit(fallos === 0 ? 0 : 1);
