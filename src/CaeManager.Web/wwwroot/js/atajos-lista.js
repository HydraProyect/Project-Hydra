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

export function registrarAtajosLista(dotNetRef) {
    const manejador = (evento) => {
        if (!TECLAS_ADMITIDAS.includes(evento.key)) return;

        const activo = document.activeElement;
        const enCampoEditable = activo && (
            activo.tagName === 'TEXTAREA' || activo.isContentEditable ||
            (activo.tagName === 'INPUT' && !TIPOS_INPUT_NO_TEXTO.has(activo.type))
        );
        if (enCampoEditable) return;

        evento.preventDefault();
        dotNetRef.invokeMethodAsync('RecibirAtajo', evento.key);
    };

    document.addEventListener('keydown', manejador);

    return {
        dispose: () => document.removeEventListener('keydown', manejador)
    };
}
