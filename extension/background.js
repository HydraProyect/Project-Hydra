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

async function conectar(hydraUrl, token, expiraEnUtc) {
  const origen = normalizarOrigen(hydraUrl);
  if (!origen) return { ok: false, error: "La URL de Hydra no es válida." };

  // Sin chrome.permissions.request: host_permissions ya cubre de forma
  // estática los dos orígenes de TALVEG (ver cabecera del fichero) — no hace
  // falta pedir nada en caliente, y por tanto tampoco depende de que esta
  // llamada conserve un gesto de usuario real a través de la mensajería.
  await chrome.storage.local.set({ hydraUrl: origen });
  await chrome.storage.session.set({ token, expiraEnUtc });
  return { ok: true };
}

async function desconectar() {
  await chrome.storage.session.remove(["token", "expiraEnUtc"]);
  return { ok: true };
}

async function peticionAutenticada(ruta, opciones = {}) {
  const { hydraUrl, token, conectado } = await obtenerConexion();
  if (!conectado) return { ok: false, error: "No hay una conexión activa con Hydra. Vuelve a conectar." };

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
// UN archivo por invocación — nunca un bucle sobre varios. La única forma
// de llamarla es un mensaje "subirDocumento" disparado por el clic real de
// un botón concreto en popup.js (ver el comentario gemelo ahí,
// renderizarDocumento) — no añadas aquí ningún camino que la invoque más de
// una vez por gesto de usuario (un "subir todos", un reintento automático
// en bucle...): es la base del argumento "lo hace el gestor, no un bot"
// frente a las plataformas externas.
async function subirDocumento({ documentoId, acreditacionId, nombreArchivo }) {
  const descarga = await peticionAutenticada(`/documentos/${documentoId}/archivo`);
  if (!descarga.ok) return descarga;
  if (!descarga.respuesta.ok) return { ok: false, error: `No pudimos descargar el PDF de Hydra (${descarga.respuesta.status}).` };

  const base64 = arrayBufferABase64(await descarga.respuesta.arrayBuffer());

  const [pestana] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!pestana?.id) return { ok: false, error: "No hay ninguna pestaña activa donde inyectar el archivo." };

  let respuestaContenido;
  try {
    respuestaContenido = await chrome.tabs.sendMessage(pestana.id, {
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

chrome.runtime.onMessage.addListener((mensaje, _remitente, enviarRespuesta) => {
  const manejadores = {
    obtenerConexion: () => obtenerConexion(),
    conectar: (m) => conectar(m.hydraUrl, m.token, m.expiraEnUtc),
    desconectar: () => desconectar(),
    listarPendientes: () => listarPendientes(),
    subirDocumento: (m) => subirDocumento(m),
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
//     receptor), así que se comunican con el mismo mensaje al usuario
//     ("instala o actualiza la extensión").
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
