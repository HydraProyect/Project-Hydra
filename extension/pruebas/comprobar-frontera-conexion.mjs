// ¿Puede una pestaña de un portal CAE leer el token o redirigir la conexión?
// Arnés para extension/background.js.
//
// El content script corre dentro del proceso de un portal ajeno: si ese renderer
// está comprometido, cualquier mensaje que el content script pueda mandar lo puede
// mandar el atacante. El token solo sale de este fichero hacia los dos orígenes de
// TALVEG, y conectar o desconectar es cosa del popup y del puente de TALVEG.
//
// Uso:  node comprobar-frontera-conexion.mjs <ruta a background.js>

import { readFileSync } from "node:fs";

let fallos = 0;
let total = 0;
function comprobar(nombre, condicion, detalle = "") {
  total++;
  if (condicion) console.log(`  ok   ${nombre}`);
  else { fallos++; console.log(`  FALLA ${nombre}${detalle ? ` — ${detalle}` : ""}`); }
}

// --- chrome simulado ------------------------------------------------------

let almacen = {};
let manejadorDeMensajes = null;
let manejadorExterno = null;
const peticiones = [];

const area = {
  get: async (claves) => {
    if (typeof claves === "string") return { [claves]: almacen[claves] };
    return Object.fromEntries((claves ?? Object.keys(almacen)).map((c) => [c, almacen[c]]));
  },
  set: async (obj) => Object.assign(almacen, structuredClone(obj)),
  remove: async (claves) => [].concat(claves).forEach((c) => delete almacen[c]),
};

globalThis.chrome = {
  runtime: {
    onMessage: { addListener: (fn) => (manejadorDeMensajes = fn) },
    onMessageExternal: { addListener: (fn) => (manejadorExterno = fn) },
    getManifest: () => ({ version: "0.0.0" }),
    lastError: null,
  },
  storage: { local: area, session: area },
  tabs: { query: async () => [], sendMessage: async () => {} },
};

globalThis.fetch = async (url) => {
  peticiones.push(url);
  return { status: 200, ok: true, type: "basic", json: async () => [], headers: { get: () => null } };
};

const ruta = process.argv[2];
if (!ruta) {
  console.error("Uso: node comprobar-frontera-conexion.mjs <ruta a background.js>");
  process.exit(2);
}
(0, eval)(readFileSync(ruta, "utf8"));

const PESTANA_PORTAL = { tab: { id: 7 }, url: "https://app.nalandaglobal.com/documentos" };
const POPUP = { url: "chrome-extension://id/popup.html" };
const TOKEN = "token-de-prueba";
const CADUCIDAD = new Date(Date.now() + 3_600_000).toISOString();

function mandar(mensaje, remitente) {
  return new Promise((resolver) => {
    const asincrono = manejadorDeMensajes(mensaje, remitente, resolver);
    if (asincrono !== true) resolver(undefined);
  });
}

function mandarExterno(mensaje, url) {
  return new Promise((resolver) => {
    manejadorExterno(mensaje, { url, origin: new URL(url).origin }, resolver);
  });
}

function codigoDe(hydraUrl) {
  return Buffer.from(JSON.stringify({ u: hydraUrl, t: TOKEN, e: CADUCIDAD }), "utf8").toString("base64");
}

function conexionInicial() {
  almacen = { hydraUrl: "https://staging.talveg.es", token: TOKEN, expiraEnUtc: CADUCIDAD };
  peticiones.length = 0;
}

// --- El token no sale por mensajería --------------------------------------

conexionInicial();
const vista = await mandar({ accion: "obtenerConexion" }, PESTANA_PORTAL);
comprobar("el portal sabe que hay conexión", vista?.conectado === true);
comprobar("el portal conoce la caducidad", vista?.expiraEnUtc === CADUCIDAD);
comprobar("el portal no recibe el token", !JSON.stringify(vista ?? {}).includes(TOKEN));

const vistaPopup = await mandar({ accion: "obtenerConexion" }, POPUP);
comprobar("el popup conserva lo que pinta", vistaPopup?.conectado === true && vistaPopup?.hydraUrl === "https://staging.talveg.es");
comprobar("el popup tampoco recibe el token", !JSON.stringify(vistaPopup ?? {}).includes(TOKEN));

