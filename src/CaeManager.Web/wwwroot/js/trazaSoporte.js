// Captura de interacciones durante una sesión de soporte (ver
// TrazaSoporte.razor.cs). Solo se carga si la sesión es de soporte: para el
// uso normal de la aplicación este módulo ni se importa.
//
// Los eventos se acumulan y se envían por lotes en vez de uno a uno: un viaje
// al servidor por clic convertiría el registro en un problema de rendimiento
// mayor que el que resuelve.

const INTERVALO_ENVIO_MS = 2000;
const MAXIMO_POR_LOTE = 25;

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

/**
 * Engancha la captura de un componente TrazaSoporte y devuelve su manejador,
 * que .NET guarda como referencia JS y usa para llamar a detener().
 *
 * El estado (lote pendiente, temporizador y listeners) es de cada captura y
 * no del módulo: el módulo se importa una sola vez por página y lo comparten
 * todos los componentes, así que un estado global haría que un segundo
 * componente pisara al primero. Tampoco se compara la referencia .NET por
 * identidad: el interop crea un objeto JS nuevo en cada llamada para la misma
 * DotNetObjectReference, así que esa comparación nunca acierta.
 *
 * Que no haya dos listeners para el mismo componente lo garantiza .NET, que
 * llama a iniciar() una sola vez por montaje.
 */
export function iniciar(referencia) {
    let pendientes = [];
    let temporizador = null;
    let activa = true;
    const enVuelo = new Set();

    function enviar() {
        if (temporizador) {
            clearTimeout(temporizador);
            temporizador = null;
        }
        if (pendientes.length === 0) return;

        const lote = pendientes;
        pendientes = [];

        const envio = referencia.invokeMethodAsync('RegistrarInteraccionesAsync', lote).catch(() => {
            // Circuito caído o navegación en curso. No se reintenta: perder una
            // anotación de interacción no puede romper la sesión de quien está
            // atendiendo una incidencia.
        });
        enVuelo.add(envio);
        envio.then(() => enVuelo.delete(envio));
    }

    function programarEnvio() {
        if (pendientes.length >= MAXIMO_POR_LOTE) {
            enviar();
            return;
        }

        if (!temporizador) {
            temporizador = setTimeout(enviar, INTERVALO_ENVIO_MS);
        }
    }

    function alPulsar(evento) {
        const descripcion = describir(evento.target);
        if (!descripcion) return;

        pendientes.push(descripcion);
        programarEnvio();
    }

    // Lo acumulado se envía antes de abandonar la página; si no, el último
    // tramo de la visita se perdería justo al cerrar. No desengancha: si la
    // página vuelve de la caché de historial, la captura sigue viva.
    function alAbandonar() { enviar(); }

    // Delegado en document y en fase de captura: así se registra el clic
    // aunque el manejador del propio elemento detenga la propagación.
    document.addEventListener('click', alPulsar, true);
    window.addEventListener('pagehide', alAbandonar);

    return {
        /**
         * Al desmontar el componente. El lote pendiente se toma y se envía
         * antes de quitar los listeners, en el mismo turno síncrono, así que
         * ningún clic queda entre medias sin anotar; cancelar el temporizador
         * no pierde nada porque ese mismo envío se adelanta ahora. Resuelve
         * cuando han terminado ese envío y los que ya iban en vuelo (del
         * temporizador o de un lote lleno), para que .NET no libere la
         * DotNetObjectReference con un lote todavía sin guardar.
         */
        detener() {
            if (!activa) return Promise.resolve();
            activa = false;

            enviar();
            document.removeEventListener('click', alPulsar, true);
            window.removeEventListener('pagehide', alAbandonar);
            return Promise.all([...enVuelo]).then(() => undefined);
        }
    };
}
