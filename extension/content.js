// Content script inyectado en las plataformas CAE externas declaradas en
// manifest.json (ver ARQUITECTURA-INTEGRACIONES.md § 14.1, repositorio de
// negocio, para el mecanismo completo). Dos trabajos, y los dos nacen de un
// gesto real del Gestor CAE:
//
//   1. Cuando pincha un campo de archivo del portal, ofrecerle ahí mismo los
//      documentos que tiene pendientes en TALVEG, en vez de dejar que se abra
//      el explorador del sistema para buscar un PDF que ni siquiera está
//      descargado.
//   2. Recibir del service worker el fichero elegido y depositarlo en ESE
//      campo — el que pinchó, no otro.
//
// Nunca decide por su cuenta cuándo subir algo, y nunca envía el formulario.

// EL CAMPO ELEGIDO. Antes esto era «el último input de archivo que se tocó», y
// cuando no se había tocado ninguno se caía al primero que hubiera en el DOM.
// Ese respaldo era silencioso y podía depositar el documento en la fila
// equivocada de un formulario de varias filas —una por tipo de documento—, que
// es precisamente donde más daño hace: el portal acepta el archivo, pero
// acreditado contra otro requisito.
//
// Ahora hay una sola forma de que este valor se rellene: que el Gestor CAE
// pinche un campo. Si está vacío, la subida falla diciéndolo, porque no hay
// ninguna respuesta razonable a «¿en cuál de los seis campos lo pongo?».
let campoElegido = null;

// Si el portal abre su propio explorador (el Gestor eligió «Buscar en mi
// equipo»), no se intercepta ese clic. Se marca el campo justo antes de
// reenviar el clic y se limpia al recibirlo.
let campoConPasoLibre = null;

// Se consulta al arrancar y después de cada subida. Sin conexión no se
// intercepta NADA: la extensión no tiene documentos que ofrecer, así que
// quedarse en medio del camino solo estorbaría a quien está trabajando.
let hayConexion = false;

// Sello de generación. La consulta de arranque es asíncrona, y mientras está en
// vuelo puede llegar el aviso de `conexionCambiada` del service worker: sin este
// contador, la respuesta vieja —pedida ANTES de conectar— aterrizaba después y
// dejaba `hayConexion` en false. La pestaña se quedaba dormida, sin error
// visible, hasta que alguien la recargara. Cada escritura del estado se queda
// con su número; la que llega con un número caducado se descarta.
let selloDeConexion = 0;

async function refrescarConexionAsync() {
  const selloPropio = ++selloDeConexion;

  try {
    const conexion = await chrome.runtime.sendMessage({ accion: "obtenerConexion" });
    if (selloPropio !== selloDeConexion) return;
    hayConexion = Boolean(conexion?.conectado);
  } catch {
    if (selloPropio !== selloDeConexion) return;
    // El service worker puede estar dormido o recargándose. Ante la duda, no
    // interceptar: el coste de equivocarse hacia «no hacer nada» es que el
    // Gestor abre el explorador como siempre.
    hayConexion = false;
  }
}

refrescarConexionAsync();

document.addEventListener(
  "click",
  (evento) => {
    const campo = evento.target;
    if (!(campo instanceof HTMLInputElement) || campo.type !== "file") return;

    campoElegido = campo;

    if (campo === campoConPasoLibre) {
      campoConPasoLibre = null;
      return; // el Gestor pidió el explorador del sistema: se deja pasar.
    }

    if (!hayConexion) return;

    // Aquí es donde se evita el explorador de archivos. Tiene que ser síncrono
    // —preventDefault no admite esperar a una respuesta asíncrona—, y por eso
    // `hayConexion` se mantiene al día por su cuenta en vez de preguntarse en
    // este momento.
    evento.preventDefault();
    evento.stopPropagation();
    abrirPanel(campo);
  },
  true
);

// El foco por teclado (tabulador + Enter) no pasa por este `click` en todos los
// navegadores, así que se recuerda igualmente cuál es el campo en juego. Esto
// NO rellena el campo elegido para la inyección automática, solo para que un
// Gestor que navegue con teclado no se quede sin panel.
document.addEventListener(
  "focusin",
  (evento) => {
    if (evento.target instanceof HTMLInputElement && evento.target.type === "file")
      campoElegido = evento.target;
  },
  true
);

