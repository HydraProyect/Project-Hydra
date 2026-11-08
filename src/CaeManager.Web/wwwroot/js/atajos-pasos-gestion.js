// Atajos de teclado de la pantalla de pasos de una Gestión automática (especificación «Gestión de varios pasos y
// Mi trabajo», § 4.5; Gestion Pasos TALVEG.dc.html): → o «s» abre el siguiente paso por hacer, ← el anterior, 1 elige
// «Subido» y 2 «Validado». Mismo motivo que atajos-lista.js: Blazor no captura keydown a nivel de document sin interop,
// porque el foco puede estar en cualquier sitio (o en ninguno). Lo que hace cada tecla lo decide la página en C#.
//
// Debe coincidir con AtajosPasosGestion.Teclas (AtajosPasosGestionSincronizadoConJsTests).
import { hayDialogoModalAbierto } from './atajos-contexto.js';

const TECLAS = ['ArrowRight', 'ArrowLeft', 's', '1', '2'];
const FLECHAS = ['ArrowRight', 'ArrowLeft'];

// Donde se escribe o se elige, la tecla es del campo: una cifra en la fecha «Válido hasta», una «s» en el motivo del
// rechazo, una flecha en el select de la causa o en la casilla «No lo sé».
const SELECTOR_CAMPO = 'input, textarea, select';

// Superficies con teclado propio que no son de la pantalla: un menú (el «⋯» de la cabecera), una lista de opciones o un
// panel o diálogo no modal. Con el foco dentro, ninguna tecla es de la pantalla.
const SELECTOR_SUPERFICIE_AJENA = '[role="menu"], [role="listbox"], [role="dialog"], [role="alertdialog"], [role="complementary"]';

// Controles cuyas flechas tienen significado propio (moverse entre radios o pestañas, mover un deslizador). Las cifras
// sí se atienden ahí: con el foco en una opción de «¿Cómo ha quedado?», «1» sigue eligiendo «Subido».
const SELECTOR_FLECHAS_PROPIAS = '[role="radiogroup"], [role="tablist"], [role="slider"], [role="spinbutton"]';

export function registrarAtajosPasosGestion(dotNetRef) {
    const manejador = (evento) => {
        if (!TECLAS.includes(evento.key)) return;
        // Ningún modificador: Ctrl/Cmd/Alt son de los atajos del navegador y del sistema, y Mayúsculas con una flecha
        // amplía una selección de texto. Una tecla mantenida tampoco recorre la Gestión a ráfagas.
        if (evento.ctrlKey || evento.metaKey || evento.altKey || evento.shiftKey) return;
        if (evento.defaultPrevented || evento.isComposing || evento.repeat) return;
        if (hayDialogoModalAbierto()) return;

        const activo = document.activeElement;
        if (activo && activo !== document.body) {
            if (activo.isContentEditable || activo.matches?.(SELECTOR_CAMPO)) return;
            if (activo.closest?.(SELECTOR_SUPERFICIE_AJENA)) return;
            if (FLECHAS.includes(evento.key) && activo.closest?.(SELECTOR_FLECHAS_PROPIAS)) return;
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
