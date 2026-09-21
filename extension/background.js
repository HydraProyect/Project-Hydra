// Service worker (MV3) del MVP1 de integración con plataformas CAE externas.
// Ver ARQUITECTURA-INTEGRACIONES.md § 14 (repositorio de negocio) para el
// diseño completo. Este fichero es el único que guarda el token y habla con
// Hydra; el popup y el content script solo le mandan mensajes.
//
// Estado guardado:
//   chrome.storage.local:   { hydraUrl }               -- sobrevive a reinicios
//   chrome.storage.session: { token, expiraEnUtc }      -- se pierde al cerrar el navegador,
//                                                           igual de corto que la vigencia real (8h)
//                                                           que de todos modos impone el servidor.
//
// Conexión automática (DEC A', 2026-09-10): a diferencia del supuesto original
// ("cada tenant vive en su propio dominio"), TALVEG sirve todos los tenants
// desde un único dominio fijo por entorno (ver Tenant.cs / TenantActual.cs —
// el tenant se resuelve por claim de sesión, nunca por Host). Eso permite
// declarar `host_permissions` y `externally_connectable` de forma ESTÁTICA
// para esos dos orígenes en manifest.json, en vez de pedir el permiso en
// caliente con `chrome.permissions.request` (que además exigía un gesto de
// usuario real y fallaba con "must be called during a user gesture" si se
// disparaba desde un mensaje reenviado). `ORIGENES_TALVEG_PERMITIDOS` de abajo
// debe coincidir exactamente con `externally_connectable.matches` del
// manifiesto — es una comprobación deliberadamente duplicada (defensa en
// profundidad: el manifiesto ya filtra quién puede llegar a
// `onMessageExternal`, pero no cuesta nada volver a comprobar el origen del
// remitente dentro del propio manejador, por si alguna vez diverge).
const ORIGENES_TALVEG_PERMITIDOS = ["https://app.talveg.es", "https://staging.talveg.es"];

async function obtenerConexion() {
  const { hydraUrl } = await chrome.storage.local.get("hydraUrl");
  const { token, expiraEnUtc } = await chrome.storage.session.get(["token", "expiraEnUtc"]);
  const conectado = Boolean(hydraUrl && token && expiraEnUtc && new Date(expiraEnUtc) > new Date());
  return { hydraUrl: hydraUrl ?? null, token: token ?? null, expiraEnUtc: expiraEnUtc ?? null, conectado };
}

function normalizarOrigen(url) {
  try {
    return new URL(url).origin;
  } catch {
    return null;
  }
}

// El content script de cada portal necesita saber si hay conexión ANTES de que
// el Gestor CAE pinche un campo de archivo: la decisión de interceptar ese clic
// tiene que ser síncrona (preventDefault no espera a nadie), así que no puede
// preguntarlo en ese momento. Sin este aviso, quien se conecta con una pestaña
// del portal ya abierta tendría que recargarla para que la extensión hiciera
// algo, sin ninguna pista de por qué.
//
// Los fallos se ignoran uno a uno a propósito: la mayoría de las pestañas no
// tienen este content script —no son portales CAE— y ahí `sendMessage` rechaza
// siempre. No es un error, es la respuesta normal.
async function avisarDeLaConexion(conectado) {
  let pestanas;
  try {
    pestanas = await chrome.tabs.query({});
  } catch {
    return;
  }

  for (const pestana of pestanas) {
    if (!pestana.id) continue;
    chrome.tabs.sendMessage(pestana.id, { accion: "conexionCambiada", conectado }).catch(() => {});
  }
}

async function conectar(hydraUrl, token, expiraEnUtc) {
  const origen = normalizarOrigen(hydraUrl);
  if (!origen) return { ok: false, error: "La URL de Hydra no es válida." };

  // Sin chrome.permissions.request: host_permissions ya cubre de forma
  // estática los dos orígenes de TALVEG (ver cabecera del fichero) — no hace
  // falta pedir nada en caliente, y por tanto tampoco depende de que esta
  // llamada conserve un gesto de usuario real a través de la mensajería.
  await chrome.storage.local.set({ hydraUrl: origen });
  await chrome.storage.session.set({ token, expiraEnUtc });
  await avisarDeLaConexion(true);
  return { ok: true };
}