function base64AArchivo(base64, nombreArchivo, tipoMime) {
  const binario = atob(base64);
  const bytes = new Uint8Array(binario.length);
  for (let i = 0; i < binario.length; i++) bytes[i] = binario.charCodeAt(i);
  return new File([bytes], nombreArchivo, { type: tipoMime });
}

function inyectarEnInput(input, archivo) {
  // El truco DataTransfer, no una asignación directa a `.files` (esa
  // propiedad no tiene setter propio salvo el que los navegadores exponen
  // específicamente para este caso, compatible con la semántica de
  // arrastrar-y-soltar). Un input controlado por React puede no reaccionar a
  // esto en todas las plataformas — riesgo ya documentado, no resuelto aquí
  // por plataforma.
  const transferencia = new DataTransfer();
  transferencia.items.add(archivo);
  input.files = transferencia.files;

  input.dispatchEvent(new Event("input", { bubbles: true }));
  input.dispatchEvent(new Event("change", { bubbles: true }));
}

// --- El panel -------------------------------------------------------------
//
// Vive en un Shadow DOM cerrado sobre un contenedor propio: el CSS del portal
// no puede deformarlo y el nuestro no puede romper el suyo. Son páginas de
// terceros que no controlamos y que a veces llevan hojas de estilo agresivas.

let anfitrionPanel = null;
let raizPanel = null;

const ESTILOS_PANEL = `
  :host { all: initial; }
  .fondo {
    position: fixed; inset: 0; z-index: 2147483647;
    background: rgba(12, 16, 18, .45);
    display: flex; align-items: center; justify-content: center;
    font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
  }
  .panel {
    background: #fff; color: #15191b;
    width: min(520px, calc(100vw - 32px));
    max-height: min(600px, calc(100vh - 64px));
    border-radius: 10px; box-shadow: 0 12px 40px rgba(0,0,0,.3);
    display: flex; flex-direction: column; overflow: hidden;
  }
  header { padding: 16px 18px 12px; border-bottom: 1px solid #e3e2dc; }
  h2 { margin: 0 0 4px; font-size: 15px; font-weight: 600; }
  .sub { margin: 0; font-size: 12.5px; color: #5a6163; }
  .cuerpo { overflow-y: auto; padding: 8px 0; flex: 1; }
  .grupo { padding: 8px 18px 4px; font-size: 11px; letter-spacing: .08em;
           text-transform: uppercase; color: #5a6163; }
  .fila { display: flex; align-items: center; gap: 12px; padding: 8px 18px; }
  .fila span { flex: 1; font-size: 13.5px; }
  button {
    font: inherit; font-size: 13px; padding: 6px 12px; border-radius: 6px;
    border: 1px solid #c9c8c0; background: #fff; color: #15191b; cursor: pointer;
  }
  button.principal { background: #1d5b57; border-color: #1d5b57; color: #fff; }
  button:disabled { opacity: .55; cursor: default; }
  footer { padding: 12px 18px; border-top: 1px solid #e3e2dc;
           display: flex; gap: 10px; justify-content: space-between; }
  .aviso { padding: 12px 18px; font-size: 13px; color: #8a4718; }
  .vacio { padding: 16px 18px; font-size: 13px; color: #5a6163; }
`;

function cerrarPanel() {
  anfitrionPanel?.remove();
  anfitrionPanel = null;
  raizPanel = null;
}

