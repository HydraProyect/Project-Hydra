// Arnés para extension/content.js sin navegador.
//
// Por qué existe: la extensión no tiene pruebas automáticas en CI (hueco
// declarado en la PR #771), y lo que se comprueba aquí NO es cosmética — es la
// regla que decide dónde acaba un documento en un formulario CAE de varias
// filas. Equivocarse ahí no da error: el portal acepta el archivo y lo acredita
// contra otro requisito.
//
// Se ejecuta con:  node <este guion> <ruta a content.js>
//
// Se simula lo justo del DOM para que el fichero cargue y se le puedan lanzar
// eventos. Cada simulación es una hipótesis sobre el navegador real, así que
// el último caso es un CONTROL POSITIVO: comprueba que el arnés es capaz de
// ver una inyección de verdad. Sin él, un arnés roto daría todo verde.

import { readFileSync } from "node:fs";

const ruta = process.argv[2];
if (!ruta) {
  console.error("Uso: node comprobar-content-extension.mjs <ruta a content.js>");
  process.exit(2);
}

let fallos = 0;
let pasadas = 0;

function comprobar(nombre, condicion, detalle = "") {
  if (condicion) {
    pasadas++;
    console.log(`  ok   ${nombre}`);
  } else {
    fallos++;
    console.log(`  FALLA ${nombre}${detalle ? ` — ${detalle}` : ""}`);
  }
}

// --- DOM simulado ---------------------------------------------------------

class NodoSimulado {
  constructor(etiqueta) {
    this.etiqueta = etiqueta;
    this.hijos = [];
    this.oyentes = {};
    this.classList = { add: () => {}, remove: () => {} };
    this.textContent = "";
    this.className = "";
    this.hidden = false;
    this.disabled = false;
    this.isConnected = true;
  }

  appendChild(n) { this.hijos.push(n); n.padre = this; return n; }
  append(...ns) { for (const n of ns) this.appendChild(n); }
  after() {}
  remove() {
    this.isConnected = false;
    if (!this.padre) return;
    this.padre.hijos = this.padre.hijos.filter((h) => h !== this);
    this.padre = null;
  }
  replaceChildren() { this.hijos = []; }
  addEventListener(tipo, fn) { (this.oyentes[tipo] ??= []).push(fn); }
  removeEventListener() {}
  // El panel vive en un Shadow DOM cerrado, así que su contenido NO cuelga de
  // `hijos`: cuelga de la raíz de sombra. La primera versión de este simulado
  // devolvía un nodo suelto y la búsqueda no encontraba nada — un falso
  // negativo del arnés, no del producto.
  attachShadow() { return (this.sombra = new NodoSimulado("#shadow")); }
  dispatchEvent() { return true; }

  // Busca en profundidad el primer nodo cuyo textContent case.
  buscarPorTexto(texto) {
    if (this.textContent === texto) return this;
    for (const hijo of [...this.hijos, ...(this.sombra ? [this.sombra] : [])]) {
      const encontrado = hijo.buscarPorTexto?.(texto);
      if (encontrado) return encontrado;
    }
    return null;
  }
}

class InputArchivoSimulado extends NodoSimulado {
  constructor(nombre) {
    super("input");
    this.type = "file";
    this.nombre = nombre;
    this.files = null;
    this.clicsRecibidos = 0;
    this.eventosDespachados = [];
  }

  click() {
    this.clicsRecibidos++;
    // Reentrada real del navegador: input.click() vuelve a disparar el
    // manejador de click en captura. Sin esto, «Buscar en mi equipo» parecería
    // funcionar aunque el paso libre estuviera mal implementado.
    lanzarClic(this);
  }

  dispatchEvent(evento) {
    this.eventosDespachados.push(evento.type);
    return true;
  }
}

const oyentesDocumento = {};
const documentoSimulado = {
  addEventListener: (tipo, fn) => ((oyentesDocumento[tipo] ??= []).push(fn)),
  removeEventListener: () => {},
  createElement: (etiqueta) => new NodoSimulado(etiqueta),
  documentElement: new NodoSimulado("html"),
};

let respuestaDeListarPendientes = { ok: true, proveedores: [] };
let respuestaDeSubirDocumento = { ok: true };
let respuestaDeObtenerConexion = { conectado: false };
const mensajesEnviados = [];
let manejadorDeMensajes = null;

