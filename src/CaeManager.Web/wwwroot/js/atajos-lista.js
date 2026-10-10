// Atajos de teclado de lista (j/k/x/Enter, P3-31) — mismo motivo que
// buscador-global.js: Blazor no puede capturar keydown a nivel de document
// sin interop porque el foco puede estar en cualquier elemento. Se ignora
// el evento si el foco está en un campo de texto/contenteditable, para no
// interceptar "j"/"k" mientras el usuario escribe en un filtro.
import { hayDialogoModalAbierto, atajosDeLista } from './atajos-contexto.js';

// Debe coincidir con CatalogoAtajos.Lista (CatalogoAtajosSincronizadoConJsTests).
const TECLAS_ADMITIDAS = ['j', 'k', 'x', 'Enter', 'f', 'e'];

// «e» (editar la fila enfocada) es también un destino de «g + letra» (g e → /empresas, en
// atajos-globales.js). Los dos módulos escuchan en document y el orden en que se registran
// no está garantizado, así que este recuerda él mismo si la tecla anterior fue una «g»
// suelta dentro de la misma ventana: entonces la «e» es del atajo global y aquí no se toca.
// Debe coincidir con VENTANA_PREFIJO_MS de atajos-globales.js.
const VENTANA_PREFIJO_GLOBAL_MS = 900;

// Buscador «Filtrar esta pantalla» del listado (BarraFiltros con pastillas). "f" lo
// enfoca aquí mismo, sin pasar por C#: no hay estado de la página que cambiar. Si la
// página no lo tiene, la tecla sigue su curso normal.
const SELECTOR_FILTRO_PANTALLA = '[data-filtro-pantalla]';

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
    // El diálogo puede haberse abierto mientras terminaba el callback de C#.
    if (hayDialogoModalAbierto()) return;
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

// Un menú (MenuAcciones) abierto: el foco está dentro de su panel role="menu", o su
// disparador anuncia aria-expanded="true" (abierto sin ítems habilitados, el foco se
// queda en el disparador).
function hayMenuAbierto(activo) {
    if (activo?.closest?.('[role="menu"]')) return true;
    return document.querySelector('[aria-haspopup="menu"][aria-expanded="true"]') !== null;
}

// Un buscador oculto (pestaña inactiva, display:none, visibility:hidden) o deshabilitado
// no puede recibir la «f»: el foco se iría a un sitio que el usuario no ve.
function esVisibleYUsable(campo) {
    if (campo.disabled || campo.getClientRects().length === 0) return false;
    return getComputedStyle(campo).visibility !== 'hidden';
}

// Fila sin menú «⋯» en las listas con QuickGrid: QuickGrid no ofrece clic de fila, así que la
// página marca el <tr> con «fila-pulsable» (RowClass) y el nombre de la fila es un botón
// «nombre-abre-vista-rapida» con el manejador de Blazor. Un clic en cualquier otro punto de
// la fila pulsa ese botón. Los controles de dentro (casilla, identificador copiable, icono
// 360, pastillas con acción) conservan su clic: aquí no se tocan. Tampoco el clic que
// termina una selección de texto, ni el de una fila cuyo marcado ya trae su propio @onclick
// (Empresas: su fila no es un <tr>). El panel de una ventana de contexto cuenta entero como
// control: su título, su pie y los huecos entre sus elementos no son botones, pero quien pulsa
// ahí está usando la ventana, no la fila. El «stopPropagation» que la ventana interactiva
// declara en Blazor no sirve aquí: corta el reparto de Blazor, no el burbujeo del DOM.
const SELECTOR_FILA_PULSABLE = 'tr.fila-pulsable';
const SELECTOR_ABRE_VISTA_RAPIDA = '.nombre-abre-vista-rapida';
const SELECTOR_CONTROL_DE_FILA = SELECTOR_INTERACTIVO + ', label, textarea, .ventana-contexto-panel';

