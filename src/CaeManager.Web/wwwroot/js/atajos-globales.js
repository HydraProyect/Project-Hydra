// Atajos globales tipo Linear (Fase D): "g" + letra navega, "n" crea aquí,
// "?" abre el chuleta. Blazor no puede capturar keydown a nivel de document
// sin interop porque el foco puede estar en cualquier elemento — mismo
// motivo que atajos-lista.js y buscador-global.js. Registrado una sola vez
// en MainLayout, no por página.
// Debe coincidir exactamente con las claves de CatalogoAtajos.DestinosNavegacion
// (C# no puede leer este array ni viceversa — CatalogoAtajosSincronizadoConJsTests
// vigila el emparejamiento leyendo este fichero como texto).
const TECLAS_DESTINO = ['c', 'e', 't', 'd', 'a', 'b', 'p', 'i'];
const VENTANA_PREFIJO_MS = 900;

export function registrarAtajosGlobales(dotNetRef) {
    let prefijoActivo = false;
    let temporizador = null;

    const limpiarPrefijo = () => {
        prefijoActivo = false;
        if (temporizador) {
            clearTimeout(temporizador);
            temporizador = null;
        }
    };

    const manejador = (evento) => {
        const activo = document.activeElement;
        const enCampoEditable = activo && (
            activo.tagName === 'INPUT' || activo.tagName === 'TEXTAREA' || activo.isContentEditable
        );
        if (enCampoEditable) {
            limpiarPrefijo();
            return;
        }

        // Ctrl/Cmd+K, atajos de lista (j/k/x/Enter) y cualquier combinación
        // con modificador quedan fuera — solo teclas sueltas.
        if (evento.ctrlKey || evento.metaKey || evento.altKey) return;

        const tecla = evento.key;

        if (prefijoActivo) {
            limpiarPrefijo();
            if (TECLAS_DESTINO.includes(tecla)) {
                evento.preventDefault();
                dotNetRef.invokeMethodAsync('IrA', tecla);
            }
            return;
        }

        if (tecla === 'g') {
            prefijoActivo = true;
            temporizador = setTimeout(limpiarPrefijo, VENTANA_PREFIJO_MS);
            return;
        }

        if (tecla === 'n') {
            evento.preventDefault();
            dotNetRef.invokeMethodAsync('CrearAqui');
            return;
        }

        if (tecla === '?') {
            evento.preventDefault();
            dotNetRef.invokeMethodAsync('AlternarAyuda');
        }
    };

    document.addEventListener('keydown', manejador);

    return {
        dispose: () => {
            limpiarPrefijo();
            document.removeEventListener('keydown', manejador);
        }
    };
}
