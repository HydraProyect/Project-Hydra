// Atajos de teclado de la pantalla de pasos de una Gestión automática (especificación «Gestión de varios pasos y
// Mi trabajo», § 4.5; Gestion Pasos TALVEG.dc.html): → o «s» abre el siguiente paso por hacer, ← el anterior, 1 elige
// «Subido» y 2 «Validado». Mismo motivo que atajos-lista.js: Blazor no captura keydown a nivel de document sin interop,
// porque el foco puede estar en cualquier sitio (o en ninguno). Lo que hace cada tecla lo decide la página en C#.
//
// Debe coincidir con AtajosPasosGestion.Teclas (AtajosPasosGestionSincronizadoConJsTests).
import { hayDialogoModalAbierto } from './atajos-contexto.js';

const TECLAS = ['ArrowRight', 'ArrowLeft', 's', '1', '2'];

// Donde se escribe o se elige de una lista, la tecla es del campo: una cifra en la fecha «Válido hasta», una «s» en el
// motivo del rechazo, una flecha en el select de la causa. Una casilla o un botón no consumen texto ni flechas: con el
// foco en «No lo sé» o en una opción de «¿Cómo ha quedado?» (la maqueta tampoco los excluye) los atajos siguen
// funcionando. Misma lista que atajos-lista.js, que la aprendió de un fallo real en CI.
const TIPOS_INPUT_NO_TEXTO = new Set([
    'checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color', 'file', 'image'
]);

// Superficies con teclado propio que no son de la pantalla: un menú o una lista de opciones, o un panel o diálogo no
// modal. Con el foco dentro, ninguna tecla es de la pantalla.
const SELECTOR_SUPERFICIE_AJENA = '[role="menu"], [role="listbox"], [role="dialog"], [role="alertdialog"], [role="complementary"]';

// Un menú abierto cuyo foco se quedó en el disparador (el «⋯» de la cabecera: sus ítems son enlaces y el foco no entra
// en ellos), como en atajos-lista.js y atajos-ficha.js.
const SELECTOR_MENU_ABIERTO = '[aria-haspopup="menu"][aria-expanded="true"]';

function esCampoEditable(activo) {
    if (activo.isContentEditable) return true;
    if (activo.tagName === 'TEXTAREA' || activo.tagName === 'SELECT') return true;
    return activo.tagName === 'INPUT' && !TIPOS_INPUT_NO_TEXTO.has(activo.type);
}

export function registrarAtajosPasosGestion(dotNetRef) {
    const manejador = (evento) => {
        // Cualquier otra tecla (Tab incluido) sigue su curso sin que este módulo la toque.
        if (!TECLAS.includes(evento.key)) return;
        // Ningún modificador: Ctrl/Cmd/Alt son de los atajos del navegador y del sistema, y Mayúsculas con una flecha
        // amplía una selección de texto. Una tecla mantenida tampoco recorre la Gestión a ráfagas.
        if (evento.ctrlKey || evento.metaKey || evento.altKey || evento.shiftKey) return;
        if (evento.defaultPrevented || evento.isComposing || evento.repeat) return;
        if (hayDialogoModalAbierto()) return;
        if (document.querySelector(SELECTOR_MENU_ABIERTO)) return;

        const activo = document.activeElement;
        if (activo && activo !== document.body) {
            if (esCampoEditable(activo)) return;
            if (activo.closest?.(SELECTOR_SUPERFICIE_AJENA)) return;
        }

        // No se toca el Tab ni el foco: la tecla solo cambia el paso abierto o la opción elegida.
        evento.preventDefault();
        dotNetRef.invokeMethodAsync('RecibirAtajo', evento.key);
    };

    document.addEventListener('keydown', manejador);

    return {
        dispose: () => document.removeEventListener('keydown', manejador)
    };
}
