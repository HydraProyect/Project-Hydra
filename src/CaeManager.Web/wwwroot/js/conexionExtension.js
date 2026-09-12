// Puente de enlace automático con la extensión de navegador (DEC A',
// 2026-09-10 — ver ARQUITECTURA-INTEGRACIONES.md § 14, repositorio de
// negocio). Envuelve `chrome.runtime.sendMessage(idExtension, ...)` — la API
// que Chrome expone a esta página SOLO porque el origen está declarado en
// `externally_connectable.matches` del manifiesto de la extensión (ver
// extension/background.js). Ningún dato sale de esta página hacia fuera de
// la extensión: no hay red, no hay servidor de por medio.
export function conectar(idExtension, mensaje) {
  return new Promise((resolve) => {
    if (typeof idExtension !== "string" || !idExtension || !window.chrome?.runtime?.sendMessage) {
      // Sin `chrome.runtime` disponible para este origen no hay forma de
      // distinguir "extensión no instalada" de "versión tan vieja que no
      // declara este origen en su manifiesto" — ambas se comunican igual.
      resolve({ disponible: false });
      return;
    }

    try {
      chrome.runtime.sendMessage(idExtension, mensaje, (respuesta) => {
        if (chrome.runtime.lastError || !respuesta) {
          resolve({ disponible: false });
          return;
        }
        resolve({ disponible: true, ...respuesta });
      });
    } catch {
      resolve({ disponible: false });
    }
  });
}
