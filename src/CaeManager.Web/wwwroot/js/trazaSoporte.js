// Captura de interacciones durante una sesión de soporte (ver
// TrazaSoporte.razor.cs). Solo se carga si la sesión es de soporte: para el
// uso normal de la aplicación este módulo ni se importa.
//
// Los eventos se acumulan y se envían por lotes en vez de uno a uno: un viaje
// al servidor por clic convertiría el registro en un problema de rendimiento
// mayor que el que resuelve.

const INTERVALO_ENVIO_MS = 2000;
const MAXIMO_POR_LOTE = 25;

let pendientes = [];
let temporizador = null;
let referenciaDotNet = null;

/**
 * Describe el elemento pulsado de forma legible para quien después lea la
 * traza. Se prefiere el texto visible porque es lo que reconoce una persona
 * ("Eliminar", "Guardar"); el selector técnico solo sirve de último recurso.
 *
 * Nunca se recoge el valor de un campo: la traza registra qué se hizo, no los
 * datos personales que se estaban viendo o escribiendo.
 */
function describir(elemento) {
    const interactivo = elemento.closest('button, a, [role="button"], summary, input[type="checkbox"], input[type="radio"]');
    if (!interactivo) return null;

    const etiqueta =
        interactivo.getAttribute('aria-label') ||
        (interactivo.innerText || '').trim().split('\n')[0] ||
        interactivo.getAttribute('title') ||
        interactivo.tagName.toLowerCase();

    const recortada = etiqueta.length > 80 ? etiqueta.slice(0, 80) : etiqueta;

    return `${recortada} — ${location.pathname}`;
}

async function enviar() {
    temporizador = null;
    if (pendientes.length === 0 || !referenciaDotNet) return;

    const lote = pendientes;
    pendientes = [];

    try {
        await referenciaDotNet.invokeMethodAsync('RegistrarInteraccionesAsync', lote);
    } catch {
        // Circuito caído o navegación en curso. No se reintenta: perder una
        // anotación de interacción no puede romper la sesión de quien está
        // atendiendo una incidencia.
    }
}

function programarEnvio() {
    if (pendientes.length >= MAXIMO_POR_LOTE) {
        if (temporizador) { clearTimeout(temporizador); }
        enviar();
        return;
    }

    if (!temporizador) {
        temporizador = setTimeout(enviar, INTERVALO_ENVIO_MS);
    }
}

export function iniciar(referencia) {
    referenciaDotNet = referencia;

    // Delegado en document y en fase de captura: así se registra el clic
    // aunque el manejador del propio elemento detenga la propagación.
    document.addEventListener('click', evento => {
        const descripcion = describir(evento.target);
        if (!descripcion) return;

        pendientes.push(descripcion);
        programarEnvio();
    }, true);

    // Lo acumulado se envía antes de abandonar la página; si no, el último
    // tramo de la visita se perdería justo al cerrar.
    window.addEventListener('pagehide', () => { enviar(); });
}
