// Medición de tiempo de enfoque por gestión.
//
// Escucha EXCLUSIVAMENTE el ciclo de vida de la página: `visibilitychange` del
// documento y `focus`/`blur` de la ventana. No registra ni registrará nunca
// `mousemove`, `keydown`, `scroll` ni ningún otro evento de periférico — esa
// restricción es lo que hace el módulo proporcionado (art. 5.1.c RGPD) y lo
// que se le promete por escrito a la plantilla en la circular del art. 87
// LOPDGDD. Si alguien necesita "más precisión", la respuesta es no.
//
// El JS solo avisa de transiciones; la acumulación y la persistencia viven en
// el componente Blazor, que es quien sabe de qué conversación se trata.

const medidores = new Map();

export function iniciar(clave, referenciaDotNet, segundosInactividad) {
    detener(clave);

    const estado = {
        referenciaDotNet,
        segundosInactividad,
        temporizadorInactividad: null,
        pausadoPorFoco: false
    };

    const notificar = (metodo, argumento) => {
        try {
            estado.referenciaDotNet.invokeMethodAsync(metodo, argumento);
        } catch {
            // El circuito ya no existe (pestaña cerrándose): no hay nada que hacer
            // ni nadie a quien avisar.
        }
    };

    const pausar = (motivo) => {
        if (estado.pausadoPorFoco) return;
        estado.pausadoPorFoco = true;
        cancelarTemporizador(estado);
        notificar('EnPausaPorEntorno', motivo);
    };

    const reanudar = () => {
        if (!estado.pausadoPorFoco) return;
        estado.pausadoPorFoco = false;
        notificar('ReanudadoPorEntorno', null);
        armarTemporizador(estado, pausar);
    };

    estado.alCambiarVisibilidad = () => (document.hidden ? pausar('oculta') : reanudar());
    estado.alPerderFoco = () => pausar('sinFoco');
    estado.alGanarFoco = () => reanudar();

    document.addEventListener('visibilitychange', estado.alCambiarVisibilidad);
    window.addEventListener('blur', estado.alPerderFoco);
    window.addEventListener('focus', estado.alGanarFoco);

    medidores.set(clave, estado);

    // Si el hilo se abre desde una pestaña que ya está en segundo plano (p. ej.
    // restaurando una sesión), nace en pausa en vez de contar tiempo que nadie
    // está dedicando.
    if (document.hidden) pausar('oculta');
}

export function detener(clave) {
    const estado = medidores.get(clave);
    if (!estado) return;

    cancelarTemporizador(estado);
    document.removeEventListener('visibilitychange', estado.alCambiarVisibilidad);
    window.removeEventListener('blur', estado.alPerderFoco);
    window.removeEventListener('focus', estado.alGanarFoco);
    medidores.delete(clave);
}

// Reinicia la cuenta atrás de inactividad. Lo llama el componente Blazor cuando
// el Gestor hace algo real sobre el hilo — no lo dispara ningún listener de
// teclado o ratón.
export function marcarInteraccion(clave) {
    const estado = medidores.get(clave);
    if (!estado || estado.pausadoPorFoco) return;

    armarTemporizador(estado, (motivo) => {
        estado.pausadoPorFoco = true;
        estado.referenciaDotNet.invokeMethodAsync('EnPausaPorEntorno', motivo);
    });
}

function armarTemporizador(estado, pausar) {
    cancelarTemporizador(estado);
    if (!estado.segundosInactividad || estado.segundosInactividad <= 0) return;

    estado.temporizadorInactividad = window.setTimeout(
        () => pausar('inactividad'), estado.segundosInactividad * 1000);
}

function cancelarTemporizador(estado) {
    if (estado.temporizadorInactividad === null) return;
    window.clearTimeout(estado.temporizadorInactividad);
    estado.temporizadorInactividad = null;
}