async function desconectar() {
  await chrome.storage.session.remove(["token", "expiraEnUtc"]);
  await avisarDeLaConexion(false);
  return { ok: true };
}

// Salida cuando el enlace automático no está disponible: ni el entorno tiene
// configurado `Extension:IdChromeStore`, ni Chrome expone `chrome.runtime` a
// la página (origen no declarado en `externally_connectable`). Sin esto, el
// gestor se queda mirando un popup que dice "se conecta sola" mientras no se
// conecta, sin ningún sitio donde pegar nada — que es exactamente lo que pasó.
//
// El código lleva los TRES datos porque `obtenerConexion` considera "no
// conectado" todo lo que no traiga caducidad: un token pegado a secas no
// serviría. Formato: base64 de {u, t, e} — contrato con
// CodigoConexionExtension.cs del lado de Hydra, NO es cifrado ni ofuscación.
function leerCodigoConexion(codigo) {
  if (typeof codigo !== "string" || !codigo.trim()) return null;

  let carga;
  try {
    carga = JSON.parse(atob(codigo.trim()));
  } catch {
    return null;
  }

  const { u: hydraUrl, t: token, e: expiraEnUtc } = carga ?? {};
  if (typeof hydraUrl !== "string" || typeof token !== "string" || typeof expiraEnUtc !== "string") return null;
  if (Number.isNaN(Date.parse(expiraEnUtc))) return null;

  return { hydraUrl, token, expiraEnUtc };
}

async function conectarManual({ codigo }) {
  const carga = leerCodigoConexion(codigo);
  if (!carga) return { ok: false, error: "Ese código no es válido. Cópialo entero desde Hydra." };

  if (new Date(carga.expiraEnUtc) <= new Date())
    return { ok: false, error: "Ese código ya caducó. Genera otro en Hydra." };

  const conexion = await conectar(carga.hydraUrl, carga.token, carga.expiraEnUtc);
  if (!conexion.ok) return conexion;

  // Se comprueba contra Hydra ANTES de dar la conexión por buena: guardar el
  // token y decir "conectado" sin haberlo usado convierte un código erróneo en
  // un fallo más tarde, en otra pantalla, sin relación aparente con lo que se
  // acaba de pegar.
  const prueba = await listarPendientes();
  if (!prueba.ok) {
    await desconectar();
    return { ok: false, error: prueba.error };
  }

  return { ok: true };
}

async function peticionAutenticada(ruta, opciones = {}) {
  const { hydraUrl, token, conectado } = await obtenerConexion();
  if (!conectado) {
    // No basta con devolver el error: si había algo guardado y lo que pasa es
    // que caducó, hay que limpiarlo y avisar a las pestañas. Sin esto, una
    // pestaña del portal abierta desde antes se queda creyendo que hay conexión
    // y sigue interceptando cada clic en un campo de archivo para abrir un
    // panel que ya no puede listar nada.
    await desconectar();
    return { ok: false, error: "No hay una conexión activa con Hydra. Vuelve a conectar." };
  }

  let respuesta;
  try {
    respuesta = await fetch(`${hydraUrl}${ruta}`, {
      ...opciones,
      // "manual": estos endpoints están protegidos con una política que
      // combina el esquema de extensión con el de cookie de Identity
      // (Policies.SesionOExtension). Cuando ninguno de los dos autentica, el
      // reto que gana es el de la cookie: una redirección 302 a la pantalla
      // de login, no un 401 limpio (comprobado contra el servidor real, no
      // supuesto). Con el "follow" por defecto, fetch sigue esa redirección
      // sola, entrega la página de login con status 200, y el .json() de más
      // abajo lanzaría una excepción sin control al intentar parsear HTML.
      // "manual" deja la redirección sin seguir para poder tratarla aquí como
      // lo que es: sesión/token inválidos, igual que un 401 de verdad.
      redirect: "manual",
      headers: { ...(opciones.headers ?? {}), Authorization: `Extension ${token}` },
    });
  } catch (error) {
    return { ok: false, error: `No pudimos contactar con Hydra (${error.message}).` };
  }

  if (respuesta.status === 401 || respuesta.type === "opaqueredirect") {
    // Mismo criterio que el servidor (ExtensionAuthenticationHandler): un
    // token caducado o revocado no distingue el motivo. Se limpia aquí para
    // que el popup vuelva a pedir conexión en vez de seguir fallando en bucle.
    await desconectar();
    return { ok: false, error: "Tu conexión con Hydra caducó. Vuelve a conectar." };
  }

  return { ok: true, respuesta };
}

