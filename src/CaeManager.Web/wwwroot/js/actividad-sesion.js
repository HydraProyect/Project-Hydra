// Señal de interacción para el cierre de sesión por inactividad (ver
// CaducidadSesion.cs).
//
// Una conexión de Blazor abierta no es actividad: el servidor solo cuenta esta
// señal. Escucha EXCLUSIVAMENTE acciones sobre la interfaz: `click`, `input`,
// `change` y `submit`. No escucha `mousemove`, `keydown`, `scroll` ni ningún
// otro evento de periférico, no mira qué se pulsó o escribió y no envía nada
// más que la propia señal, como la circular de medición de tiempos promete a la
// plantilla. Leer sin tocar nada no cuenta como interacción.
//
// `isTrusted` deja fuera lo que dispare un guion: tiene que actuar la persona.

(function () {
    const RUTA = '/cuenta/sesion/actividad';
    const MILISEGUNDOS_ENTRE_SENALES = 60000;
    let ultimaSenal = -Infinity;

    function alInteractuar(evento) {
        if (!evento.isTrusted) return;

        const ahora = performance.now();
        if (ahora - ultimaSenal < MILISEGUNDOS_ENTRE_SENALES) return;
        ultimaSenal = ahora;

        // Sin sesión el servidor responde con un desafío: `manual` evita seguirlo.
        fetch(RUTA, { method: 'POST', credentials: 'same-origin', redirect: 'manual', keepalive: true })
            .catch(function () { });
    }

    for (const tipo of ['click', 'input', 'change', 'submit']) {
        document.addEventListener(tipo, alInteractuar, { capture: true, passive: true });
    }
})();
