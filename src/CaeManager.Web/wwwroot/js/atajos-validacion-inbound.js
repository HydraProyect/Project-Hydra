// Atajos de teclado de la pantalla «Validación» de Inbound (tarjeta 8): Espacio/V validar, R rechazar,
// I marcar incompleto, J/K siguiente y anterior, ? ver los atajos, Esc cancelar y E ir a la fecha de emisión.
// Mismo motivo que atajos-lista.js: Blazor no captura keydown de document sin interop. La tecla se ignora si el
// foco está en un campo donde se escribe (salvo Esc) y Espacio/Intro se dejan al botón que tenga el foco.
const TECLAS = new Set([' ', 'v', 'r', 'i', 'e', 'j', 'k', '?', 'Escape', 'Enter']);

const TIPOS_INPUT_NO_TEXTO = new Set([
    'checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color', 'file', 'image'
]);

const SELECTOR_INTERACTIVO = 'button, a[href], select, summary, input, ' +
    '[role="button"], [role="link"], [role="menuitem"], [role="tab"], [role="checkbox"], [role="option"]';

const SELECTOR_CAMPO_EMISION = '[data-campo-emision] input';

export function registrarAtajosValidacion(dotNetRef) {
    const manejador = async (evento) => {
        if (evento.ctrlKey || evento.metaKey || evento.altKey || evento.defaultPrevented || evento.isComposing) return;
        const tecla = evento.key.length === 1 ? evento.key.toLowerCase() : evento.key;
        if (!TECLAS.has(tecla)) return;

        const activo = document.activeElement;
        const enCampoEditable = activo && (
            activo.tagName === 'TEXTAREA' || activo.tagName === 'SELECT' || activo.isContentEditable ||
            (activo.tagName === 'INPUT' && !TIPOS_INPUT_NO_TEXTO.has(activo.type))
        );
        if (enCampoEditable && tecla !== 'Escape') return;

        const enElementoInteractivo = activo && activo !== document.body && activo.matches?.(SELECTOR_INTERACTIVO);
        if ((tecla === ' ' || tecla === 'Enter') && enElementoInteractivo) return;

        if (tecla === 'e') {
            const campo = document.querySelector(SELECTOR_CAMPO_EMISION);
            if (!campo || campo.disabled) return;
            evento.preventDefault();
            campo.focus();
            campo.select?.();
            return;
        }

        evento.preventDefault();
        await dotNetRef.invokeMethodAsync('RecibirAtajo', tecla === ' ' ? 'Espacio' : tecla);
    };

    document.addEventListener('keydown', manejador);

    return {
        dispose: () => document.removeEventListener('keydown', manejador)
    };
}
