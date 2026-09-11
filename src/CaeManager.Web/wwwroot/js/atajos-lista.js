// Atajos de teclado de lista (j/k/x/Enter, P3-31) — mismo motivo que
// buscador-global.js: Blazor no puede capturar keydown a nivel de document
// sin interop porque el foco puede estar en cualquier elemento. Se ignora
// el evento si el foco está en un campo de texto/contenteditable, para no
// interceptar "j"/"k" mientras el usuario escribe en un filtro.
const TECLAS_ADMITIDAS = ['j', 'k', 'x', 'Enter'];

// <input> cuyo type NO consume texto libre (checkbox, radio, los distintos
// botones...) — un Tab que aterriza en "Solo críticos" o en un checkbox de
// fila no debe bloquear j/k/x/Enter igual que lo haría un campo de
// búsqueda. Bug real encontrado en CI (P3-31/Bandeja E2E): tagName === 'INPUT'
// por sí solo también es cierto para esos controles, así que la primera "j"
// tras un Tab desde el buscador caía siempre en el checkbox siguiente y se
// descartaba en silencio, sin ningún error visible.
const TIPOS_INPUT_NO_TEXTO = new Set([
    'checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color', 'file', 'image'
]);

// Elementos con acción nativa propia al pulsar Enter (activar un botón,
// seguir un enlace, abrir un <select>/<summary>...). j/k/x se siguen
// interceptando aunque el foco esté aquí — no tienen acción nativa que
// perder y es el comportamiento ya buscado por TIPOS_INPUT_NO_TEXTO arriba
// (un Tab que aterriza en un botón no debe romper la navegación por
// teclado de la lista) — pero Enter sí tiene una y before-fix se cancelaba
// siempre con preventDefault(), así que un Tab a "+ Nueva empresa",
// "Reintentar" o el disparador de MenuAcciones y luego Enter activaba el
// atajo de lista (abría la fila enfocada) en vez del control con el foco.
const SELECTOR_INTERACTIVO = 'button, a[href], select, summary, input, ' +
    '[role="button"], [role="link"], [role="menuitem"], [role="tab"], [role="checkbox"], [role="option"]';

// Selector de la fila que cada página marca como enfocada por j/k — mismo
// nombre en (casi) todas las páginas Gen 2 de lista; Bandeja usa el suyo
// propio porque PanelResolverItem es una tarjeta, no una fila de tabla.
const SELECTOR_FILA_ENFOCADA = '.fila-enfocada, .panel-resolver-item-enfocado';

// Da a la fila enfocada por j/k foco de DOM real (antes solo llevaba la
// clase CSS: ni foco ni estado ARIA, invisible para lectores de pantalla).
// tabIndex = -1 la hace focable por script sin meterla en el orden de Tab
// normal — la propia fila no tiene por qué ser alcanzable con Tab, la
// navegación entre filas ya la hacen j/k.
function enfocarFilaActiva(intentosRestantes = 10) {
    const fila = document.querySelector(SELECTOR_FILA_ENFOCADA);
    if (fila) {
        if (!fila.hasAttribute('tabindex')) fila.tabIndex = -1;
        fila.focus({ preventScroll: true });
        return;
    }
    // El parche del DOM tras RecibirAtajo (interop -> C# -> StateHasChanged)
    // puede no haber llegado todavía cuando invokeMethodAsync resuelve —
    // reintenta unos frames antes de rendirse.
    if (intentosRestantes > 0) {
        requestAnimationFrame(() => enfocarFilaActiva(intentosRestantes - 1));
    }
}

export function registrarAtajosLista(dotNetRef) {
    const manejador = async (evento) => {
        if (!TECLAS_ADMITIDAS.includes(evento.key)) return;

        const activo = document.activeElement;
        const enCampoEditable = activo && (
            activo.tagName === 'TEXTAREA' || activo.isContentEditable ||
            (activo.tagName === 'INPUT' && !TIPOS_INPUT_NO_TEXTO.has(activo.type))
        );
        if (enCampoEditable) return;

        const enElementoInteractivo = activo && activo !== document.body && activo.matches?.(SELECTOR_INTERACTIVO);
        if (evento.key === 'Enter' && enElementoInteractivo) return;

        evento.preventDefault();
        await dotNetRef.invokeMethodAsync('RecibirAtajo', evento.key);

        if (evento.key === 'j' || evento.key === 'k') {
            requestAnimationFrame(() => enfocarFilaActiva());
        }
    };

    document.addEventListener('keydown', manejador);

    return {
        dispose: () => document.removeEventListener('keydown', manejador)
    };
}