function pulsarFila(evento) {
    if (evento.defaultPrevented || evento.button !== 0) return;
    if (evento.ctrlKey || evento.metaKey || evento.altKey || evento.shiftKey) return;
    const origen = evento.target;
    const fila = origen?.closest?.(SELECTOR_FILA_PULSABLE);
    if (!fila) return;
    if (origen.closest(SELECTOR_CONTROL_DE_FILA)) return;
    if (window.getSelection?.()?.toString()) return;
    fila.querySelector(SELECTOR_ABRE_VISTA_RAPIDA)?.click();
}

export function registrarAtajosLista(dotNetRef) {
    let ultimaGSuelta = 0;

    const manejador = async (evento) => {
        // Una tecla con modificador (o el modificador solo: Ctrl, AltGr) no es de nadie y no
        // toca el prefijo: el módulo global tampoco lo limpia ahí, y «g, Ctrl, e» sigue
        // siendo «ir a Empresas».
        if (evento.ctrlKey || evento.metaKey || evento.altKey) return;

        // Cualquier otra tecla consume el prefijo; solo lo vuelve a armar una «g» que el módulo
        // global también trataría como prefijo (más abajo, pasadas sus mismas guardas).
        const trasPrefijoGlobal = Date.now() - ultimaGSuelta < VENTANA_PREFIJO_GLOBAL_MS;
        ultimaGSuelta = 0;

        const esPrefijoGlobal = evento.key === 'g';
        if (!esPrefijoGlobal && !TECLAS_ADMITIDAS.includes(evento.key)) return;
        if (evento.defaultPrevented || evento.isComposing) return;
        if (hayDialogoModalAbierto()) return;

        const activo = document.activeElement;
        const enCampoEditable = activo && (
            activo.tagName === 'TEXTAREA' || activo.tagName === 'SELECT' || activo.isContentEditable ||
            (activo.tagName === 'INPUT' && !TIPOS_INPUT_NO_TEXTO.has(activo.type))
        );
        if (enCampoEditable) return;

        // Una «g» tecleada dentro de un campo o con un diálogo abierto no
        // arma nada (el módulo global tampoco): la «e» que venga después es la de editar.
        // Con el prefijo ya armado, la segunda «g» lo consume, igual que allí.
        if (esPrefijoGlobal) {
            // Un botón o una casilla con el foco son INPUT/BUTTON: el global no arma el
            // prefijo sobre un INPUT de ningún tipo.
            if (!trasPrefijoGlobal && activo?.tagName !== 'INPUT') ultimaGSuelta = Date.now();
            return;
        }
        if (evento.key === 'e' && trasPrefijoGlobal) return;

        const enElementoInteractivo = activo && activo !== document.body && activo.matches?.(SELECTOR_INTERACTIVO);
        if (evento.key === 'Enter' && enElementoInteractivo) return;

        if (evento.key === 'f') {
            // Con un menú abierto (una pastilla de filtro, el «⋯» de la cabecera o de una fila)
            // el teclado es del menú: «f» no le roba el foco.
            if (hayMenuAbierto(activo)) return;
            const filtro = document.querySelector(SELECTOR_FILTRO_PANTALLA);
            if (!filtro || !esVisibleYUsable(filtro)) return;
            // preventDefault: la «f» no debe acabar escrita dentro del campo recién enfocado.
            evento.preventDefault();
            filtro.focus();
            filtro.select?.();
            return;
        }

        evento.preventDefault();
        await dotNetRef.invokeMethodAsync('RecibirAtajo', evento.key);

        if (evento.key === 'j' || evento.key === 'k') {
            requestAnimationFrame(() => enfocarFilaActiva());
        }
    };

    document.addEventListener('keydown', manejador);
    document.addEventListener('click', pulsarFila);
    atajosDeLista.activos++;
    let retirado = false;

    return {
        dispose: () => {
            // Un dispose repetido no descuenta dos veces.
            if (!retirado) {
                retirado = true;
                atajosDeLista.activos--;
            }
            document.removeEventListener('keydown', manejador);
            document.removeEventListener('click', pulsarFila);
        }
    };
}
