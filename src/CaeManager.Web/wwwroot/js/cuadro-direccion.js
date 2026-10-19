// Cuadro de dirección: preferencias de presentación en este navegador y descarga de tablas en CSV.
// Todo es presentación (qué tarjetas se ven, en qué orden, qué aviso se cerró): ningún dato de negocio
// pasa por aquí. localStorage puede estar vacío o bloqueado; cada acceso lo tolera.

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

// Descarga un texto como fichero. El BOM hace que Excel abra el CSV en UTF-8.
export function descargar(nombre, texto, tipo) {
    const blob = new Blob(['﻿', texto], { type: tipo || 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const enlace = document.createElement('a');
    enlace.href = url;
    enlace.download = nombre;
    document.body.appendChild(enlace);
    enlace.click();
    enlace.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// Presentación (maqueta, «Presentar»): pantalla completa del navegador al entrar y al salir. Sin soporte, no hace nada.
export function pantallaCompleta(activar) {
    try {
        if (activar && document.documentElement.requestFullscreen) {
            document.documentElement.requestFullscreen();
        } else if (!activar && document.fullscreenElement) {
            document.exitFullscreen();
        }
    } catch {
        // Sin pantalla completa: la presentación sigue en la ventana.
    }
}