async function listarPendientes() {
  const resultado = await peticionAutenticada("/extension/acreditaciones-pendientes");
  if (!resultado.ok) return resultado;

  if (!resultado.respuesta.ok)
    return { ok: false, error: `Hydra respondió ${resultado.respuesta.status}.` };

  return { ok: true, proveedores: await resultado.respuesta.json() };
}

function arrayBufferABase64(buffer) {
  let binario = "";
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < bytes.byteLength; i++) binario += String.fromCharCode(bytes[i]);
  return btoa(binario);
}

// Ritmo humano (MVP2 § 14.5): esta función descarga UN documento e inyecta
// UN archivo por invocación — nunca un bucle sobre varios. Hay DOS formas de
// llamarla, y las dos nacen del clic real del Gestor CAE sobre un documento
// concreto: el botón "Subir" del popup (ver el comentario gemelo en popup.js,
// renderizarDocumento) y el botón "Poner aquí" del panel que content.js abre
// cuando se pulsa un campo de archivo del portal (construirFila). Lo que
// comparten no es dónde está el botón, sino cuántas veces se puede pulsar: una
// por documento. No añadas aquí ningún camino que la invoque más de una vez
// por gesto de usuario (un "subir todos", un reintento automático en bucle...):
// es la base del argumento "lo hace el gestor, no un bot" frente a las
// plataformas externas.
async function subirDocumento({ documentoId, acreditacionId, nombreArchivo }, pestanaQueLoPidio) {
  const descarga = await peticionAutenticada(`/documentos/${documentoId}/archivo`);
  if (!descarga.ok) return descarga;
  if (!descarga.respuesta.ok) return { ok: false, error: `No pudimos descargar el PDF de Hydra (${descarga.respuesta.status}).` };

  const base64 = arrayBufferABase64(await descarga.respuesta.arrayBuffer());

  // Cuando la petición nace del panel del content script, el archivo va a ESA
  // pestaña, no a la que esté activa cuando termine la descarga. El PDF tarda,
  // y en ese rato el Gestor CAE puede cambiar de pestaña: con la pestaña activa
  // el documento acabaría en otro portal que también tuviera un campo elegido,
  // la respuesta sería `ok` y TALVEG marcaría la acreditación como subida. El
  // mismo error que este incremento vino a cerrar, un nivel más arriba.
  // El popup no tiene pestaña propia (`remitente.tab` es undefined), así que
  // para él sigue valiendo la activa: es la que el Gestor está mirando mientras
  // el popup está abierto.
  const idPestana =
    pestanaQueLoPidio ?? (await chrome.tabs.query({ active: true, currentWindow: true }))[0]?.id;
  if (!idPestana) return { ok: false, error: "No hay ninguna pestaña activa donde inyectar el archivo." };

  let respuestaContenido;
  try {
    respuestaContenido = await chrome.tabs.sendMessage(idPestana, {
      accion: "inyectarArchivo",
      base64,
      nombreArchivo: nombreArchivo || "documento.pdf",
      tipoMime: "application/pdf",
    });
  } catch {
    return {
      ok: false,
      error: "Esta pestaña no tiene el content script cargado (¿es una plataforma CAE reconocida? ¿recargaste la página tras instalar la extensión?).",
    };
  }

  if (!respuestaContenido?.ok)
    return { ok: false, error: respuestaContenido?.error ?? "No pudimos rellenar el formulario de la plataforma." };

  // Marcar "subida" es un acto humano confirmando que el formulario se rellenó
  // — no que la plataforma ya lo aceptó (eso lo revisa el gestor manualmente
  // y lo registra con "Marcar aceptado"/"Marcar rechazado" en Hydra).
  const marcado = await peticionAutenticada(`/extension/acreditaciones/${acreditacionId}/subida`, { method: "POST" });
  if (!marcado.ok) return marcado;
  if (!marcado.respuesta.ok) {
    // Kill switch remoto (MVP2 § 14.5): un 409 aquí es
    // "Acreditacion.ConectorInactivo" — segunda línea de defensa, poco
    // probable de alcanzar porque popup.js ya deja de ofrecer "Subir" para
    // un proveedor inactivo (mismo dato, leído antes). Se muestra el
    // detalle real del problema en vez de un genérico "Hydra no aceptó".
    let detalle;
    try {
      detalle = (await marcado.respuesta.json())?.detail;
    } catch {
      // Cuerpo no JSON (o vacío) — se usa el mensaje genérico de abajo.
    }
    return { ok: false, error: detalle ?? `Hydra no aceptó marcarla como subida (${marcado.respuesta.status}).` };
  }

  return { ok: true };
}

