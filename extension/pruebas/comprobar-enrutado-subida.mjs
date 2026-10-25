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
const marcados = [];   // POST a …/subida: {url, cuerpo}
let manejadorDeMensajes = null;

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
      get: async (clave) => (typeof clave === "string" ? { [clave]: almacen[clave] } : { ...almacen }),
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
  },
};

// Respuestas HTTP simuladas: descarga del PDF y marcado posterior.
let subible = true;
let proveedorActivo = true;
let documentoDeLaLista = "11111111-1111-1111-1111-111111111111";
let listaFalla = false;
let dominiosDelProveedor = ["nalandaglobal.com"];
globalThis.fetch = async (url, opciones) => {
  if (String(url).endsWith("/subida"))
    marcados.push({ url: String(url), cuerpo: opciones?.body ? JSON.parse(opciones.body) : null });
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

// PLATAFORMA CORRECTA. Un documento de una Plataforma CAE no entra en el portal
// de otra: el popup lista todo, así que esta puerta es la que cuenta.
{
  conectar(dentroDeOchoHoras);

  // Control: en el portal de su Plataforma sube, y el servidor recibe el host declarado.
  entregas.length = 0; marcados.length = 0;
  const propio = await pedirSubida({ tab: { id: 7 }, url: "https://acme.nalandaglobal.com/subir?x=1" });
  comprobar("en el portal de su plataforma (subdominio) sube", propio?.ok === true, JSON.stringify(propio));
  comprobar("declara al servidor el host de la pestaña",
    marcados.length === 1 && marcados[0].cuerpo?.dominioPortal === "acme.nalandaglobal.com",
    JSON.stringify(marcados));

  // Panel: la URL del remitente manda aunque otra pestaña esté activa.
  entregas.length = 0; marcados.length = 0;
  const ajenoPanel = await pedirSubida({ tab: { id: 7 }, url: "https://app.dokify.net/subir" });
  comprobar("desde el panel de otra plataforma NO sube", ajenoPanel?.ok === false && ajenoPanel.error.includes("otra plataforma"), JSON.stringify(ajenoPanel));
  comprobar("y no entrega ningún archivo al formulario", !entregas.some((e) => e.accion === "inyectarArchivo"));
  comprobar("ni marca nada en TALVEG", marcados.length === 0);

  // Popup: sin pestaña propia, se pregunta por la pestaña activa.
  entregas.length = 0; marcados.length = 0;
  pestanaActiva = 55;
  urlsDePestanas[55] = "https://app.dokify.net/subir";
  const ajenoPopup = await pedirSubida({});
  comprobar("desde el popup, con la pestaña activa en otra plataforma, NO sube",
    ajenoPopup?.ok === false && !entregas.some((e) => e.accion === "inyectarArchivo") && marcados.length === 0);
  urlsDePestanas[55] = "https://app.nalandaglobal.com/";
  const propioPopup = await pedirSubida({});
  comprobar("desde el popup, en el portal de su plataforma, sube", propioPopup?.ok === true, JSON.stringify(propioPopup));
  delete urlsDePestanas[55];

  // El sufijo exige el punto: «notnalandaglobal.com» no es un subdominio.
  entregas.length = 0; marcados.length = 0;
  const parecido = await pedirSubida({ tab: { id: 7 }, url: "https://notnalandaglobal.com/" });
  comprobar("un host que solo acaba igual (sin punto) NO cuenta", parecido?.ok === false && marcados.length === 0);

  // Catálogo sin dominios para el Proveedor: no se puede saber, no se sube.
  entregas.length = 0; marcados.length = 0;
  dominiosDelProveedor = [];
  const sinDominios = await pedirSubida({ tab: { id: 7 }, url: "https://app.nalandaglobal.com/" });
  comprobar("sin dominios del proveedor en la lista, no sube",
    sinDominios?.ok === false && !entregas.some((e) => e.accion === "inyectarArchivo") && marcados.length === 0);
  dominiosDelProveedor = ["nalandaglobal.com"];

  // Pestaña sin URL legible: no se sube.
  entregas.length = 0; marcados.length = 0;
  urlsDePestanas[7] = "about:blank";
  const sinHost = await pedirSubida({ tab: { id: 7 } });
  comprobar("si no hay host legible en la pestaña, no sube", sinHost?.ok === false && marcados.length === 0);
  delete urlsDePestanas[7];
}

console.log(`\n${fallos === 0 ? "todo en verde" : `${fallos} fallos`}.\n`);
console.log(`${total} comprobaciones ejecutadas.`);
process.exit(fallos > 0 ? 1 : 0);
