// Apoyo de CampoSelectAvanzado.razor: solo lo que Blazor no puede hacer desde C#.
//  1. preventDefault condicional de las teclas de navegación del combobox (Blazor decide
//     preventDefault al pintar, no por tecla: sin esto las flechas desplazan la página, Intro
//     envía el formulario y Espacio vuelve a pulsar el disparador).
//  2. Anclar el panel a la capa superior del navegador (Popover API) para que un Drawer o un
//     Modal con overflow no lo recorte, y voltearlo hacia arriba si no cabe debajo.
// La lógica (qué opción está activa, qué se elige) vive en C#. Si este módulo no carga, el panel
// cae a posición absoluta bajo el disparador (CSS) y sigue funcionando.
const MARGEN = 8;
const HUECO = 4;
const ANCHO_MINIMO_PANEL = 240;

export function crear(raiz) {
    let suprimirSoltarEspacio = false;
    let anclaje = null;

    const abierto = () => raiz.dataset.abierto === 'true';
    const esDisparador = el => el.closest?.('.csa-disparador') !== null && el.closest?.('.csa-disparador') !== undefined;
    const esBuscador = el => el.classList?.contains('csa-buscador') === true;

    function alPulsar(e) {
        if (e.ctrlKey || e.metaKey || e.altKey) return;
        const destino = e.target;
        if (!esDisparador(destino) && !esBuscador(destino)) return;

        if (!abierto()) {
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') e.preventDefault();
            return;
        }

        switch (e.key) {
            case 'ArrowDown':
            case 'ArrowUp':
            case 'Enter':
                e.preventDefault();
                break;
            case ' ':
                if (!esBuscador(destino)) {
                    e.preventDefault();
                    suprimirSoltarEspacio = true;
                }
                break;
            case 'Home':
            case 'End':
                if (!esBuscador(destino) || destino.value === '') e.preventDefault();
                break;
        }
    }

    // Firefox pulsa un botón al SOLTAR Espacio: sin esto, elegir con Espacio cerraría la lista y la
    // volvería a abrir con el keyup.
    function alSoltar(e) {
        if (e.key === ' ' && suprimirSoltarEspacio) {
            suprimirSoltarEspacio = false;
            e.preventDefault();
        }
    }

    raiz.addEventListener('keydown', alPulsar);
    raiz.addEventListener('keyup', alSoltar);

    function colocar(panel, disparador) {
        const caja = disparador.getBoundingClientRect();
        const altoVentana = window.innerHeight;
        const anchoVentana = window.innerWidth;

        const ancho = Math.min(Math.max(caja.width, ANCHO_MINIMO_PANEL), anchoVentana - 2 * MARGEN);
        const izquierda = Math.min(Math.max(caja.left, MARGEN), anchoVentana - ancho - MARGEN);
        const abajo = altoVentana - caja.bottom - MARGEN - HUECO;
        const arriba = caja.top - MARGEN - HUECO;

        // Alto natural = lo que ocupa el panel fuera de la lista + lo que mediría la lista entera.
        // Sin tocar max-height: retirarlo un instante reiniciaría el scroll de la lista.
        const lista = panel.querySelector('[role="listbox"]');
        const natural = lista ? panel.offsetHeight - lista.clientHeight + lista.scrollHeight : panel.scrollHeight;
        const voltear = natural > abajo && arriba > abajo;
        const disponible = Math.max(voltear ? arriba : abajo, 96);

        panel.style.position = 'fixed';
        panel.style.margin = '0';
        panel.style.right = 'auto';
        panel.style.left = izquierda + 'px';
        panel.style.width = ancho + 'px';
        panel.style.maxHeight = disponible + 'px';
        if (voltear) {
            panel.style.top = 'auto';
            panel.style.bottom = (altoVentana - caja.top + HUECO) + 'px';
        } else {
            panel.style.bottom = 'auto';
            panel.style.top = (caja.bottom + HUECO) + 'px';
        }
        panel.dataset.lado = voltear ? 'arriba' : 'abajo';
    }

    return {
        anclar(panel, disparador) {
            this.soltar();
            if (typeof panel.showPopover === 'function') {
                panel.setAttribute('popover', 'manual');
                try { panel.showPopover(); } catch { /* ya visible */ }
            }
            colocar(panel, disparador);

            let cuadro = 0;
            const recolocar = (e) => {
                if (e && e.type === 'scroll' && panel.contains(e.target)) return;
                cancelAnimationFrame(cuadro);
                cuadro = requestAnimationFrame(() => colocar(panel, disparador));
            };
            window.addEventListener('resize', recolocar);
            window.addEventListener('scroll', recolocar, true);
            anclaje = () => {
                cancelAnimationFrame(cuadro);
                window.removeEventListener('resize', recolocar);
                window.removeEventListener('scroll', recolocar, true);
            };
        },

        soltar() {
            if (anclaje) {
                anclaje();
                anclaje = null;
            }
        },

        desplazarA(id) {
            document.getElementById(id)?.scrollIntoView({ block: 'nearest' });
        },

        dispose() {
            this.soltar();
            raiz.removeEventListener('keydown', alPulsar);
            raiz.removeEventListener('keyup', alSoltar);
        },
    };
}