chrome.runtime.onMessage.addListener((mensaje, remitente, enviarRespuesta) => {
  const manejadores = {
    obtenerConexion: () => obtenerConexion(),
    conectar: (m) => conectar(m.hydraUrl, m.token, m.expiraEnUtc),
    conectarManual: (m) => conectarManual(m),
    desconectar: () => desconectar(),
    listarPendientes: () => listarPendientes(),
    subirDocumento: (m) => subirDocumento(m, remitente?.tab?.id),
  };

  const manejador = manejadores[mensaje?.accion];
  if (!manejador) return false;

  manejador(mensaje).then(enviarRespuesta);
  return true; // respuesta asíncrona
});

// Puente de enlace automático TALVEG -> extensión (DEC A'). A diferencia de
// chrome.runtime.onMessage (arriba, solo popup/content script de esta MISMA
// extensión), onMessageExternal es el canal que Chrome expone a páginas web
// autorizadas por `externally_connectable.matches` — cualquier otra página no
// tiene siquiera `chrome.runtime.sendMessage` disponible para esta extensión.
//
// Cuatro condiciones de aceptación de la DEC, todas aplicadas aquí:
//  1. Se valida el origen del remitente ADEMÁS de lo que ya filtra el
//     manifiesto — nunca fiarse de una sola capa para una interfaz de
//     confianza entre la app y la extensión.
//  2. El contrato de mensaje es estrecho: un `tipo` versionado
//     ("hydra.conectarExtension.v1"), campos exactos, y cualquier otra forma
//     se rechaza. Esto NO es una API genérica de ejecución desde la web.
//  3. La respuesta siempre incluye `versionExtension` cuando la extensión
//     responde, para que la página distinga "conectó bien" de "instalada
//     pero algo falló" — la tercera situación, "no instalada o versión tan
//     vieja que ni siquiera tiene este listener", es indistinguible desde la
//     página (chrome.runtime.sendMessage falla igual en ambos casos: no hay
//     receptor). La página ya NO las junta bajo "instala o actualiza la
//     extensión": desde 2026-09-21 distingue cuatro situaciones y ofrece el
//     código de conexión cuando no hay versión que leer (ver ResultadoEnlace y
//     CompatibilidadExtension en el repositorio de la app). Aquí no cambia
//     nada; se anota porque este comentario describía el mensaje de enfrente.
//  4. Qué ID de extensión llama la página es una decisión de configuración
//     de Hydra (Extension:IdChromeStore), no de este fichero — ver
//     ConectarExtension.razor.cs en el repositorio de la app.
function origenDelRemitente(remitente) {
  if (remitente.origin) return remitente.origin;
  return normalizarOrigen(remitente.url ?? "");
}

chrome.runtime.onMessageExternal.addListener((mensaje, remitente, enviarRespuesta) => {
  if (!ORIGENES_TALVEG_PERMITIDOS.includes(origenDelRemitente(remitente))) {
    enviarRespuesta({ ok: false, error: "Origen no autorizado." });
    return false;
  }

  if (mensaje?.tipo !== "hydra.conectarExtension.v1") {
    enviarRespuesta({ ok: false, error: "Tipo de mensaje no reconocido." });
    return false;
  }

  const { hydraUrl, token, expiraEnUtc } = mensaje;
  if (typeof hydraUrl !== "string" || typeof token !== "string" || typeof expiraEnUtc !== "string") {
    enviarRespuesta({ ok: false, error: "Mensaje de conexión incompleto." });
    return false;
  }

  conectar(hydraUrl, token, expiraEnUtc).then((resultado) =>
    enviarRespuesta({ ...resultado, versionExtension: chrome.runtime.getManifest().version })
  );
  return true; // respuesta asíncrona
});
