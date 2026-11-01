// ¿Cada flujo va al Tenant por el que empezó? Arnés para extension/background.js.
//
// El Tenant elegido es una solicitud que resuelve el servidor, pero la extensión
// tiene que pedir siempre el mismo durante un flujo: si la persona cambia la
// selección en el popup con un documento ya puesto en el formulario, «Lo he
// enviado» y «Deshacer» no pueden acabar en el otro Tenant. Y si TALVEG retira una
// selección, no se cae a otro Tenant: se avisa y se espera a que la persona elija.
// Y si TALVEG no confirma el Tenant efectivo de una respuesta (un binario que ignora
// la solicitud), no se muestra ni se entrega nada de ella.
//
// Uso:  node comprobar-tenant-beneficiario.mjs <ruta a background.js>

import { readFileSync } from "node:fs";

let fallos = 0;
let total = 0;
function comprobar(nombre, condicion, detalle = "") {
  total++;
  if (condicion) console.log(`  ok   ${nombre}`);
  else { fallos++; console.log(`  FALLA ${nombre}${detalle ? ` — ${detalle}` : ""}`); }
}

const TENANT_ORIGEN = "aaaaaaaa-0000-0000-0000-000000000000";
const TENANT_A = "aaaaaaaa-1111-1111-1111-111111111111";
const TENANT_B = "bbbbbbbb-2222-2222-2222-222222222222";
const ACREDITACION = "22222222-2222-2222-2222-222222222222";
const DOCUMENTO = "11111111-1111-1111-1111-111111111111";
const TITULO_RECHAZO = "Extension.SeleccionNoAutorizada";

let almacen = {};
let manejadorDeMensajes = null;

globalThis.chrome = {
  runtime: {
    onMessage: { addListener: (fn) => (manejadorDeMensajes = fn) },
    onMessageExternal: { addListener() {} },
    getManifest: () => ({ version: "0.5.2" }),
    lastError: null,
  },
  storage: {
    local: {
      get: async (clave) => (typeof clave === "string" ? { [clave]: almacen[clave] } : { ...almacen }),
      set: async (obj) => Object.assign(almacen, obj),
      remove: async (claves) => [].concat(claves).forEach((c) => delete almacen[c]),
    },
    session: {
      get: async (clave) => structuredClone(typeof clave === "string" ? { [clave]: almacen[clave] } : { ...almacen }),
      set: async (obj) => Object.assign(almacen, obj),
      remove: async (claves) => [].concat(claves).forEach((c) => delete almacen[c]),
    },
  },
  tabs: {
    query: async () => [{ id: 7 }],
    get: async (id) => ({ id, url: "https://app.nalandaglobal.com/documentos" }),
    sendMessage: async (_idPestana, cuerpo) => {
      mensajesAPestana.push(cuerpo);
      return { ok: true };
    },
    onRemoved: { addListener() {} },
  },
};

let llamadas = [];
let mensajesAPestana = [];
let tenantsRetirados = new Set();
// Qué confirma cada respuesta: por defecto, el Tenant pedido, o el de origen si no se
// pidió ninguno. `confirmar(ruta, tenant)` simula lo demás: null es un binario que
// no sabe confirmar. `estadoDe(ruta)` fuerza un estado para una ruta.
let confirmar = (_ruta, tenant) => tenant ?? TENANT_ORIGEN;
let estadoDe = () => null;
let listaDeTenants = { estado: 200, tenants: [
  { tenantId: TENANT_ORIGEN, nombre: "Origen", esOrigen: true },
  { tenantId: TENANT_A, nombre: "A", esOrigen: false },
  { tenantId: TENANT_B, nombre: "B", esOrigen: false },
], porDefecto: TENANT_A };

function respuesta(estado, cuerpo, extras = {}) {
  const r = {
    ok: estado < 300, status: estado, type: "basic", headers: new Headers(),
    json: async () => cuerpo,
    arrayBuffer: async () => new TextEncoder().encode("%PDF-falso").buffer,
    ...extras,
  };
  r.clone = () => r;
  return r;
}