const chromeSimulado = {
  runtime: {
    sendMessage: async (mensaje) => {
      mensajesEnviados.push(mensaje);
      if (mensaje.accion === "obtenerConexion") return respuestaDeObtenerConexion;
      if (mensaje.accion === "listarPendientes") return respuestaDeListarPendientes;
      if (mensaje.accion === "subirDocumento") return respuestaDeSubirDocumento;
      return { ok: false, error: "acción no simulada" };
    },
    onMessage: { addListener: (fn) => (manejadorDeMensajes = fn) },
  },
};

class DataTransferSimulado {
  constructor() { this.items = { add: (a) => (this.archivo = a) }; }
  get files() { return [this.archivo]; }
}

function lanzarClic(objetivo) {
  const evento = {
    type: "click",
    target: objetivo,
    prevenido: false,
    preventDefault() { this.prevenido = true; },
    stopPropagation() {},
  };
  for (const fn of oyentesDocumento.click ?? []) fn(evento);
  return evento;
}

// Cada bloque que recarga content.js parte de un documento nuevo. Sin esto, los
// paneles de los casos anteriores seguían colgados de `documentElement` y
// `buscarPorTexto` devolvía el botón del panel VIEJO —cuyo closure apunta a otro
// campo—, de modo que el reenvío del clic parecía roto. Es la misma trampa que
// el selector posicional del E2E: no falla por ausencia, encuentra otra cosa.
function reiniciarDom() {
  for (const k of Object.keys(oyentesDocumento)) delete oyentesDocumento[k];
  documentoSimulado.documentElement = new NodoSimulado("html");
}

function responder() {
  let respuesta;
  return {
    enviar: (r) => (respuesta = r),
    leer: () => respuesta,
  };
}

