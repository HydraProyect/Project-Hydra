// Service worker (MV3) del MVP1 de integración con plataformas CAE externas.
// Ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 14 (repositorio de negocio) para el
// diseño completo. Este fichero es el único que guarda el token y habla con
// Hydra; el popup y el content script solo le mandan mensajes.
//
// Estado guardado:
//   chrome.storage.local:   { hydraUrl }               -- sobrevive a reinicios
//   chrome.storage.session: { token, expiraEnUtc }      -- se pierde al cerrar el navegador,
//                                                           igual de corto que la vigencia real (8h)
//                                                           que de todos modos impone el servidor.
//   chrome.storage.session: { enviosPendientes, enviosConfirmados } -- «Lo he enviado» (E1): por
//                                                           pestaña, la acreditación puesta en el formulario
//                                                           y aún sin confirmar, y la última confirmada que
//                                                           se puede deshacer. Solo Ids, el tipo de documento
//                                                           y caducidades; nada del Trabajador ni el PDF.
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
// El aviso lleva la caducidad para que cada pestaña pueda apagarse sola cuando
// llegue la hora. El vencimiento de un token no produce ningún evento por su
// cuenta: si nadie hace una petición, nadie se entera de que pasó.
async function avisarDeLaConexion(conectado, expiraEnUtc = null) {
  let pestanas;
  try {
    pestanas = await chrome.tabs.query({});
  } catch {
    return;
  }

  for (const pestana of pestanas) {
    if (!pestana.id) continue;
    chrome.tabs
      .sendMessage(pestana.id, { accion: "conexionCambiada", conectado, expiraEnUtc })
      .catch(() => {});
  }
}

async function conectar(hydraUrl, token, expiraEnUtc) {
  const origen = normalizarOrigen(hydraUrl);
  if (!origen) return { ok: false, error: "La URL de TALVEG no es válida." };

  // Sin chrome.permissions.request: host_permissions ya cubre de forma
  // estática los dos orígenes de TALVEG (ver cabecera del fichero) — no hace
  // falta pedir nada en caliente, y por tanto tampoco depende de que esta
  // llamada conserve un gesto de usuario real a través de la mensajería.
  await chrome.storage.local.set({ hydraUrl: origen });
  await chrome.storage.session.set({ token, expiraEnUtc });
  await avisarDeLaConexion(true, expiraEnUtc);
  return { ok: true };
}

// Solo el «Desconectar» que pide la persona (popup) borra los envíos sin
// confirmar: quien se desconecta a propósito no quiere que se le ofrezcan a
// quien conecte después. La desconexión automática (401, redirección al login o
// caducidad de las 8 h) los conserva, para que «Lo he enviado» se pueda
// reintentar al volver a conectar; caducan solos a los 30 min, y el servidor
// vuelve a comprobar la cartera de quien confirme.
async function desconectar({ borrarEnvios = false } = {}) {
  const claves = ["token", "expiraEnUtc"];
  if (borrarEnvios) claves.push(CLAVE_ENVIOS_PENDIENTES, CLAVE_ENVIOS_CONFIRMADOS);
  await chrome.storage.session.remove(claves);
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
  if (!carga) return { ok: false, error: "Ese código no es válido. Cópialo entero desde TALVEG." };

  if (new Date(carga.expiraEnUtc) <= new Date())
    return { ok: false, error: "Ese código ya caducó. Genera otro en TALVEG." };

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
    return { ok: false, error: "No hay una conexión activa con TALVEG. Vuelve a conectar." };
  }

  let respuesta;
  try {
    respuesta = await fetch(`${hydraUrl}${ruta}`, {
      ...opciones,
      credentials: "omit",
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
    return { ok: false, error: `No pudimos contactar con TALVEG (${error.message}).` };
  }

  if (respuesta.status === 401 || respuesta.type === "opaqueredirect") {
    // Mismo criterio que el servidor (ExtensionAuthenticationHandler): un
    // token caducado o revocado no distingue el motivo. Se limpia aquí para
    // que el popup vuelva a pedir conexión en vez de seguir fallando en bucle.
    await desconectar();
    return { ok: false, error: "Tu conexión con TALVEG caducó. Vuelve a conectar." };
  }

  return { ok: true, respuesta };
}

async function listarPendientes() {
  const resultado = await peticionAutenticada("/extension/acreditaciones-pendientes");
  if (!resultado.ok) return resultado;

  if (!resultado.respuesta.ok)
    return { ok: false, error: `TALVEG respondió ${resultado.respuesta.status}.` };

  return { ok: true, proveedores: await resultado.respuesta.json() };
}

function arrayBufferABase64(buffer) {
  let binario = "";
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < bytes.byteLength; i++) binario += String.fromCharCode(bytes[i]);
  return btoa(binario);
}

