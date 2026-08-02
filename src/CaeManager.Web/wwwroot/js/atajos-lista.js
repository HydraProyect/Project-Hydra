// Atajos de teclado de lista (j/k/x/Enter, P3-31) — mismo motivo que
// buscador-global.js: Blazor no puede capturar keydown a nivel de document
// sin interop porque el foco puede estar en cualquier elemento. Se ignora
// el evento si el foco está en un campo de texto/contenteditable, para no
// interceptar "j"/"k" mientras el usuario escribe en un filtro.
const TECLAS_ADMITIDAS = ['j', 'k', 'x', 'Enter'];

export function registrarAtajosLista(dotNetRef) {
    const manejador = (evento) => {
        if (!TECLAS_ADMITIDAS.includes(evento.key)) return;

        const activo = document.activeElement;
        const enCampoEditable = activo && (
            activo.tagName === 'INPUT' || activo.tagName === 'TEXTAREA' || activo.isContentEditable
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