globalThis.fetch = async (url, opciones = {}) => {
  const ruta = String(url);
  const tenant = opciones.headers?.["X-Tenant-Beneficiario"];
  llamadas.push({ ruta, tenant, credenciales: opciones.credentials });
  const r = responder(ruta, tenant);
  const efectivo = r.status === 403 || r.status === 429 || r.status >= 500 ? null : confirmar(ruta, tenant);
  if (efectivo) r.headers.set("X-Tenant-Efectivo", efectivo);
  return r;
};

function responder(ruta, tenant) {
  const forzado = estadoDe(ruta);
  if (forzado) return respuesta(forzado, { detail: `Detalle del ${forzado}` });
  if (tenant && tenantsRetirados.has(tenant))
    return respuesta(403, { title: TITULO_RECHAZO, status: 403 });
  if (ruta.endsWith("/extension/tenants-beneficiarios"))
    return respuesta(listaDeTenants.estado, { tenants: listaDeTenants.tenants, porDefecto: listaDeTenants.porDefecto });
  if (ruta.endsWith("/entrega")) return respuesta(200, { ticket: "ticket-del-arnes" });
  if (ruta.endsWith("/archivo")) return respuesta(200, {});
  if (ruta.endsWith("/subida/deshacer")) return respuesta(204, {});
  if (ruta.endsWith("/subida")) return respuesta(200, { cambioId: "c-1", version: "v-1" });
  return respuesta(200, [{
    proveedorPlataformaCaeId: "p-1", proveedorNombre: "Nalanda", proveedorActivo: true,
    dominios: ["nalandaglobal.com"], proveedoresSolapados: [],
    clientes: [{ documentos: [{ acreditacionId: ACREDITACION, documentoId: DOCUMENTO }] }],
  }]);
}

(0, eval)(readFileSync(process.argv[2], "utf8"));

function conectar() {
  almacen = {
    hydraUrl: "https://staging.talveg.es",
    token: "token-inventado-para-el-arnes",
    expiraEnUtc: new Date(Date.now() + 8 * 3600e3).toISOString(),
  };
  llamadas = [];
  mensajesAPestana = [];
  tenantsRetirados = new Set();
  confirmar = (_ruta, tenant) => tenant ?? TENANT_ORIGEN;
  estadoDe = () => null;
  listaDeTenants = { ...listaDeTenants, estado: 200, porDefecto: TENANT_A };
}

function mensaje(cuerpo, remitente = {}) {
  return new Promise((resolver) => manejadorDeMensajes(cuerpo, remitente, resolver));
}

const pestana = { tab: { id: 7 }, url: "https://app.nalandaglobal.com/documentos" };
const subir = () => mensaje({
  accion: "subirDocumento", documentoId: DOCUMENTO, acreditacionId: ACREDITACION, nombreArchivo: "Poliza.pdf",
}, pestana);
const tenantDe = (fin) => llamadas.filter((l) => l.ruta.endsWith(fin)).map((l) => l.tenant);

console.log("Selección inicial");
conectar();
let resultado = await mensaje({ accion: "listarPendientes" });
comprobar("sin selección guardada se usa el Tenant por defecto que da el servidor",
  resultado.ok && tenantDe("/acreditaciones-pendientes")[0] === TENANT_A);
comprobar("la lista de Tenants se pide sin cabecera de selección", tenantDe("/tenants-beneficiarios")[0] === undefined);

conectar();
listaDeTenants = { ...listaDeTenants, porDefecto: null };
await mensaje({ accion: "listarPendientes" });
comprobar("sin Tenant por defecto se queda en el de origen", tenantDe("/acreditaciones-pendientes")[0] === TENANT_ORIGEN);

conectar();
listaDeTenants = { ...listaDeTenants, estado: 404 };
resultado = await mensaje({ accion: "listarPendientes" });
comprobar("un servidor sin la lista sigue funcionando, sin cabecera",
  resultado.ok && tenantDe("/acreditaciones-pendientes")[0] === undefined);

conectar();
listaDeTenants = { ...listaDeTenants, estado: 500 };
resultado = await mensaje({ accion: "listarPendientes" });
comprobar("si la lista falla no se cae al Tenant de origen sin decirlo",
  !resultado.ok && tenantDe("/acreditaciones-pendientes").length === 0);