// ¿Es `host` el dominio del Proveedor o uno de sus subdominios? Misma regla que
// content.js (hostCoincide) y que el servidor (CoincideHost): exacto o por sufijo
// con punto delimitador.
function hostCoincide(host, dominio) {
  if (typeof host !== "string" || typeof dominio !== "string") return false;
  const h = host.trim().toLowerCase();
  const d = dominio.trim().toLowerCase();
  if (!h || !d) return false;
  return h === d || h.endsWith("." + d);
}

// El host de la pestaña donde va el documento. Desde el panel, la URL la da el
// remitente del mensaje (la propia pestaña); el popup no tiene pestaña propia, así
// que se pregunta por la pestaña elegida. Sin host legible devuelve null y no se
// sube nada.
async function hostDeLaPestana(idPestana, urlDelRemitente) {
  let url = urlDelRemitente;
  if (!url) {
    try {
      url = (await chrome.tabs.get(idPestana))?.url;
    } catch {
      return null;
    }
  }
  return hostDeUnaUrl(url);
}

function hostDeUnaUrl(url) {
  try {
    return new URL(url).hostname || null;
  } catch {
    return null;
  }
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
async function subirDocumento({ documentoId, acreditacionId, nombreArchivo }, pestanaQueLoPidio, urlDelRemitente) {
  // El host de la pestaña se lee antes de descargar nada: sin él no hay forma de
  // saber de qué portal se trata y todo lo demás sería trabajo perdido.
  const idPestanaDestino =
    pestanaQueLoPidio ?? (await chrome.tabs.query({ active: true, currentWindow: true }))[0]?.id;
  if (!idPestanaDestino) return { ok: false, error: "No hay ninguna pestaña activa donde inyectar el archivo." };
  const dominioPortal = await hostDeLaPestana(idPestanaDestino, urlDelRemitente);
  if (!dominioPortal)
    return { ok: false, error: "No pudimos saber en qué portal está esta pestaña, así que no se pone el documento." };

  const descarga = await peticionAutenticada(`/documentos/${documentoId}/archivo`);
  if (!descarga.ok) return descarga;
  if (!descarga.respuesta.ok) return { ok: false, error: `No pudimos descargar el PDF de TALVEG (${descarga.respuesta.status}).` };

  const base64 = arrayBufferABase64(await descarga.respuesta.arrayBuffer());

  // Cuando la petición nace del panel del content script, el archivo va a ESA
  // pestaña, no a la que esté activa cuando termine la descarga. El PDF tarda,
  // y en ese rato el Gestor CAE puede cambiar de pestaña: con la pestaña activa
  // el documento acabaría en otro portal que también tuviera un campo elegido,
  // la respuesta sería `ok` y la confirmación «Lo he enviado» se pediría ahí. El
  // mismo error que este incremento vino a cerrar, un nivel más arriba.
  // El popup no tiene pestaña propia (`remitente.tab` es undefined), así que
  // para él sigue valiendo la activa: es la que el Gestor está mirando mientras
  // el popup está abierto.
  // (La pestaña y su host se fijaron arriba, antes de descargar.)
  const idPestana = idPestanaDestino;

  // Revalidar después de descargar y antes de entregar nada al formulario.
  const pendientes = await listarPendientes();
  if (!pendientes.ok) return pendientes;
  const proveedores = Array.isArray(pendientes.proveedores) ? pendientes.proveedores : [];
  const dueno = proveedores.find((proveedor) => (proveedor.clientes ?? []).some((cliente) =>
    (cliente.documentos ?? []).some((documento) =>
      documento.acreditacionId === acreditacionId && documento.documentoId === documentoId)));
  if (dueno?.proveedorActivo === false)
    return { ok: false, error: "Conector desactivado temporalmente. Contacta con soporte si lo necesitas." };
  if (!dueno)
    return { ok: false, error: "Esta acreditaci\u00f3n ya no se puede subir. Actualiza la lista en TALVEG; si fue rechazada, renueva el documento." };

  // Un documento solo entra en el portal de SU Plataforma CAE. El panel ya no
  // ofrece los de otras, pero el popup lista todo, así que la comprobación que
  // cierra de verdad la puerta está aquí, antes de entregar nada al formulario.
  // Sin dominios en la respuesta no hay forma de saberlo: no se sube.
  if (!(dueno.dominios ?? []).some((dominio) => hostCoincide(dominioPortal, dominio)))
    return { ok: false, error: "Este documento es de otra plataforma CAE: no se puede poner en este portal." };

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

  // Poner el archivo en el formulario NO es subirlo (D-4 del plan de la
  // extensión, incremento E1): la persona aún tiene que revisarlo y pulsar
  // Enviar en la plataforma, y puede no hacerlo. Hasta el 0.4.x aquí se marcaba
  // «Subida» sin más; ahora se deja la acreditación pendiente de confirmar en
  // ESTA pestaña y la página le pregunta «Lo he enviado» / «No lo he enviado».
  // Solo la confirmación llama a TALVEG (confirmarEnvio).
  await anotarEnvioPendiente(idPestana, acreditacionId, (nombreArchivo || "documento").replace(/\.pdf$/i, ""));
  try {
    await chrome.tabs.sendMessage(idPestana, { accion: "pedirConfirmacionEnvio" });
  } catch {
    // La pestaña ya no escucha (se recargó o se cerró): al volver a cargar, el
    // content script pide sus envíos pendientes con obtenerEnviosPendientes.
  }

  return { ok: true, pendienteDeConfirmar: true };
}

// --- «Lo he enviado» --------------------------------------------------------
//
// El estado vive en chrome.storage.session y no en una variable: el service
// worker de MV3 se duerme a los ~30 s sin eventos, y la persona tarda más que
// eso en revisar el formulario y enviarlo. Si la plataforma recarga la página
// al enviar, el content script nuevo pide lo pendiente de su pestaña y vuelve a
// preguntar. Todo va por pestaña (remitente.tab.id, que pone Chrome, no el
// mensaje), y el content script nunca elige qué acreditación se anota: solo
// confirma una de las que ESTA extensión puso en SU pestaña.

const CLAVE_ENVIOS_PENDIENTES = "enviosPendientes";
const CLAVE_ENVIOS_CONFIRMADOS = "enviosConfirmados";
// Si en este tiempo no llega «Lo he enviado», el intento caduca y TALVEG no anota nada.
const CADUCIDAD_ENVIO_PENDIENTE_MS = 30 * 60 * 1000;
// La página ofrece «Deshacer» 10 s; el margen cubre el viaje del mensaje.
const VENTANA_DESHACER_MS = 15 * 1000;

async function leerPorPestana(clave) {
  const datos = await chrome.storage.session.get(clave);
  const mapa = datos?.[clave];
  return mapa && typeof mapa === "object" ? mapa : {};
}

async function guardarPorPestana(clave, mapa) {
  await chrome.storage.session.set({ [clave]: mapa });
}

// Cada cambio lee el mapa entero, lo modifica y lo escribe con un await entre
// medias: dos mensajes a la vez (poner B mientras se confirma A, o la poda de lo
// caducado) se pisarían y perderían o resucitarían un envío. Todo lo que
// escribe en estos mapas pasa de uno en uno por esta cola.
let colaDeEnvios = Promise.resolve();
function enSerie(tarea) {
  const resultado = colaDeEnvios.then(tarea);
  colaDeEnvios = resultado.catch(() => {});
  return resultado;
}

// Lo de una pestaña, sin lo caducado. Las claves son texto: storage las serializa así.
function vigentesDePestana(clave, idPestana) {
  return enSerie(() => podarYLeerDePestana(clave, idPestana));
}

async function podarYLeerDePestana(clave, idPestana) {
  const mapa = await leerPorPestana(clave);
  const ahora = Date.now();
  const propios = mapa[String(idPestana)] ?? {};
  const vigentes = Object.fromEntries(Object.entries(propios).filter(([, v]) => v?.caducaEn > ahora));
  if (Object.keys(vigentes).length !== Object.keys(propios).length) {
    if (Object.keys(vigentes).length === 0) delete mapa[String(idPestana)];
    else mapa[String(idPestana)] = vigentes;
    await guardarPorPestana(clave, mapa);
  }
  return vigentes;
}

function cambiarEnPestana(clave, idPestana, acreditacionId, valor) {
  return enSerie(async () => {
    const mapa = await leerPorPestana(clave);
    const propios = { ...(mapa[String(idPestana)] ?? {}) };
    if (valor === undefined) delete propios[acreditacionId];
    else propios[acreditacionId] = valor;
    if (Object.keys(propios).length === 0) delete mapa[String(idPestana)];
    else mapa[String(idPestana)] = propios;
    await guardarPorPestana(clave, mapa);
  });
}

async function anotarEnvioPendiente(idPestana, acreditacionId, documento) {
  await cambiarEnPestana(CLAVE_ENVIOS_PENDIENTES, idPestana, acreditacionId, {
    documento,
    caducaEn: Date.now() + CADUCIDAD_ENVIO_PENDIENTE_MS,
  });
}

async function obtenerEnviosPendientes(idPestana) {
  if (!idPestana) return { ok: true, pendientes: [] };
  const vigentes = await vigentesDePestana(CLAVE_ENVIOS_PENDIENTES, idPestana);
  return {
    ok: true,
    pendientes: Object.entries(vigentes).map(([acreditacionId, v]) => ({ acreditacionId, documento: v.documento })),
  };
}

async function leerDetalleDelProblema(respuesta) {
  try {
    return (await respuesta.json())?.detail;
  } catch {
    return undefined; // cuerpo no JSON (o vacío): el llamador usa su mensaje genérico.
  }
}

// «Lo he enviado»: la ÚNICA llamada que anota «Subida» en TALVEG. El servidor
// deja un cambio de estado con origen Extensión (Historial del Centro) y
// devuelve lo que pide el «Deshacer».
async function confirmarEnvio({ acreditacionId }, idPestana, urlDeLaPestana) {
  if (!idPestana) return { ok: false, error: "Esta confirmación solo se hace desde la pestaña de la plataforma." };
  const pendientes = await vigentesDePestana(CLAVE_ENVIOS_PENDIENTES, idPestana);
  if (!pendientes[acreditacionId])
    return { ok: false, error: "Ya no hay nada que confirmar aquí. Si lo enviaste, anótalo en Centro 360." };

  // El servidor vuelve a comprobar que este portal es el de la Plataforma CAE del
  // canal (segunda línea tras la puerta de subirDocumento, por si un cliente
  // desactualizado se la saltó). El host sale de la URL que Chrome pone al
  // mensaje de ESTA pestaña, no del contenido del mensaje.
  const marcado = await peticionAutenticada(`/extension/acreditaciones/${acreditacionId}/subida`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ dominioPortal: hostDeUnaUrl(urlDeLaPestana) }),
  });
  // Sin conexión, o con la conexión caducada (peticionAutenticada desconecta
  // sola, sin borrar los envíos): sigue pendiente, para reintentar al volver a
  // conectar mientras no pasen los 30 min. Solo el «Desconectar» del popup lo borra.
  if (!marcado.ok) return marcado;
  if (!marcado.respuesta.ok) {
    // Un 409 es un conector inactivo o un rechazo posterior a la lista: no se
    // arregla reintentando, así que deja de estar pendiente. Se muestra el
    // detalle real en vez de un genérico «TALVEG no aceptó».
    const detalle = await leerDetalleDelProblema(marcado.respuesta);
    if (marcado.respuesta.status === 409 || marcado.respuesta.status === 404)
      await cambiarEnPestana(CLAVE_ENVIOS_PENDIENTES, idPestana, acreditacionId, undefined);
    return { ok: false, error: detalle ?? `TALVEG no aceptó anotarlo (${marcado.respuesta.status}).` };
  }

  await cambiarEnPestana(CLAVE_ENVIOS_PENDIENTES, idPestana, acreditacionId, undefined);

  let anotado = null;
  try {
    anotado = await marcado.respuesta.json();
  } catch {
    // Un servidor anterior respondía 204 sin cuerpo: anotado, pero sin «Deshacer».
  }
  const puedeDeshacer = Boolean(anotado?.cambioId && anotado?.version);
  if (puedeDeshacer) {
    await cambiarEnPestana(CLAVE_ENVIOS_CONFIRMADOS, idPestana, acreditacionId, {
      cambioId: anotado.cambioId,
      version: anotado.version,
      caducaEn: Date.now() + VENTANA_DESHACER_MS,
    });
  }
  return { ok: true, puedeDeshacer };
}

