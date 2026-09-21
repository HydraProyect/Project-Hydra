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
    sendMessage: async (idPestana, mensaje) => {
      entregas.push({ idPestana, accion: mensaje.accion });
      return { ok: true };
    },
  },
};

// Respuestas HTTP simuladas: descarga del PDF y marcado posterior.
globalThis.fetch = async () => ({
  ok: true,
  status: 200,
  type: "basic",
  arrayBuffer: async () => new TextEncoder().encode("%PDF-falso").buffer,
  json: async () => ({}),
  text: async () => "",
});

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

console.log(`\n${fallos === 0 ? "todo en verde" : `${fallos} fallos`}.\n`);
console.log(`${total} comprobaciones ejecutadas.`);
process.exit(fallos > 0 ? 1 : 0);