console.log("Un flujo conserva su Tenant");
conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
await mensaje({ accion: "listarPendientes" }, pestana);
await mensaje({ accion: "elegirTenant", tenantId: TENANT_B });
resultado = await subir();
comprobar("el documento se pone", resultado.ok, resultado.error);
comprobar("la subida de una pestaña va al Tenant que esa pestaña listó",
  [...tenantDe("/entrega"), ...tenantDe("/archivo")].every((t) => t === TENANT_A) && tenantDe("/archivo").length === 1);
await mensaje({ accion: "elegirTenant", tenantId: TENANT_ORIGEN });
resultado = await mensaje({ accion: "confirmarEnvio", acreditacionId: ACREDITACION }, pestana);
comprobar("«Lo he enviado» va al Tenant del envío, no al que marca ahora el popup",
  resultado.ok && tenantDe("/subida")[0] === TENANT_A);
await mensaje({ accion: "elegirTenant", tenantId: TENANT_B });
resultado = await mensaje({ accion: "deshacerEnvio", acreditacionId: ACREDITACION }, pestana);
comprobar("«Deshacer» va al mismo Tenant", resultado.ok && tenantDe("/subida/deshacer")[0] === TENANT_A);
comprobar("ninguna petición lleva cookies", llamadas.every((l) => l.credenciales === "omit"));

console.log("Elegir");
conectar();
resultado = await mensaje({ accion: "elegirTenant", tenantId: TENANT_A }, pestana);
comprobar("una pestaña del portal no cambia la selección", !resultado.ok && !almacen.tenantSeleccionado);
resultado = await mensaje({ accion: "elegirTenant", tenantId: "cccccccc-3333-3333-3333-333333333333" });
comprobar("un Tenant fuera de la lista no se guarda", !resultado.ok && !almacen.tenantSeleccionado);

console.log("Selección retirada");
conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
await mensaje({ accion: "listarPendientes" }, pestana);
tenantsRetirados.add(TENANT_A);
resultado = await mensaje({ accion: "listarPendientes" });
comprobar("el rechazo se avisa", !resultado.ok && resultado.seleccionRetirada === true);
const peticionesTrasRechazo = llamadas.length;
resultado = await subir();
comprobar("no se reintenta contra otro Tenant", !resultado.ok && llamadas.length === peticionesTrasRechazo,
  `peticiones: ${llamadas.length - peticionesTrasRechazo}`);
resultado = await mensaje({ accion: "obtenerTenants" });
comprobar("el popup recibe el aviso y ninguna selección", resultado.ok && resultado.avisoRetirada && resultado.seleccionado === null);
await mensaje({ accion: "elegirTenant", tenantId: TENANT_B });
resultado = await mensaje({ accion: "listarPendientes" });
comprobar("al elegir otro se sigue con ese", resultado.ok && tenantDe("/acreditaciones-pendientes").at(-1) === TENANT_B);

console.log("Rechazo con el popup ya en otro Tenant");
conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
await mensaje({ accion: "listarPendientes" }, pestana);
await mensaje({ accion: "elegirTenant", tenantId: TENANT_B });
tenantsRetirados.add(TENANT_A);
resultado = await subir();
comprobar("el flujo de esa pestaña se para", !resultado.ok && resultado.seleccionRetirada === true);
comprobar("la selección de la persona no cae", almacen.tenantSeleccionado === TENANT_B && !almacen.seleccionRetirada);

console.log("Confirmación rechazada");
conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
await mensaje({ accion: "listarPendientes" }, pestana);
await subir();
tenantsRetirados.add(TENANT_A);
const antes = tenantDe("/subida").length;
resultado = await mensaje({ accion: "confirmarEnvio", acreditacionId: ACREDITACION }, pestana);
comprobar("la confirmación no se repite sin Tenant ni con otro",
  !resultado.ok && resultado.seleccionRetirada === true
    && tenantDe("/subida").length === antes + 1 && tenantDe("/subida").at(-1) === TENANT_A);

console.log("Respuesta sin la confirmación del Tenant efectivo");
const documentoPuesto = () => mensajesAPestana.some((m) => m.accion === "inyectarArchivo");
const sinConfirmar = (r) => !r.ok && r.tenantSinConfirmar === true && !r.proveedores;

conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
confirmar = () => null;
resultado = await mensaje({ accion: "listarPendientes" }, pestana);
comprobar("un binario que ignora la solicitud no muestra su listado", sinConfirmar(resultado), JSON.stringify(resultado));
comprobar("la selección sigue y la conexión también: no es una retirada",
  almacen.tenantSeleccionado === TENANT_A && !almacen.seleccionRetirada && Boolean(almacen.token));

conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
confirmar = () => TENANT_ORIGEN;
resultado = await mensaje({ accion: "listarPendientes" }, pestana);
comprobar("confirmar el Tenant de origen no vale por el Tenant beneficiario pedido", sinConfirmar(resultado));

conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
await mensaje({ accion: "listarPendientes" }, pestana);
confirmar = (ruta, tenant) => (ruta.endsWith("/acreditaciones-pendientes") ? tenant : null);
resultado = await subir();
comprobar("sin confirmar el ticket no se pide el archivo", sinConfirmar(resultado) && tenantDe("/archivo").length === 0);
comprobar("ni se pone el documento ni queda envío pendiente",
  !documentoPuesto() && !almacen.enviosPendientes?.["7"]?.[ACREDITACION]);

conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
await mensaje({ accion: "listarPendientes" }, pestana);
confirmar = (ruta, tenant) => (ruta.endsWith("/archivo") ? TENANT_ORIGEN : tenant);
resultado = await subir();
comprobar("un archivo de otro Tenant no llega a la pestaña",
  sinConfirmar(resultado) && tenantDe("/archivo").length === 1 && !documentoPuesto());

conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
await mensaje({ accion: "listarPendientes" }, pestana);
await subir();
confirmar = () => null;
estadoDe = (ruta) => (ruta.endsWith("/subida") ? 404 : null);
resultado = await mensaje({ accion: "confirmarEnvio", acreditacionId: ACREDITACION }, pestana);
comprobar("un 404 sin confirmar no da por perdido el envío",
  sinConfirmar(resultado) && Boolean(almacen.enviosPendientes?.["7"]?.[ACREDITACION]));
confirmar = (_ruta, tenant) => tenant;
estadoDe = () => null;
resultado = await mensaje({ accion: "confirmarEnvio", acreditacionId: ACREDITACION }, pestana);
comprobar("con la confirmación de vuelta, «Lo he enviado» se completa", resultado.ok && resultado.puedeDeshacer);
confirmar = () => null;
resultado = await mensaje({ accion: "deshacerEnvio", acreditacionId: ACREDITACION }, pestana);
comprobar("«Deshacer» sin confirmar lleva a Centro 360, no a reintentar",
  sinConfirmar(resultado) && resultado.error.includes("Centro 360"));

conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
confirmar = (_ruta, tenant) => tenant.toUpperCase();
resultado = await mensaje({ accion: "listarPendientes" }, pestana);
comprobar("el Guid confirmado se compara sin distinguir mayúsculas", resultado.ok, resultado.error);

conectar();
listaDeTenants = { ...listaDeTenants, estado: 404 };
confirmar = () => null;
resultado = await mensaje({ accion: "listarPendientes" }, pestana);
comprobar("sin Tenant pedido no se exige confirmación", resultado.ok, resultado.error);

conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
await mensaje({ accion: "listarPendientes" }, pestana);
estadoDe = (ruta) => (ruta.endsWith("/entrega") ? 429 : null);
resultado = await subir();
comprobar("un 429 conserva su mensaje: lo corta el limitador, antes de resolver el Tenant",
  !resultado.ok && !resultado.tenantSinConfirmar && resultado.error.includes("muchos documentos"));
estadoDe = (ruta) => (ruta.endsWith("/entrega") ? 500 : null);
resultado = await subir();
comprobar("un 500 sigue siendo un fallo y no pone nada",
  !resultado.ok && !resultado.tenantSinConfirmar && !documentoPuesto());

console.log("Desconectar");
conectar();
await mensaje({ accion: "elegirTenant", tenantId: TENANT_A });
await mensaje({ accion: "listarPendientes" }, pestana);
await mensaje({ accion: "desconectar" });
comprobar("desconectar olvida la selección y la de cada pestaña", !almacen.tenantSeleccionado && !almacen.tenantPorPestana);

console.log(`\n${total} comprobaciones, ${fallos} fallos.`);
process.exit(fallos ? 1 : 0);
