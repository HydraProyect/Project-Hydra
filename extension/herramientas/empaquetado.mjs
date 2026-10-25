// Empaquetado de la extensión de navegador de TALVEG. Herramienta de build: NO
// forma parte del paquete (el paquete se calcula por alcanzabilidad desde el
// manifiesto, y nada apunta a esta carpeta).
//
// Sin dependencias: solo `node:` integrado. Lo usan `empaquetar.mjs` (CI) y el
// arnés `pruebas/comprobar-empaquetado.mjs`.
//
// QUÉ GARANTIZA
//  - El zip contiene exactamente lo que la extensión necesita para cargar: el
//    manifiesto y todo lo que este referencia (iconos, popup, service worker,
//    content scripts), más lo que el popup y los scripts referencian a su vez
//    (hoja de estilos, módulos importados). Nada más: ni `pruebas/`, ni esta
//    carpeta, ni claves, ni documentación.
//  - Es reproducible byte a byte: entradas en orden ordinal, sello de tiempo
//    fijo (1980-01-01), atributos fijos, sin compresión (así los bytes no
//    dependen de la versión de zlib del runner).
//  - El manifiesto lleva `key` (clave PÚBLICA, SPKI RSA) y de ella sale un ID de
//    extensión estable. La clave privada no existe en el repositorio.

import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { inflateRawSync } from "node:zlib";
import { posix, join } from "node:path";

// ---------------------------------------------------------------- referencias

const EXTENSIONES_PROHIBIDAS = [".pem", ".key", ".crx", ".p12", ".pfx", ".map", ".md", ".mjs", ".env", ".zip"];
const CARPETAS_PROHIBIDAS = new Set(["pruebas", "herramientas", "node_modules", ".git"]);

function sinParametros(ruta) {
  return ruta.split("#")[0].split("?")[0];
}

