// ¿A qué pestaña va el documento? Arnés para extension/background.js.
//
// El panel del content script vive DENTRO de una pestaña, y el PDF tarda en
// bajar. Si mientras tanto el Gestor CAE cambia de pestaña y la subida se
// entrega a «la activa», el documento entra en otro portal, la respuesta es
// `ok` y TALVEG marca la acreditación como subida. No hay error que mirar.
// Hallazgo P1 de Codex sobre este mismo incremento.
//
// Uso:  node comprobar-enrutado-subida.mjs <ruta a background.js>

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
let pestanaActiva = 99;
const entregas = [];   // {idPestana, accion}
const urlsDePestanas = {};
let manejadorDeMensajes = null;
let alCerrarPestana = null;

// Reloj adelantable: la caducidad de lo pendiente (30 min) y la ventana del «Deshacer» (15 s) se miden con Date.now.
const ahoraReal = Date.now;
let desfaseMs = 0;
Date.now = () => ahoraReal() + desfaseMs;

globalThis.chrome = {
  runtime: {
    onMessage: { addListener: (fn) => (manejadorDeMensajes = fn) },
    onMessageExternal: { addListener() {} },
    getManifest: () => ({ version: "0.4.0" }),
    lastError: null,
  },
  storage: {
    local: {
      get: async (clave) => (typeof clave === "string" ? { [clave]: almacen[clave] } : { ...almacen }),
      set: async (obj) => Object.assign(almacen, obj),
      remove: async (claves) => [].concat(claves).forEach((c) => delete almacen[c]),
      clear: async () => (almacen = {}),
    },
    session: {
      // Copia, como el chrome real: devolver la referencia ocultaría las escrituras que se pisan.
      get: async (clave) => structuredClone(typeof clave === "string" ? { [clave]: almacen[clave] } : { ...almacen }),
      set: async (obj) => Object.assign(almacen, obj),
      remove: async (claves) => [].concat(claves).forEach((c) => delete almacen[c]),
    },
  },
  tabs: {
    query: async (criterio) =>
      criterio?.active ? [{ id: pestanaActiva }] : [{ id: 1 }, { id: 2 }, { id: pestanaActiva }],
    // La URL de cada pestaña; por defecto, el portal de la Plataforma CAE de la lista.
    get: async (id) => ({ id, url: urlsDePestanas[id] ?? "https://app.nalandaglobal.com/documentos" }),
    sendMessage: async (idPestana, mensaje) => {
      entregas.push({ idPestana, accion: mensaje.accion });
      return { ok: true };
    },
    onRemoved: { addListener: (fn) => (alCerrarPestana = fn) },
  },
};

// Respuestas HTTP simuladas: descarga del PDF y marcado posterior.
let subible = true;
let proveedorActivo = true;
let documentoDeLaLista = "11111111-1111-1111-1111-111111111111";
let listaFalla = false;
let dominiosDelProveedor = ["nalandaglobal.com"];
let estadoDeLaSubida = 200;
const llamadas = []; // {url, metodo, cuerpo}
const CAMBIO = "33333333-3333-3333-3333-333333333333";
const VERSION = "44444444-4444-4444-4444-444444444444";
globalThis.fetch = async (url, opciones = {}) => {
  llamadas.push({ url: String(url), metodo: opciones.method ?? "GET", cuerpo: opciones.body });
  if (String(url).endsWith("/subida/deshacer"))
    return { ok: true, status: 204, type: "basic", json: async () => { throw new Error("sin cuerpo"); } };
  if (String(url).endsWith("/subida"))
    return {
      ok: estadoDeLaSubida < 300, status: estadoDeLaSubida, type: "basic",
      json: async () => estadoDeLaSubida < 300 ? { cambioId: CAMBIO, version: VERSION } : { detail: "Este conector está desactivado temporalmente." },
    };
  return {
  ok: !(listaFalla && String(url).includes("acreditaciones-pendientes")),
  status: 200,
  type: "basic",
  arrayBuffer: async () => new TextEncoder().encode("%PDF-falso").buffer,
  json: async () => subible ? [{ proveedorActivo, dominios: dominiosDelProveedor, clientes: [{ documentos: [{
    acreditacionId: "22222222-2222-2222-2222-222222222222",
    documentoId: documentoDeLaLista,
  }] }] }] : [],
  text: async () => "",
  };
};