async function reposar() {
  // Deja correr las promesas pendientes del fichero bajo prueba.
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

// --- Carga ----------------------------------------------------------------

const fuente = readFileSync(ruta, "utf8");
const cargar = new Function(
  "document", "chrome", "HTMLInputElement", "DataTransfer", "File", "atob", "Event", "console",
  fuente
);

cargar(
  documentoSimulado,
  chromeSimulado,
  InputArchivoSimulado,
  DataTransferSimulado,
  class FileSimulado { constructor(partes, nombre, opciones) { this.name = nombre; this.type = opciones?.type; } },
  (s) => Buffer.from(s, "base64").toString("binary"),
  class EventoSimulado { constructor(tipo) { this.type = tipo; } },
  { log: () => {}, warn: () => {} }
);

await reposar();

console.log(`\ncontent.js — ${ruta}\n`);

// --- Casos ----------------------------------------------------------------

// 1. Sin conexión no se toca nada: el explorador del sistema se abre como
//    siempre. Interceptar sin tener documentos que ofrecer sería estorbar.
{
  const campo = new InputArchivoSimulado("sin-conexion");
  const evento = lanzarClic(campo);
  comprobar("sin conexión, el clic NO se intercepta", evento.prevenido === false);
}

// 2. Con conexión sí se intercepta: ahí está el panel en vez del explorador.
manejadorDeMensajes({ accion: "conexionCambiada", conectado: true }, null, () => {});
await reposar();

let campoPrincipal;
{
  campoPrincipal = new InputArchivoSimulado("principal");
  const evento = lanzarClic(campoPrincipal);
  await reposar();
  comprobar("con conexión, el clic se intercepta", evento.prevenido === true);
  comprobar(
    "se piden los documentos pendientes al interceptar",
    mensajesEnviados.some((m) => m.accion === "listarPendientes")
  );
}

// 3. La inyección va al campo que se pulsó, no al primero de la página.
{
  const otroCampo = new InputArchivoSimulado("otro");
  lanzarClic(otroCampo); // el último pulsado pasa a ser este
  await reposar();

  const r = responder();
  manejadorDeMensajes(
    { accion: "inyectarArchivo", base64: Buffer.from("hola").toString("base64"), nombreArchivo: "a.pdf", tipoMime: "application/pdf" },
    null,
    r.enviar
  );

  comprobar("la inyección responde ok", r.leer()?.ok === true, JSON.stringify(r.leer()));
  comprobar("el archivo va al campo pulsado", otroCampo.files !== null);
  comprobar("y NO al primero de la página", campoPrincipal.files === null);
}

// 4. EL CASO QUE MOTIVÓ EL CAMBIO. Sin ningún campo pulsado no se adivina:
//    antes se caía al primer input[type=file] del DOM en silencio.
{
  // Se recarga el fichero para partir de cero, que es el estado real de una
  // pestaña recién abierta.
  reiniciarDom();

  cargar(
    documentoSimulado, chromeSimulado, InputArchivoSimulado, DataTransferSimulado,
    class { constructor(p, n, o) { this.name = n; this.type = o?.type; } },
    (s) => Buffer.from(s, "base64").toString("binary"),
    class { constructor(t) { this.type = t; } },
    { log: () => {}, warn: () => {} }
  );
  await reposar();

  const huerfano = new InputArchivoSimulado("nadie-lo-pulso");
  const r = responder();
  manejadorDeMensajes(
    { accion: "inyectarArchivo", base64: Buffer.from("hola").toString("base64"), nombreArchivo: "a.pdf", tipoMime: "application/pdf" },
    null,
    r.enviar
  );

  comprobar("sin campo pulsado, la inyección FALLA", r.leer()?.ok === false);
  comprobar(
    "y lo dice en vez de adivinar",
    typeof r.leer()?.error === "string" && r.leer().error.includes("Pulsa primero")
  );
  comprobar("ningún campo recibe el archivo por descarte", huerfano.files === null);
}

// 5. La salida: tras «Buscar en mi equipo» el clic reenviado NO se intercepta,
//    o la extensión se convertiría en un secuestro del campo de archivo.
{
  reiniciarDom();
  cargar(
    documentoSimulado, chromeSimulado, InputArchivoSimulado, DataTransferSimulado,
    class { constructor(p, n, o) { this.name = n; this.type = o?.type; } },
    (s) => Buffer.from(s, "base64").toString("binary"),
    class { constructor(t) { this.type = t; } },
    { log: () => {}, warn: () => {} }
  );
  await reposar(); // que la consulta de arranque aterrice ANTES del aviso
  manejadorDeMensajes({ accion: "conexionCambiada", conectado: true }, null, () => {});
  await reposar();

  const campo = new InputArchivoSimulado("con-salida");
  const primero = lanzarClic(campo);
  await reposar();
  comprobar("el primer clic se intercepta", primero.prevenido === true);

  // Se busca el botón dentro del panel y se pulsa, igual que haría el Gestor.
  const botonEquipo = documentoSimulado.documentElement.buscarPorTexto("Buscar en mi equipo");
  comprobar("el panel ofrece «Buscar en mi equipo»", botonEquipo !== null);

  if (botonEquipo) {
    const clicsAntes = campo.clicsRecibidos;
    botonEquipo.oyentes.click[0]();
    comprobar("reenvía el clic al campo", campo.clicsRecibidos === clicsAntes + 1);

    // El clic reenviado pasó por el manejador (InputArchivoSimulado.click lo
    // relanza). Si el paso libre no funcionara, se habría vuelto a prevenir y
    // habríamos abierto otro panel en vez del explorador.
    comprobar(
      "el reenvío deja paso: no reabre el panel",
      documentoSimulado.documentElement.buscarPorTexto("Buscar en mi equipo") === null
    );

    const trasLaSalida = lanzarClic(campo);
    comprobar(
      "el siguiente clic vuelve a interceptarse (el paso libre se gasta una sola vez)",
      trasLaSalida.prevenido === true
    );
  }
}

// 5-bis. LA CARRERA QUE ENCONTRÓ ESTE ARNÉS. El content script consulta la
//    conexión al inyectarse, y esa respuesta tarda. Si el Gestor conecta la
//    extensión mientras la consulta viaja, el aviso llega primero y la respuesta
//    vieja —pedida antes de conectar— aterriza después diciendo "no conectado".
//    Sin sello de generación, la pestaña se quedaba dormida sin decir nada.
{
  reiniciarDom();

  // La respuesta de arranque se retiene a propósito para poder soltarla DESPUÉS
  // del aviso, que es el orden que rompía el estado.
  let soltarLaRespuestaDeArranque;
  const respuestaRetenida = new Promise((r) => (soltarLaRespuestaDeArranque = r));
  const sendMessageOriginal = chromeSimulado.runtime.sendMessage;
  chromeSimulado.runtime.sendMessage = async (mensaje) => {
    if (mensaje.accion === "obtenerConexion") {
      await respuestaRetenida;
      return { conectado: false };
    }
    return sendMessageOriginal(mensaje);
  };

  cargar(
    documentoSimulado, chromeSimulado, InputArchivoSimulado, DataTransferSimulado,
    class { constructor(p, n, o) { this.name = n; this.type = o?.type; } },
    (s) => Buffer.from(s, "base64").toString("binary"),
    class { constructor(t) { this.type = t; } },
    { log: () => {}, warn: () => {} }
  );

  manejadorDeMensajes({ accion: "conexionCambiada", conectado: true }, null, () => {});
  soltarLaRespuestaDeArranque();
  await reposar();

  const campo = new InputArchivoSimulado("tras-la-carrera");
  const evento = lanzarClic(campo);
  comprobar(
    "la respuesta tardía de arranque NO pisa el aviso de conexión",
    evento.prevenido === true
  );

  chromeSimulado.runtime.sendMessage = sendMessageOriginal;
}

// 5-ter. La conexión se cae con el panel DELANTE. Cerrarlo sería lo cómodo y
//    lo peor: el clic que lo abrió ya se gastó, así que el Gestor CAE se
//    quedaría mirando un campo que no reacciona, sin panel y sin explorador.
{
  reiniciarDom();
  cargar(
    documentoSimulado, chromeSimulado, InputArchivoSimulado, DataTransferSimulado,
    class { constructor(p, n, o) { this.name = n; this.type = o?.type; } },
    (s) => Buffer.from(s, "base64").toString("binary"),
    class { constructor(t) { this.type = t; } },
    { log: () => {}, warn: () => {} }
  );
  await reposar();
  manejadorDeMensajes({ accion: "conexionCambiada", conectado: true }, null, () => {});
  await reposar();

  const campo = new InputArchivoSimulado("conexion-perdida");
  lanzarClic(campo);
  await reposar();
  comprobar(
    "precondición: el panel está abierto",
    documentoSimulado.documentElement.buscarPorTexto("Buscar en mi equipo") !== null
  );

  manejadorDeMensajes({ accion: "conexionCambiada", conectado: false }, null, () => {});
  await reposar();

  comprobar(
    "al perder la conexión, el panel NO se cierra",
    documentoSimulado.documentElement.buscarPorTexto("Buscar en mi equipo") !== null
  );

  const raiz = documentoSimulado.documentElement;
  const textos = [];
  (function recoger(n) {
    if (n.textContent) textos.push(n.textContent);
    for (const h of [...n.hijos, ...(n.sombra ? [n.sombra] : [])]) recoger(h);
  })(raiz);
  comprobar(
    "y dice que se perdió la conexión",
    textos.some((x) => x.includes("Se perdió la conexión con TALVEG"))
  );

  // Con la conexión caída, el siguiente clic ya no se intercepta.
  const otro = new InputArchivoSimulado("tras-la-caida");
  comprobar("y a partir de ahí no se intercepta nada", lanzarClic(otro).prevenido === false);
}

// 5-quater. La lista llega TARDE, después de caerse la conexión. Pintarla
//    encima del aviso devolvería al Gestor CAE unos botones que ya no pueden
//    terminar ninguna subida. Segundo P2 de Codex sobre este incremento.
{
  reiniciarDom();

  let soltarLaLista;
  const listaRetenida = new Promise((r) => (soltarLaLista = r));
  const sendMessageOriginal = chromeSimulado.runtime.sendMessage;
  chromeSimulado.runtime.sendMessage = async (mensaje) => {
    if (mensaje.accion === "listarPendientes") {
      await listaRetenida;
      return {
        ok: true,
        proveedores: [
          {
            proveedorNombre: "Plataforma CAE de prueba",
            proveedorActivo: true,
            clientes: [
              {
                clienteNombre: "Cliente empresarial inventado",
                documentos: [
                  {
                    documentoId: "d1",
                    acreditacionId: "a1",
                    tipoDocumentoNombre: "Reconocimiento medico",
                    propietarioNombre: "Trabajador inventado",
                    estado: "Pendiente",
                  },
                ],
              },
            ],
          },
        ],
      };
    }
    return sendMessageOriginal(mensaje);
  };

  cargar(
    documentoSimulado, chromeSimulado, InputArchivoSimulado, DataTransferSimulado,
    class { constructor(p, n, o) { this.name = n; this.type = o?.type; } },
    (s) => Buffer.from(s, "base64").toString("binary"),
    class { constructor(t) { this.type = t; } },
    { log: () => {}, warn: () => {} }
  );
  await reposar();
  manejadorDeMensajes({ accion: "conexionCambiada", conectado: true }, null, () => {});
  await reposar();

  const campo = new InputArchivoSimulado("lista-tardia");
  lanzarClic(campo);           // abre el panel; la lista queda en vuelo
  await reposar();

  manejadorDeMensajes({ accion: "conexionCambiada", conectado: false }, null, () => {});
  await reposar();
  soltarLaLista();             // ahora sí llega, tarde
  await reposar();

  const textos = [];
  (function recoger(n) {
    if (n.textContent) textos.push(n.textContent);
    for (const h of [...n.hijos, ...(n.sombra ? [n.sombra] : [])]) recoger(h);
  })(documentoSimulado.documentElement);

  comprobar(
    "la lista que llega tarde NO borra el aviso de conexión perdida",
    textos.some((x) => x.includes("Se perdió la conexión con TALVEG"))
  );
  comprobar(
    "y no deja botones de subida que ya no pueden funcionar",
    !textos.includes("Poner aquí")
  );

  chromeSimulado.runtime.sendMessage = sendMessageOriginal;
}

// 5-quinquies. El token vence con la pestaña abierta y nadie haciendo nada. El
//    vencimiento no produce ningún evento por su cuenta, así que la pestaña
//    tiene que apagarse sola. Tercer P2 de Codex sobre este incremento.
{
  reiniciarDom();
  cargar(
    documentoSimulado, chromeSimulado, InputArchivoSimulado, DataTransferSimulado,
    class { constructor(p, n, o) { this.name = n; this.type = o?.type; } },
    (s) => Buffer.from(s, "base64").toString("binary"),
    class { constructor(t) { this.type = t; } },
    { log: () => {}, warn: () => {} }
  );
  await reposar();

  // Dos mensajes en vez de uno, y a propósito. El primero da una hora lejana
  // para que la comprobación de "todavía no ha caducado" no compita con el
  // reloj: con un margen corto, una pausa del proceso —recolección de basura,
  // una máquina compartida— hace caducar el token antes de que se lance el
  // clic, y el arnés acusa al producto de algo que ha provocado él. Ocurrió:
  // 30 ms bastaban en esta máquina y fallaban en el runner de CI.
  manejadorDeMensajes(
    { accion: "conexionCambiada", conectado: true, expiraEnUtc: new Date(Date.now() + 60_000).toISOString() },
    null,
    () => {}
  );
  await reposar();

  const antes = new InputArchivoSimulado("antes-de-caducar");
  comprobar("antes de la hora, el clic se intercepta", lanzarClic(antes).prevenido === true);

  // El segundo reprograma el reloj a una hora inminente. Aquí la espera larga
  // no estorba: una vez caducado, el estado ya no vuelve solo, así que esperar
  // de más solo refuerza la medición.
  manejadorDeMensajes(
    { accion: "conexionCambiada", conectado: true, expiraEnUtc: new Date(Date.now() + 5).toISOString() },
    null,
    () => {}
  );
  await new Promise((r) => setTimeout(r, 250));

  const despues = new InputArchivoSimulado("despues-de-caducar");
  comprobar(
    "pasada la hora, el clic ya NO se intercepta aunque nadie haya pedido nada",
    lanzarClic(despues).prevenido === false
  );
}

// 6. CONTROL POSITIVO DEL ARNÉS. Si el simulado no fuera capaz de registrar una
//    inyección, todos los casos de arriba darían verde sin observar nada.
{
  const campo = new InputArchivoSimulado("control");
  comprobar("control: un campo simulado empieza sin archivo", campo.files === null);
  campo.files = ["algo"];
  comprobar("control: el arnés SÍ ve un archivo cuando lo hay", campo.files !== null);
  comprobar(
    "control: el arnés registra los eventos de un input",
    (campo.dispatchEvent({ type: "change" }), campo.eventosDespachados.includes("change"))
  );
}

console.log(`\n${pasadas} de ${pasadas + fallos} comprobaciones pasan.\n`);
process.exit(fallos > 0 ? 1 : 0);