// «No lo he enviado»: no se anota nada y deja de estar pendiente.
async function descartarEnvio({ acreditacionId }, idPestana) {
  if (idPestana) await cambiarEnPestana(CLAVE_ENVIOS_PENDIENTES, idPestana, acreditacionId, undefined);
  return { ok: true };
}

// «Deshacer» del aviso «Anotado en TALVEG»: el cambio y la versión salen de lo
// que guardó confirmarEnvio para esta pestaña, nunca del mensaje.
async function deshacerEnvio({ acreditacionId }, idPestana) {
  if (!idPestana) return { ok: false, error: "Solo se deshace desde la pestaña de la plataforma." };
  const confirmados = await vigentesDePestana(CLAVE_ENVIOS_CONFIRMADOS, idPestana);
  const confirmado = confirmados[acreditacionId];
  if (!confirmado) return { ok: false, error: "Ya no se puede deshacer desde aquí. Corrígelo desde Centro 360." };
  await cambiarEnPestana(CLAVE_ENVIOS_CONFIRMADOS, idPestana, acreditacionId, undefined);

  const deshecho = await peticionAutenticada(`/extension/acreditaciones/${acreditacionId}/subida/deshacer`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ cambioId: confirmado.cambioId, version: confirmado.version }),
  });
  if (!deshecho.ok) return deshecho;
  if (!deshecho.respuesta.ok) {
    const detalle = await leerDetalleDelProblema(deshecho.respuesta);
    return { ok: false, error: detalle ?? `TALVEG no pudo deshacerlo (${deshecho.respuesta.status}). Corrígelo desde Centro 360.` };
  }
  return { ok: true };
}