const fuente = readFileSync(process.argv[2], "utf8");
(0, eval)(fuente);

function conectar(expiraEnUtc) {
  almacen = {
    hydraUrl: "https://staging.talveg.es",
    token: "token-inventado-para-el-arnes",
    expiraEnUtc,
  };
}

const dentroDeOchoHoras = new Date(Date.now() + 8 * 3600e3).toISOString();
const ayer = new Date(Date.now() - 24 * 3600e3).toISOString();

function pedirSubida(remitente) {
  return new Promise((resolver) =>
    manejadorDeMensajes(
      {
        accion: "subirDocumento",
        documentoId: "11111111-1111-1111-1111-111111111111",
        acreditacionId: "22222222-2222-2222-2222-222222222222",
        nombreArchivo: "Reconocimiento medico.pdf",
      },
      remitente,
      resolver
    )
  );
}

console.log(`\nbackground.js — ${process.argv[2]}\n`);

// 1. EL CASO DE CODEX. El panel de la pestaña 7 pide la subida; para cuando el
//    PDF llega, el Gestor está mirando la 99.
{
  conectar(dentroDeOchoHoras);
  entregas.length = 0;
  pestanaActiva = 99;

  const resultado = await pedirSubida({ tab: { id: 7 } });
  const inyecciones = entregas.filter((e) => e.accion === "inyectarArchivo");

  comprobar("la subida del panel se resuelve", resultado?.ok === true, JSON.stringify(resultado));
  comprobar("hay exactamente una inyección", inyecciones.length === 1, `hubo ${inyecciones.length}`);
  comprobar(
    "el documento va a la pestaña que lo pidió, no a la activa",
    inyecciones[0]?.idPestana === 7,
    `fue a la ${inyecciones[0]?.idPestana}`
  );
}

// 2. El popup no tiene pestaña propia: para él sigue valiendo la activa, que es
//    la que el Gestor está mirando mientras lo tiene abierto.
{
  conectar(dentroDeOchoHoras);
  entregas.length = 0;
  pestanaActiva = 42;

  await pedirSubida({});
  const inyecciones = entregas.filter((e) => e.accion === "inyectarArchivo");
  comprobar(
    "desde el popup, el documento va a la pestaña activa",
    inyecciones[0]?.idPestana === 42,
    `fue a la ${inyecciones[0]?.idPestana}`
  );
}

// 3. Conexión caducada: no basta con devolver el error. Hay que limpiar y
//    avisar, o las pestañas abiertas siguen interceptando clics para un panel
//    que ya no puede listar nada.
{
  conectar(ayer);
  entregas.length = 0;

  const resultado = await pedirSubida({ tab: { id: 7 } });
  const avisos = entregas.filter((e) => e.accion === "conexionCambiada");

  comprobar("con la conexión caducada, la subida falla", resultado?.ok === false);
  comprobar("se borra el token guardado", !almacen.token, JSON.stringify(Object.keys(almacen)));
  comprobar("y se avisa a las pestañas abiertas", avisos.length > 0, `hubo ${avisos.length}`);
  comprobar(
    "ninguna inyección llegó a salir",
    entregas.filter((e) => e.accion === "inyectarArchivo").length === 0
  );
}

// 4. CONTROL POSITIVO DEL ARNÉS: si no registrara las entregas, todo lo de
//    arriba daría verde sin observar nada.
{
  entregas.length = 0;
  await chrome.tabs.sendMessage(1234, { accion: "prueba-del-arnes" });
  comprobar("control: el arnés registra a qué pestaña se entrega", entregas[0]?.idPestana === 1234);
}