// --- Un portal no conecta ni desconecta -----------------------------------

conexionInicial();
await mandar({ accion: "conectar", hydraUrl: "https://atacante.example", token: "suyo", expiraEnUtc: CADUCIDAD }, PESTANA_PORTAL);
comprobar("el portal no cambia el destino", almacen.hydraUrl === "https://staging.talveg.es");
comprobar("el portal no cambia el token", almacen.token === TOKEN);

conexionInicial();
await mandar({ accion: "conectarManual", codigo: codigoDe("https://app.talveg.es") }, PESTANA_PORTAL);
comprobar("el portal no conecta con un código", almacen.hydraUrl === "https://staging.talveg.es");

conexionInicial();
await mandar({ accion: "desconectar" }, PESTANA_PORTAL);
comprobar("el portal no desconecta", almacen.token === TOKEN);

// --- El destino es siempre TALVEG -----------------------------------------

almacen = {};
peticiones.length = 0;
const ajeno = await mandar({ accion: "conectarManual", codigo: codigoDe("https://atacante.example") }, POPUP);
comprobar("un código hacia otro origen se rechaza", ajeno?.ok === false);
comprobar("un código hacia otro origen no guarda el token", almacen.token === undefined);
comprobar("un código hacia otro origen no hace ninguna petición", peticiones.length === 0);

almacen = {};
const degradado = await mandar({ accion: "conectarManual", codigo: codigoDe("http://app.talveg.es") }, POPUP);
comprobar("un código sin https se rechaza", degradado?.ok === false && almacen.token === undefined);

almacen = {};
const puente = await mandarExterno(
  { tipo: "hydra.conectarExtension.v1", hydraUrl: "https://atacante.example", token: TOKEN, expiraEnUtc: CADUCIDAD },
  "https://app.talveg.es/configuracion");
comprobar("el puente rechaza otro destino", puente?.ok === false && almacen.token === undefined);

// storage.local también es accesible desde un content script: el destino se valida
// al leerlo, no solo al guardarlo.
conexionInicial();
almacen.hydraUrl = "https://atacante.example";
const reescrito = await mandar({ accion: "obtenerConexion" }, PESTANA_PORTAL);
await mandar({ accion: "listarPendientes", url: "https://app.nalandaglobal.com/documentos" }, PESTANA_PORTAL);
comprobar("un destino reescrito en storage no cuenta como conexión", reescrito?.conectado === false && reescrito?.hydraUrl === null);
comprobar("un destino reescrito en storage no recibe peticiones", !peticiones.some((url) => String(url).includes("atacante.example")), peticiones.join(" "));

// --- Controles positivos --------------------------------------------------
// Sin ellos, un arnés que no llegara a los manejadores daría todo verde.

almacen = {};
peticiones.length = 0;
const manual = await mandar({ accion: "conectarManual", codigo: codigoDe("https://staging.talveg.es/") }, POPUP);
comprobar("CONTROL: el popup conecta con un código de TALVEG", manual?.ok === true && almacen.token === TOKEN, JSON.stringify(manual));
comprobar("CONTROL: esa conexión llega a TALVEG", peticiones[0]?.startsWith("https://staging.talveg.es/") === true);

almacen = {};
const enlazado = await mandarExterno(
  { tipo: "hydra.conectarExtension.v1", hydraUrl: "https://app.talveg.es", token: TOKEN, expiraEnUtc: CADUCIDAD },
  "https://app.talveg.es/configuracion");
comprobar("CONTROL: el puente de TALVEG conecta", enlazado?.ok === true && almacen.token === TOKEN, JSON.stringify(enlazado));

conexionInicial();
await mandar({ accion: "desconectar" }, POPUP);
comprobar("CONTROL: el popup desconecta", almacen.token === undefined);

console.log(fallos === 0 ? "\nTODO OK" : `\n${fallos} FALLO(S)`);
console.log(`${total} comprobaciones ejecutadas.`);
process.exit(fallos === 0 ? 0 : 1);