// Una pestaña cerrada no va a confirmar nada.
chrome.tabs?.onRemoved?.addListener?.((idPestana) => enSerie(async () => {
  for (const clave of [CLAVE_ENVIOS_PENDIENTES, CLAVE_ENVIOS_CONFIRMADOS]) {
    const mapa = await leerPorPestana(clave);
    if (mapa[String(idPestana)]) {
      delete mapa[String(idPestana)];
      await guardarPorPestana(clave, mapa);
    }
  }
}));
chrome.runtime.onMessage.addListener((mensaje, remitente, enviarRespuesta) => {
  const manejadores = {
    obtenerConexion: () => obtenerConexion(),
    conectar: (m) => conectar(m.hydraUrl, m.token, m.expiraEnUtc),
    conectarManual: (m) => conectarManual(m),
    // El «Desconectar» del popup: el único que borra también los envíos sin confirmar.
    desconectar: () => desconectar({ borrarEnvios: true }),
    listarPendientes: () => listarPendientes(),
    // `remitente.url` solo es la de la pestaña cuando escribe un content script; la
    // del popup es la de su propia página de la extensión.
    subirDocumento: (m) => subirDocumento(m, remitente?.tab?.id, remitente?.tab ? remitente.url : undefined),
    // Solo desde el content script de una pestaña: remitente.tab lo pone Chrome.
    obtenerEnviosPendientes: () => obtenerEnviosPendientes(remitente?.tab?.id),
    confirmarEnvio: (m) => confirmarEnvio(m, remitente?.tab?.id, remitente?.url),
    descartarEnvio: (m) => descartarEnvio(m, remitente?.tab?.id),
    deshacerEnvio: (m) => deshacerEnvio(m, remitente?.tab?.id),
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