// La lista que vio la persona puede haber quedado obsoleta durante la descarga.
{
  conectar(dentroDeOchoHoras);
  entregas.length = 0;
  subible = false;
  const resultado = await pedirSubida({ tab: { id: 7 } });
  comprobar("avisa si ya no es subible", resultado?.ok === false && resultado.error.includes("ya no se puede subir"));
  comprobar("no coloca el fichero si ya no es subible", !entregas.some(e => e.accion === "inyectarArchivo"));
  subible = true;

  // Otro documento en la lista (renovado con archivo nuevo): el archivo descargado ya no vale.
  entregas.length = 0;
  documentoDeLaLista = "99999999-9999-9999-9999-999999999999";
  const otroDocumento = await pedirSubida({ tab: { id: 7 } });
  comprobar("avisa si el documento de la acreditacion cambio", otroDocumento?.ok === false && !entregas.some(e => e.accion === "inyectarArchivo"));
  documentoDeLaLista = "11111111-1111-1111-1111-111111111111";

  // Conector desactivado entre la lista y la subida: aviso propio, sin rechazo falso.
  entregas.length = 0;
  proveedorActivo = false;
  const inactivo = await pedirSubida({ tab: { id: 7 } });
  comprobar("avisa del conector desactivado sin hablar de rechazo",
    inactivo?.ok === false && inactivo.error.includes("desactivado") && !inactivo.error.includes("rechazada")
    && !entregas.some(e => e.accion === "inyectarArchivo"));
  proveedorActivo = true;

  // Si no se puede releer la lista, no se coloca nada.
  entregas.length = 0;
  listaFalla = true;
  const sinLista = await pedirSubida({ tab: { id: 7 } });
  comprobar("no coloca el fichero si no se puede releer la lista", sinLista?.ok === false && !entregas.some(e => e.accion === "inyectarArchivo"));
  listaFalla = false;
}

// --- «Lo he enviado» (E1, D-4): poner el archivo NO anota nada en TALVEG ----

function mensaje(accion, remitente, extra = {}) {
  return new Promise((resolver) => manejadorDeMensajes({ accion, ...extra }, remitente, resolver));
}
const ACREDITACION = "22222222-2222-2222-2222-222222222222";
const postsDeSubida = () => llamadas.filter((l) => l.metodo === "POST" && l.url.endsWith("/subida"));
const postsDeDeshacer = () => llamadas.filter((l) => l.metodo === "POST" && l.url.endsWith("/subida/deshacer"));