function abrirPanel(campo) {
  cerrarPanel();

  anfitrionPanel = document.createElement("div");
  raizPanel = anfitrionPanel.attachShadow({ mode: "closed" });
  document.documentElement.appendChild(anfitrionPanel);

  const estilos = document.createElement("style");
  estilos.textContent = ESTILOS_PANEL;
  raizPanel.appendChild(estilos);

  const fondo = document.createElement("div");
  fondo.className = "fondo";
  fondo.addEventListener("click", (e) => {
    if (e.target === fondo) cerrarPanel();
  });
  raizPanel.appendChild(fondo);

  const panel = document.createElement("div");
  panel.className = "panel";
  fondo.appendChild(panel);

  const cabecera = document.createElement("header");
  const titulo = document.createElement("h2");
  titulo.textContent = "Elegir documento de TALVEG";
  const sub = document.createElement("p");
  sub.className = "sub";
  sub.textContent = "Se descargará y se pondrá en el campo que acabas de pulsar. El formulario lo envías tú.";
  cabecera.append(titulo, sub);
  panel.appendChild(cabecera);

  const cuerpo = document.createElement("div");
  cuerpo.className = "cuerpo";
  const cargando = document.createElement("p");
  cargando.className = "vacio";
  cargando.textContent = "Buscando tus documentos pendientes…";
  cuerpo.appendChild(cargando);
  panel.appendChild(cuerpo);

  const pie = document.createElement("footer");
  const botonEquipo = document.createElement("button");
  botonEquipo.type = "button";
  botonEquipo.textContent = "Buscar en mi equipo";
  botonEquipo.addEventListener("click", () => abrirExploradorDelSistema(campo));
  const botonCancelar = document.createElement("button");
  botonCancelar.type = "button";
  botonCancelar.textContent = "Cancelar";
  botonCancelar.addEventListener("click", cerrarPanel);
  pie.append(botonEquipo, botonCancelar);
  panel.appendChild(pie);

  document.addEventListener("keydown", cerrarConEscape, true);

  cargarPendientesEnPanelAsync(cuerpo, campo);
}

function cerrarConEscape(evento) {
  if (evento.key !== "Escape" || !anfitrionPanel) return;
  document.removeEventListener("keydown", cerrarConEscape, true);
  cerrarPanel();
}

// La salida. Sin esto, interceptar el clic convertiría la extensión en un
// secuestro del campo de archivo: el Gestor no podría subir un documento que
// tenga en su disco y no esté en TALVEG, que es un caso real y frecuente.
function abrirExploradorDelSistema(campo) {
  cerrarPanel();
  campoConPasoLibre = campo;
  campo.click();
}

async function cargarPendientesEnPanelAsync(cuerpo, campo) {
  let resultado;
  try {
    resultado = await chrome.runtime.sendMessage({ accion: "listarPendientes" });
  } catch (error) {
    resultado = { ok: false, error: `No pudimos hablar con la extensión (${error.message}).` };
  }

  if (!anfitrionPanel) return; // lo cerraron mientras cargaba.
  cuerpo.replaceChildren();

  if (!resultado?.ok) {
    const aviso = document.createElement("p");
    aviso.className = "aviso";
    aviso.textContent = `${resultado?.error ?? "No pudimos leer tus documentos."} Puedes buscar el archivo en tu equipo.`;
    cuerpo.appendChild(aviso);
    return;
  }

  const filas = aplanarDocumentos(resultado.proveedores);
  if (filas.length === 0) {
    const vacio = document.createElement("p");
    vacio.className = "vacio";
    vacio.textContent = "No tienes documentos pendientes de subir. Si el portal te pide otra cosa, búscala en tu equipo.";
    cuerpo.appendChild(vacio);
    return;
  }

  let grupoActual = null;
  for (const fila of filas) {
    if (fila.grupo !== grupoActual) {
      grupoActual = fila.grupo;
      const encabezado = document.createElement("p");
      encabezado.className = "grupo";
      encabezado.textContent = grupoActual;
      cuerpo.appendChild(encabezado);
    }

    cuerpo.appendChild(construirFila(fila.documento, fila.proveedorActivo, campo));
  }
}

// Aplana proveedores → clientes → documentos conservando el orden, y trae de
// cada nivel lo que hace falta para decidir: el rótulo del grupo y si el
// conector sigue activo (kill switch remoto, MVP2 § 14.5 — misma comprobación
// que hace el popup, y por el mismo motivo: si TALVEG apagó ese conector, ni
// se ofrece el botón).
function aplanarDocumentos(proveedores) {
  const filas = [];

  for (const proveedor of proveedores ?? []) {
    for (const cliente of proveedor.clientes ?? []) {
      for (const documento of cliente.documentos ?? []) {
        filas.push({
          grupo: `${proveedor.proveedorNombre} · ${cliente.clienteNombre}`,
          proveedorActivo: proveedor.proveedorActivo !== false,
          documento,
        });
      }
    }
  }

  return filas;
}