function esLocal(ruta) {
  return ruta && !/^([a-z][a-z0-9+.-]*:|\/\/|#)/i.test(ruta);
}

function normalizar(desdeFichero, ruta) {
  const limpia = sinParametros(ruta);
  const base = limpia.startsWith("/") ? limpia.slice(1) : posix.join(posix.dirname(desdeFichero), limpia);
  return posix.normalize(base);
}

/** Rutas que el manifiesto referencia (relativas a la raíz de la extensión). */
export function referenciasDelManifiesto(manifiesto) {
  const salida = new Set();
  const añadir = (ruta) => {
    if (typeof ruta === "string" && esLocal(ruta)) salida.add(normalizar("manifest.json", ruta));
  };
  for (const ruta of Object.values(manifiesto.icons ?? {})) añadir(ruta);
  const accion = manifiesto.action ?? manifiesto.browser_action ?? manifiesto.page_action ?? {};
  añadir(accion.default_popup);
  const iconos = accion.default_icon;
  if (typeof iconos === "string") añadir(iconos);
  else for (const ruta of Object.values(iconos ?? {})) añadir(ruta);
  añadir(manifiesto.background?.service_worker);
  añadir(manifiesto.options_page);
  añadir(manifiesto.options_ui?.page);
  añadir(manifiesto.side_panel?.default_path);
  for (const cs of manifiesto.content_scripts ?? []) {
    for (const ruta of [...(cs.js ?? []), ...(cs.css ?? [])]) añadir(ruta);
  }
  for (const grupo of manifiesto.web_accessible_resources ?? []) {
    for (const ruta of grupo.resources ?? []) {
      if (typeof ruta === "string" && !ruta.includes("*")) añadir(ruta);
    }
  }
  return salida;
}

/** Recursos locales que un HTML referencia con src= o href=. */
export function referenciasDeHtml(ruta, texto) {
  const salida = new Set();
  for (const m of texto.matchAll(/\b(?:src|href)\s*=\s*["']([^"']+)["']/gi)) {
    if (esLocal(m[1])) salida.add(normalizar(ruta, m[1]));
  }
  return salida;
}

/** Módulos relativos que un JS importa de forma estática. */
export function referenciasDeJs(ruta, texto) {
  const salida = new Set();
  for (const m of texto.matchAll(/(?:^|[;\s])(?:import|export)\s[^"'`;]*?from\s*["']([^"']+)["']|(?:^|[;\s])import\s*["']([^"']+)["']/gm)) {
    const destino = m[1] ?? m[2];
    if (destino && (destino.startsWith("./") || destino.startsWith("../") || destino.startsWith("/"))) {
      salida.add(normalizar(ruta, destino));
    }
  }
  // import("./x.js"), importScripts("x.js"), chrome.runtime.getURL("x.html"):
  // solo cuentan los literales; una ruta calculada no se puede seguir y la
  // revisa el arnés (todo fichero de la carpeta debe estar en el zip o excluido).
  for (const m of texto.matchAll(/\b(?:import|getURL)\s*\(\s*["']([^"']+)["']\s*\)|\bimportScripts\s*\(\s*["']([^"']+)["']/g)) {
    const destino = m[1] ?? m[2];
    if (destino && esLocal(destino) && !destino.startsWith("data:")) salida.add(normalizar(ruta, destino));
  }
  return salida;
}

/** Recursos que una hoja de estilos referencia con url() o @import. */
export function referenciasDeCss(ruta, texto) {
  const salida = new Set();
  for (const m of texto.matchAll(/url\(\s*["']?([^"')]+)["']?\s*\)|@import\s+["']([^"']+)["']/gi)) {
    const destino = m[1] ?? m[2];
    if (destino && esLocal(destino)) salida.add(normalizar(ruta, destino));
  }
  return salida;
}

/**
 * Cierre de referencias desde `manifest.json`. `leer(ruta)` devuelve un Buffer
 * o null si no existe. Devuelve { ficheros: string[] ordenados, faltan: string[] }.
 */
export function cierreDeReferencias(leer) {
  const crudo = leer("manifest.json");
  if (!crudo) return { ficheros: [], faltan: ["manifest.json"] };
  const manifiesto = JSON.parse(crudo.toString("utf8"));
  const pendientes = ["manifest.json", ...referenciasDelManifiesto(manifiesto)];
  const vistos = new Set();
  const faltan = [];
  while (pendientes.length) {
    const ruta = pendientes.pop();
    if (vistos.has(ruta)) continue;
    vistos.add(ruta);
    const datos = leer(ruta);
    if (!datos) {
      faltan.push(ruta);
      continue;
    }
    if (ruta.endsWith(".html")) pendientes.push(...referenciasDeHtml(ruta, datos.toString("utf8")));
    else if (ruta.endsWith(".js")) pendientes.push(...referenciasDeJs(ruta, datos.toString("utf8")));
    else if (ruta.endsWith(".css")) pendientes.push(...referenciasDeCss(ruta, datos.toString("utf8")));
  }
  const ficheros = [...vistos].filter((r) => !faltan.includes(r)).sort(ordinal);
  return { ficheros, faltan: faltan.sort(ordinal) };
}

function ordinal(a, b) {
  return a < b ? -1 : a > b ? 1 : 0;
}

// ------------------------------------------------------------------ escritor

const TABLA_CRC = (() => {
  const tabla = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    tabla[n] = c >>> 0;
  }
  return tabla;
})();

export function crc32(buffer) {
  let c = 0xffffffff;
  for (const byte of buffer) c = TABLA_CRC[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

const FECHA_DOS_FIJA = 0x0021; // 1980-01-01
const HORA_DOS_FIJA = 0x0000;
const ATRIBUTOS_EXTERNOS = ((0o100644 << 16) >>> 0);

/** Zip reproducible. `entradas`: [{ ruta, datos: Buffer }]. */
export function construirZip(entradas) {
  const ordenadas = [...entradas].sort((a, b) => ordinal(a.ruta, b.ruta));
  const locales = [];
  const centrales = [];
  let desplazamiento = 0;
  for (const { ruta, datos } of ordenadas) {
    const nombre = Buffer.from(ruta, "utf8");
    const crc = crc32(datos);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6);
    local.writeUInt16LE(0, 8); // sin compresión
    local.writeUInt16LE(HORA_DOS_FIJA, 10);
    local.writeUInt16LE(FECHA_DOS_FIJA, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(datos.length, 18);
    local.writeUInt32LE(datos.length, 22);
    local.writeUInt16LE(nombre.length, 26);
    local.writeUInt16LE(0, 28);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE((3 << 8) | 20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(0, 10);
    central.writeUInt16LE(HORA_DOS_FIJA, 12);
    central.writeUInt16LE(FECHA_DOS_FIJA, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(datos.length, 20);
    central.writeUInt32LE(datos.length, 24);
    central.writeUInt16LE(nombre.length, 28);
    central.writeUInt16LE(0, 30);
    central.writeUInt16LE(0, 32);
    central.writeUInt16LE(0, 34);
    central.writeUInt16LE(0, 36);
    central.writeUInt32LE(ATRIBUTOS_EXTERNOS, 38);
    central.writeUInt32LE(desplazamiento, 42);
    locales.push(local, nombre, datos);
    centrales.push(central, nombre);
    desplazamiento += local.length + nombre.length + datos.length;
  }
  const directorio = Buffer.concat(centrales);
  const fin = Buffer.alloc(22);
  fin.writeUInt32LE(0x06054b50, 0);
  fin.writeUInt16LE(ordenadas.length, 8);
  fin.writeUInt16LE(ordenadas.length, 10);
  fin.writeUInt32LE(directorio.length, 12);
  fin.writeUInt32LE(desplazamiento, 16);
  return Buffer.concat([...locales, directorio, fin]);
}

/** Lee un zip (almacenado o deflate) y verifica el CRC. Devuelve [{ ruta, datos }]. */
export function leerZip(zip) {
  let fin = -1;
  for (let i = zip.length - 22; i >= 0; i--) {
    if (zip.readUInt32LE(i) === 0x06054b50) {
      fin = i;
      break;
    }
  }
  if (fin < 0) throw new Error("zip sin directorio central");
  const total = zip.readUInt16LE(fin + 10);
  let p = zip.readUInt32LE(fin + 16);
  const salida = [];
  for (let n = 0; n < total; n++) {
    if (zip.readUInt32LE(p) !== 0x02014b50) throw new Error("directorio central corrupto");
    const metodo = zip.readUInt16LE(p + 10);
    const crc = zip.readUInt32LE(p + 16);
    const tamComprimido = zip.readUInt32LE(p + 20);
    const lNombre = zip.readUInt16LE(p + 28);
    const lExtra = zip.readUInt16LE(p + 30);
    const lComentario = zip.readUInt16LE(p + 32);
    const local = zip.readUInt32LE(p + 42);
    const ruta = zip.subarray(p + 46, p + 46 + lNombre).toString("utf8");
    const inicio = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
    const crudo = zip.subarray(inicio, inicio + tamComprimido);
    const datos = metodo === 0 ? Buffer.from(crudo) : metodo === 8 ? inflateRawSync(crudo) : null;
    if (!datos) throw new Error(`método de compresión no soportado en ${ruta}`);
    if (crc32(datos) !== crc) throw new Error(`CRC incorrecto en ${ruta}`);
    salida.push({ ruta, datos });
    p += 46 + lNombre + lExtra + lComentario;
  }
  return salida;
}

// ------------------------------------------------------------ disco → paquete

export function lectorDeDisco(raiz) {
  return (ruta) => {
    if (ruta.split("/").includes("..")) return null;
    try {
      return readFileSync(join(raiz, ...ruta.split("/")));
    } catch {
      return null;
    }
  };
}

/** Construye el zip a partir de la carpeta de la extensión. */
export function empaquetarCarpeta(raiz) {
  const leer = lectorDeDisco(raiz);
  const { ficheros, faltan } = cierreDeReferencias(leer);
  if (faltan.length) throw new Error(`El manifiesto referencia ficheros que no existen: ${faltan.join(", ")}`);
  const zip = construirZip(ficheros.map((ruta) => ({ ruta, datos: leer(ruta) })));
  const manifiesto = JSON.parse(leer("manifest.json").toString("utf8"));
  return { zip, version: manifiesto.version, ficheros };
}

export function sha256(buffer) {
  return createHash("sha256").update(buffer).digest("hex");
}

// ---------------------------------------------------------------- validación

const PREFIJO_SPKI_RSA = Buffer.from("30820122300d06092a864886f70d01010105000382010f00", "hex");

/** ID de extensión de Chrome derivado de la clave pública (base64 SPKI). */
export function idDeExtension(claveBase64) {
  const der = Buffer.from(claveBase64, "base64");
  return [...createHash("sha256").update(der).digest().subarray(0, 16)]
    .map((b) => String.fromCharCode(97 + (b >> 4)) + String.fromCharCode(97 + (b & 15)))
    .join("");
}

/**
 * Comprueba un paquete ya construido. Devuelve la lista de problemas (vacía = bueno).
 * `idEsperado`: el ID estable que el equipo ha fijado; si cambia la clave, cambia el ID.
 */
export function validarPaquete(zip, { idEsperado } = {}) {
  const problemas = [];
  let entradas;
  try {
    entradas = leerZip(zip);
  } catch (e) {
    return [`el zip no se puede leer: ${e.message}`];
  }
  const porRuta = new Map(entradas.map((e) => [e.ruta, e.datos]));
  if (porRuta.size !== entradas.length) problemas.push("el zip repite rutas");

  const manifestoCrudo = porRuta.get("manifest.json");
  if (!manifestoCrudo) return [...problemas, "el zip no contiene manifest.json en la raíz"];
  let manifiesto;
  try {
    manifiesto = JSON.parse(manifestoCrudo.toString("utf8"));
  } catch {
    return [...problemas, "manifest.json del zip no es JSON válido"];
  }

  // 1. La clave pública fija el ID.
  const clave = manifiesto.key;
  if (typeof clave !== "string" || clave.length === 0) {
    problemas.push("el manifiesto no tiene `key`: la extensión perdería el ID estable en desarrollo");
  } else if (!/^[A-Za-z0-9+/]+={0,2}$/.test(clave)) {
    problemas.push("`key` no es base64");
  } else {
    const der = Buffer.from(clave, "base64");
    if (!der.subarray(0, PREFIJO_SPKI_RSA.length).equals(PREFIJO_SPKI_RSA)) {
      problemas.push("`key` no es una clave pública RSA de 2048 bits en formato SPKI (¿se pegó una clave privada?)");
    } else if (idEsperado && idDeExtension(clave) !== idEsperado) {
      problemas.push(`el ID derivado de \`key\` es ${idDeExtension(clave)} y no ${idEsperado}: la clave ha cambiado`);
    }
  }

  // 2. Versión en formato de Chrome: 1 a 4 enteros.
  if (typeof manifiesto.version !== "string" || !/^\d+(\.\d+){0,3}$/.test(manifiesto.version)) {
    problemas.push("`version` no tiene el formato de Chrome (1 a 4 enteros separados por puntos)");
  }

  // 3. Ni carpetas ni extensiones que no tocan, ni rutas sospechosas.
  for (const ruta of porRuta.keys()) {
    const partes = ruta.split("/");
    if (ruta.startsWith("/") || ruta.includes("\\") || partes.includes("..")) {
      problemas.push(`ruta no permitida en el zip: ${ruta}`);
    }
    if (partes.slice(0, -1).some((p) => CARPETAS_PROHIBIDAS.has(p))) {
      problemas.push(`el zip incluye ${ruta}: carpeta de pruebas o herramientas`);
    }
    const nombre = partes[partes.length - 1].toLowerCase();
    if (EXTENSIONES_PROHIBIDAS.some((ext) => nombre.endsWith(ext)) || nombre.startsWith(".env")) {
      problemas.push(`el zip incluye ${ruta}: tipo de fichero que no forma parte de la extensión`);
    }
  }

  // 4. Exactamente el cierre del manifiesto: ni de más ni de menos.
  const { ficheros, faltan } = cierreDeReferencias((r) => porRuta.get(r) ?? null);
  for (const ruta of faltan) problemas.push(`falta en el zip ${ruta}, que la extensión referencia`);
  const esperados = new Set(ficheros);
  for (const ruta of porRuta.keys()) {
    if (!esperados.has(ruta)) problemas.push(`el zip incluye ${ruta}, que la extensión no referencia`);
  }
  return problemas;
}

// ------------------------------------------------------------------ versiones

function partesDeVersion(version) {
  const partes = String(version).split(".").map(Number);
  if (partes.length < 1 || partes.length > 4 || partes.some((n) => !Number.isInteger(n) || n < 0)) {
    throw new Error(`versión inválida: ${version}`);
  }
  while (partes.length < 4) partes.push(0);
  return partes;
}

/** true si `nueva` es estrictamente mayor que `anterior` (orden numérico de Chrome). */
export function versionAumenta(anterior, nueva) {
  const a = partesDeVersion(anterior);
  const b = partesDeVersion(nueva);
  for (let i = 0; i < 4; i++) {
    if (b[i] !== a[i]) return b[i] > a[i];
  }
  return false;
}

/**
 * Si el paquete cambió respecto a la base, la versión debe subir. Devuelve un
 * problema o null. `zipBase` puede ser null (no había extensión en la base).
 */
export function comprobarVersionContraBase({ zipBase, versionBase, zipActual, versionActual }) {
  if (!zipBase) return null;
  if (sha256(zipBase) === sha256(zipActual)) return null;
  if (versionAumenta(versionBase, versionActual)) return null;
  return `el contenido del paquete cambió respecto a la base pero la versión sigue en ${versionActual} (base ${versionBase}): hay que subirla en extension/manifest.json`;
}
