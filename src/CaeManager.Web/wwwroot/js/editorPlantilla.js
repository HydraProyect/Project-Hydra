// Editor visual de plantilla (ADR-010 § 2.3) — arrastrar/redimensionar cajas
// sobre el fondo rasterizado de cada página. Mismo criterio de escala que
// ConversorArchivosPdf.ConvertirImagenAPdf (72 puntos/pulgada), pero
// invertido y simplificado al máximo: el contenedor de cada página se
// dimensiona en la propia página Razor a los puntos PDF exactos en píxeles
// CSS (1 punto = 1 píxel), así que aquí no hace falta ningún cálculo de
// escala — un desplazamiento de N píxeles de ratón es literalmente un
// desplazamiento de N puntos PDF. Páginas grandes se ven con scroll nativo,
// no reescaladas — evita tener que mantener sincronizado un factor de zoom
// entre C# y JS sin un navegador real donde ajustarlo a mano.
//
// Puntero (no ratón/touch por separado), mismo criterio que firmaEnCampo.js.
// Delegación de eventos sobre el contenedor: las cajas se añaden/quitan
// según edita el gestor, así que atar un listener por caja obligaría a
// re-inicializar en cada cambio.

function idNumerico(elemento) {
    return parseInt(elemento.dataset.idLocal, 10);
}

export function iniciar(referencia, idContenedor) {
    const contenedor = document.getElementById(idContenedor);
    if (!contenedor) return;

    let arrastre = null; // { modo: 'mover'|'redimensionar', caja, inicioX, inicioY, left, top, width, height }

    contenedor.addEventListener('pointerdown', evento => {
        const handle = evento.target.closest('.editor-plantilla-handle');
        const caja = evento.target.closest('.editor-plantilla-caja');
        if (!caja) return;

        evento.preventDefault();
        caja.setPointerCapture(evento.pointerId);

        arrastre = {
            modo: handle ? 'redimensionar' : 'mover',
            caja,
            inicioX: evento.clientX,
            inicioY: evento.clientY,
            left: caja.offsetLeft,
            top: caja.offsetTop,
            width: caja.offsetWidth,
            height: caja.offsetHeight,
        };
    });

    contenedor.addEventListener('pointermove', evento => {
        if (!arrastre) return;

        const deltaX = evento.clientX - arrastre.inicioX;
        const deltaY = evento.clientY - arrastre.inicioY;
        const pagina = arrastre.caja.parentElement;
        const anchoPagina = pagina.clientWidth;
        const altoPagina = pagina.clientHeight;

        if (arrastre.modo === 'mover') {
            const nuevoLeft = Math.max(0, Math.min(arrastre.left + deltaX, anchoPagina - arrastre.width));
            const nuevoTop = Math.max(0, Math.min(arrastre.top + deltaY, altoPagina - arrastre.height));
            arrastre.caja.style.left = `${nuevoLeft}px`;
            arrastre.caja.style.top = `${nuevoTop}px`;
        } else {
            const anchoMinimo = 15;
            const altoMinimo = 12;
            const nuevoAncho = Math.max(anchoMinimo, Math.min(arrastre.width + deltaX, anchoPagina - arrastre.left));
            const nuevoAlto = Math.max(altoMinimo, Math.min(arrastre.height + deltaY, altoPagina - arrastre.top));
            arrastre.caja.style.width = `${nuevoAncho}px`;
            arrastre.caja.style.height = `${nuevoAlto}px`;
        }
    });

    const terminarArrastre = evento => {
        if (!arrastre) return;

        const caja = arrastre.caja;
        arrastre = null;

        referencia.invokeMethodAsync(
            'ActualizarPosicionAsync',
            idNumerico(caja),
            caja.offsetLeft,
            caja.offsetTop,
            caja.offsetWidth,
            caja.offsetHeight);
    };

    contenedor.addEventListener('pointerup', terminarArrastre);
    contenedor.addEventListener('pointercancel', terminarArrastre);
}