function construirFila(documento, proveedorActivo, campo) {
  const fila = document.createElement("div");
  fila.className = "fila";

  const descripcion = document.createElement("span");
  descripcion.textContent = `${documento.propietarioNombre} — ${documento.tipoDocumentoNombre}`;
  if (documento.estado === "Rechazada") descripcion.textContent += " (rechazada antes)";
  fila.appendChild(descripcion);

  // Ritmo humano (MVP2 § 14.5): un botón por documento, un clic, una subida.
  // Este panel es el SEGUNDO origen posible de esa acción —el primero es el
  // popup— y respeta la misma regla: nunca una selección múltiple, nunca un
  // «subir todos», nunca un bucle. Lo que cambia respecto al popup es solo
  // dónde está el botón, no cuántas veces se puede pulsar.
  const boton = document.createElement("button");
  boton.type = "button";
  boton.className = "principal";
  boton.textContent = "Poner aquí";
  boton.disabled = !proveedorActivo;
  boton.addEventListener("click", () => elegirDocumentoAsync(documento, boton, fila, campo));
  fila.appendChild(boton);

  return fila;
}

async function elegirDocumentoAsync(documento, boton, fila, campo) {
  boton.disabled = true;
  boton.textContent = "Poniendo…";

  // Se fija aquí, y no al abrir el panel, porque entre medias el Gestor pudo
  // pinchar en otro sitio de la página. El campo que vale es el que originó
  // este panel.
  campoElegido = campo;

  let resultado;
  try {
    resultado = await chrome.runtime.sendMessage({
      accion: "subirDocumento",
      documentoId: documento.documentoId,
      acreditacionId: documento.acreditacionId,
      nombreArchivo: `${documento.tipoDocumentoNombre}.pdf`,
    });
  } catch (error) {
    resultado = { ok: false, error: `No pudimos hablar con la extensión (${error.message}).` };
  }

  if (resultado?.ok) {
    cerrarPanel();
    return;
  }

  boton.disabled = false;
  boton.textContent = "Poner aquí";
  const aviso = document.createElement("p");
  aviso.className = "aviso";
  aviso.textContent = resultado?.error ?? "No pudimos poner el documento en el formulario.";
  fila.after(aviso);
}

// --- Depositar el fichero -------------------------------------------------

chrome.runtime.onMessage.addListener((mensaje, _remitente, enviarRespuesta) => {
  // El service worker avisa al conectar y al desconectar. Sin esto, quien se
  // conecta con la pestaña del portal ya abierta tendría que recargarla para
  // que la extensión hiciera algo, sin ninguna pista de por qué.
  if (mensaje?.accion === "conexionCambiada") {
    // El aviso manda sobre cualquier consulta en vuelo: invalida su sello.
    selloDeConexion++;
    hayConexion = Boolean(mensaje.conectado);
    if (!hayConexion) cerrarPanel();
    enviarRespuesta({ ok: true });
    return true;
  }

  if (mensaje?.accion !== "inyectarArchivo") return false;

  try {
    // Sin respaldo al primer input de la página. Un formulario CAE tiene una
    // fila por tipo de documento, así que «el primero que haya» es casi
    // siempre el equivocado, y equivocarse aquí no da error: el portal acepta
    // el archivo y lo acredita contra otro requisito. Mejor no hacer nada y
    // decir por qué.
    if (!campoElegido || !campoElegido.isConnected) {
      enviarRespuesta({
        ok: false,
        error: "Pulsa primero el campo de archivo donde quieres el documento: la extensión lo deja exactamente ahí, y no adivina cuál es.",
      });
      return true;
    }

    const archivo = base64AArchivo(mensaje.base64, mensaje.nombreArchivo, mensaje.tipoMime);
    inyectarEnInput(campoElegido, archivo);

    enviarRespuesta({ ok: true });
  } catch (error) {
    enviarRespuesta({ ok: false, error: `No pudimos rellenar el formulario (${error.message}).` });
  }

  return true;
});
