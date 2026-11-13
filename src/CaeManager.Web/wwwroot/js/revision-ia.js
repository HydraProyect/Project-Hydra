// Revisión IA: preferencias de presentación en este navegador (el avance automático al terminar una revisión, D-49).
// Solo presentación: ningún dato de negocio pasa por aquí. localStorage puede estar vacío o bloqueado; cada acceso lo tolera.

export function leer(clave) {
    try {
        return window.localStorage.getItem(clave);
    } catch {
        return null;
    }
}

export function guardar(clave, valor) {
    try {
        window.localStorage.setItem(clave, valor);
        return true;
    } catch {
        return false;
    }
}