{
  conectar(dentroDeOchoHoras);
  entregas.length = 0;
  llamadas.length = 0;

  const puesto = await pedirSubida({ tab: { id: 7 } });
  comprobar("poner el archivo se resuelve pendiente de confirmar", puesto?.ok === true && puesto.pendienteDeConfirmar === true, JSON.stringify(puesto));
  comprobar("sin confirmación, ningún POST a …/subida", postsDeSubida().length === 0, `hubo ${postsDeSubida().length}`);
  comprobar("se pide la confirmación a la pestaña que lo pidió",
    entregas.some((e) => e.accion === "pedirConfirmacionEnvio" && e.idPestana === 7));

  const pendientes7 = await mensaje("obtenerEnviosPendientes", { tab: { id: 7 } });
  comprobar("la pestaña 7 ve su envío pendiente con el tipo de documento",
    pendientes7?.pendientes?.length === 1 && pendientes7.pendientes[0].acreditacionId === ACREDITACION
      && pendientes7.pendientes[0].documento === "Reconocimiento medico", JSON.stringify(pendientes7));
  const pendientes8 = await mensaje("obtenerEnviosPendientes", { tab: { id: 8 } });
  comprobar("otra pestaña no ve nada pendiente", pendientes8?.pendientes?.length === 0, JSON.stringify(pendientes8));

  const ajena = await mensaje("confirmarEnvio", { tab: { id: 8 } }, { acreditacionId: ACREDITACION });
  comprobar("otra pestaña no puede confirmar lo puesto en la 7", ajena?.ok === false && postsDeSubida().length === 0);
  const sinPestana = await mensaje("confirmarEnvio", {}, { acreditacionId: ACREDITACION });
  comprobar("sin pestaña (popup) no se confirma", sinPestana?.ok === false && postsDeSubida().length === 0);

  const confirmado = await mensaje("confirmarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  comprobar("«Lo he enviado» hace exactamente un POST a …/subida", confirmado?.ok === true && postsDeSubida().length === 1,
    `${JSON.stringify(confirmado)} · ${postsDeSubida().length}`);
  comprobar("y ofrece deshacer", confirmado?.puedeDeshacer === true);
  const otraVez = await mensaje("confirmarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  comprobar("confirmar dos veces no anota dos veces", otraVez?.ok === false && postsDeSubida().length === 1);

  const deshecho = await mensaje("deshacerEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  const cuerpo = JSON.parse(postsDeDeshacer()[0]?.cuerpo ?? "{}");
  comprobar("«Deshacer» manda el cambio y la versión que devolvió TALVEG",
    deshecho?.ok === true && cuerpo.cambioId === CAMBIO && cuerpo.version === VERSION, JSON.stringify(cuerpo));
  const deshacerOtraVez = await mensaje("deshacerEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  comprobar("no se deshace dos veces", deshacerOtraVez?.ok === false && postsDeDeshacer().length === 1);
}

// «No lo he enviado»: nada en TALVEG y deja de estar pendiente.
{
  conectar(dentroDeOchoHoras);
  llamadas.length = 0;
  await pedirSubida({ tab: { id: 7 } });
  await mensaje("descartarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  const tras = await mensaje("confirmarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  comprobar("tras «No lo he enviado» no se anota nada", tras?.ok === false && postsDeSubida().length === 0);
}

// Un 409 al confirmar (conector apagado entre medias): se dice el motivo y deja de estar pendiente.
{
  conectar(dentroDeOchoHoras);
  llamadas.length = 0;
  await pedirSubida({ tab: { id: 7 } });
  estadoDeLaSubida = 409;
  const rechazado = await mensaje("confirmarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  estadoDeLaSubida = 200;
  comprobar("un 409 muestra el detalle del servidor", rechazado?.ok === false && rechazado.error.includes("desactivado"));
  const pendientes = await mensaje("obtenerEnviosPendientes", { tab: { id: 7 } });
  comprobar("y deja de estar pendiente", pendientes?.pendientes?.length === 0);
}

// El «Desconectar» que pide la persona (popup) borra lo pendiente: no se ofrece a quien conecte después.
{
  conectar(dentroDeOchoHoras);
  await pedirSubida({ tab: { id: 7 } });
  await mensaje("desconectar", {});
  const pendientes = await mensaje("obtenerEnviosPendientes", { tab: { id: 7 } });
  comprobar("el «Desconectar» del popup borra lo pendiente", pendientes?.pendientes?.length === 0, JSON.stringify(pendientes));
}

// Vuelve a conectar SIN vaciar storage.session: conectar() empieza de cero, y aquí importa lo que sobrevive.
function reconectar() {
  Object.assign(almacen, { hydraUrl: "https://staging.talveg.es", token: "token-inventado-para-el-arnes", expiraEnUtc: dentroDeOchoHoras });
}
const MINUTO = 60 * 1000;

// Lo pendiente caduca a los 30 min: pasado ese tiempo ni se ofrece ni se anota.
{
  conectar(dentroDeOchoHoras);
  llamadas.length = 0;
  desfaseMs = 0;
  await pedirSubida({ tab: { id: 7 } });
  desfaseMs = 29 * MINUTO;
  const aLos29 = await mensaje("obtenerEnviosPendientes", { tab: { id: 7 } });
  comprobar("a los 29 min sigue pendiente", aLos29?.pendientes?.length === 1, JSON.stringify(aLos29));
  desfaseMs = 30 * MINUTO + 1000;
  const aLos30 = await mensaje("obtenerEnviosPendientes", { tab: { id: 7 } });
  comprobar("pasados 30 min ya no se ofrece", aLos30?.pendientes?.length === 0, JSON.stringify(aLos30));
  const tarde = await mensaje("confirmarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  comprobar("y confirmarlo tarde no anota nada", tarde?.ok === false && postsDeSubida().length === 0);
  desfaseMs = 0;
}

// El «Deshacer» solo vale 15 s desde la confirmación.
{
  conectar(dentroDeOchoHoras);
  llamadas.length = 0;
  desfaseMs = 0;
  await pedirSubida({ tab: { id: 7 } });
  await mensaje("confirmarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  desfaseMs = 16 * 1000;
  const tarde = await mensaje("deshacerEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  comprobar("pasados 15 s ya no se deshace desde la extensión", tarde?.ok === false && postsDeDeshacer().length === 0,
    JSON.stringify(tarde));
  comprobar("y remite a Centro 360", tarde?.error?.includes("Centro 360"));

  desfaseMs = 0;
  llamadas.length = 0;
  await pedirSubida({ tab: { id: 7 } });
  await mensaje("confirmarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  desfaseMs = 14 * 1000;
  const aTiempo = await mensaje("deshacerEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  comprobar("a los 14 s sí se deshace", aTiempo?.ok === true && postsDeDeshacer().length === 1);
  desfaseMs = 0;
}

// Otra pestaña no deshace lo que se confirmó en la 7.
{
  conectar(dentroDeOchoHoras);
  llamadas.length = 0;
  await pedirSubida({ tab: { id: 7 } });
  await mensaje("confirmarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  const ajena = await mensaje("deshacerEnvio", { tab: { id: 8 } }, { acreditacionId: ACREDITACION });
  comprobar("otra pestaña no puede deshacer lo confirmado en la 7", ajena?.ok === false && postsDeDeshacer().length === 0);
  const sinPestana = await mensaje("deshacerEnvio", {}, { acreditacionId: ACREDITACION });
  comprobar("sin pestaña (popup) no se deshace", sinPestana?.ok === false && postsDeDeshacer().length === 0);
  const propia = await mensaje("deshacerEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  comprobar("la pestaña 7 sí lo deshace", propia?.ok === true && postsDeDeshacer().length === 1);
}

// Cerrar una pestaña olvida lo suyo, y solo lo suyo.
{
  conectar(dentroDeOchoHoras);
  llamadas.length = 0;
  await pedirSubida({ tab: { id: 7 } });
  await pedirSubida({ tab: { id: 9 } });
  comprobar("precondición: el arnés recoge el oyente de tabs.onRemoved", typeof alCerrarPestana === "function");
  await alCerrarPestana?.(7);
  const de7 = await mensaje("obtenerEnviosPendientes", { tab: { id: 7 } });
  const de9 = await mensaje("obtenerEnviosPendientes", { tab: { id: 9 } });
  comprobar("al cerrar la pestaña 7 se olvida lo pendiente en ella", de7?.pendientes?.length === 0, JSON.stringify(de7));
  comprobar("y la 9 conserva lo suyo", de9?.pendientes?.length === 1, JSON.stringify(de9));

  await mensaje("confirmarEnvio", { tab: { id: 9 } }, { acreditacionId: ACREDITACION });
  await alCerrarPestana?.(9);
  const deshacerCerrada = await mensaje("deshacerEnvio", { tab: { id: 9 } }, { acreditacionId: ACREDITACION });
  comprobar("ni se deshace lo confirmado en una pestaña ya cerrada", deshacerCerrada?.ok === false && postsDeDeshacer().length === 0);
}

// La desconexión automática (401 o las 8 h cumplidas) conserva lo pendiente para reintentarlo al volver a conectar.
{
  conectar(dentroDeOchoHoras);
  llamadas.length = 0;
  await pedirSubida({ tab: { id: 7 } });
  estadoDeLaSubida = 401;
  const con401 = await mensaje("confirmarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  estadoDeLaSubida = 200;
  comprobar("con un 401 no se anota y se desconecta", con401?.ok === false && !almacen.token, JSON.stringify(con401));
  reconectar();
  const trasEl401 = await mensaje("obtenerEnviosPendientes", { tab: { id: 7 } });
  comprobar("tras el 401 sigue pendiente", trasEl401?.pendientes?.length === 1, JSON.stringify(trasEl401));
  const reintento = await mensaje("confirmarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  comprobar("y al volver a conectar se confirma", reintento?.ok === true);

  await pedirSubida({ tab: { id: 7 } });
  almacen.expiraEnUtc = ayer;
  const caducada = await mensaje("confirmarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION });
  comprobar("con la conexión de 8 h caducada no se anota", caducada?.ok === false && !almacen.token);
  reconectar();
  const trasCaducar = await mensaje("obtenerEnviosPendientes", { tab: { id: 7 } });
  comprobar("y lo pendiente sobrevive a la caducidad", trasCaducar?.pendientes?.length === 1, JSON.stringify(trasCaducar));
}

// Dos mensajes a la vez no se pisan: cada uno lee y escribe el mapa entero, y sin la cola el segundo resucitaría
// lo que borró el primero.
{
  conectar(dentroDeOchoHoras);
  await pedirSubida({ tab: { id: 7 } });
  await pedirSubida({ tab: { id: 9 } });
  await Promise.all([
    mensaje("descartarEnvio", { tab: { id: 7 } }, { acreditacionId: ACREDITACION }),
    mensaje("descartarEnvio", { tab: { id: 9 } }, { acreditacionId: ACREDITACION }),
  ]);
  const de7 = await mensaje("obtenerEnviosPendientes", { tab: { id: 7 } });
  const de9 = await mensaje("obtenerEnviosPendientes", { tab: { id: 9 } });
  comprobar("dos descartes a la vez borran los dos", de7?.pendientes?.length === 0 && de9?.pendientes?.length === 0,
    `${JSON.stringify(de7)} · ${JSON.stringify(de9)}`);
}

// PLATAFORMA CORRECTA. Un documento de una Plataforma CAE no entra en el portal
// de otra: el popup lista todo, así que la puerta de subirDocumento es la que
// cuenta; al confirmar «Lo he enviado», el servidor recibe el host de la pestaña.
{
  conectar(dentroDeOchoHoras);
  const sinEntregas = () => !entregas.some((e) => e.accion === "inyectarArchivo");
  const pedir = async (remitente) => {
    entregas.length = 0; llamadas.length = 0;
    return pedirSubida(remitente);
  };

  // Control: en el portal de su Plataforma (subdominio) se pone el archivo.
  const propio = await pedir({ tab: { id: 7 }, url: "https://acme.nalandaglobal.com/subir?x=1" });
  comprobar("en el portal de su plataforma (subdominio) se pone el archivo",
    propio?.ok === true && !sinEntregas(), JSON.stringify(propio));

  // La confirmación declara al servidor el host de ESA pestaña, no uno que mande el mensaje.
  const confirmado = await mensaje("confirmarEnvio", { tab: { id: 7 }, url: "https://acme.nalandaglobal.com/otra/ruta" },
    { acreditacionId: ACREDITACION, dominioPortal: "app.dokify.net" });
  const cuerpoConfirmado = JSON.parse(postsDeSubida().at(-1)?.cuerpo ?? "{}");
  comprobar("«Lo he enviado» declara al servidor el host de la pestaña",
    confirmado?.ok === true && cuerpoConfirmado.dominioPortal === "acme.nalandaglobal.com", JSON.stringify(cuerpoConfirmado));

  // Panel: la URL del remitente manda aunque otra pestaña esté activa.
  const ajenoPanel = await pedir({ tab: { id: 7 }, url: "https://app.dokify.net/subir" });
  comprobar("desde el panel de otra plataforma NO se pone el archivo",
    ajenoPanel?.ok === false && ajenoPanel.error.includes("otra plataforma"), JSON.stringify(ajenoPanel));
  comprobar("y no entrega ningún archivo al formulario", sinEntregas());
  const sinPendiente = await mensaje("obtenerEnviosPendientes", { tab: { id: 7 } });
  comprobar("ni deja nada pendiente de confirmar", sinPendiente?.pendientes?.length === 0, JSON.stringify(sinPendiente));

  // Popup: sin pestaña propia, se pregunta por la pestaña activa.
  pestanaActiva = 55;
  urlsDePestanas[55] = "https://app.dokify.net/subir";
  const ajenoPopup = await pedir({});
  comprobar("desde el popup, con la pestaña activa en otra plataforma, NO se pone el archivo",
    ajenoPopup?.ok === false && sinEntregas());
  urlsDePestanas[55] = "https://app.nalandaglobal.com/";
  const propioPopup = await pedir({});
  comprobar("desde el popup, en el portal de su plataforma, sí", propioPopup?.ok === true, JSON.stringify(propioPopup));
  delete urlsDePestanas[55];
  await mensaje("descartarEnvio", { tab: { id: 55 } }, { acreditacionId: ACREDITACION });

  // El sufijo exige el punto: «notnalandaglobal.com» no es un subdominio.
  const parecido = await pedir({ tab: { id: 7 }, url: "https://notnalandaglobal.com/" });
  comprobar("un host que solo acaba igual (sin punto) NO cuenta", parecido?.ok === false && sinEntregas());

  // Catálogo sin dominios para el Proveedor: no se puede saber, no se pone.
  dominiosDelProveedor = [];
  const sinDominios = await pedir({ tab: { id: 7 }, url: "https://app.nalandaglobal.com/" });
  comprobar("sin dominios del proveedor en la lista, no se pone el archivo", sinDominios?.ok === false && sinEntregas());
  dominiosDelProveedor = ["nalandaglobal.com"];

  // Pestaña sin URL legible: no se pone.
  urlsDePestanas[7] = "about:blank";
  const sinHost = await pedir({ tab: { id: 7 } });
  comprobar("si no hay host legible en la pestaña, no se pone el archivo", sinHost?.ok === false && sinEntregas());
  delete urlsDePestanas[7];
}
console.log(`\n${fallos === 0 ? "todo en verde" : `${fallos} fallos`}.\n`);
console.log(`${total} comprobaciones ejecutadas.`);
process.exit(fallos > 0 ? 1 : 0);
