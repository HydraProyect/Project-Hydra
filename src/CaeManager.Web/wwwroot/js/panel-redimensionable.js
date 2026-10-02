// Divisor arrastrable de paneles (D-26: lista de hilos de Comunicaciones).
//
// Delegación de eventos sobre el documento: el divisor es un elemento
// `[data-redimensionable]` que Blazor puede destruir y recrear en cada render,
// así que ningún listener cuelga de él. El ancho vive en la variable CSS
// --bandeja-lista-ancho de <html>, de modo que sobrevive a esos
// renders y a la navegación mejorada. Teclado: flechas izquierda/derecha
// (Mayús = paso grande), Inicio/Fin para los extremos. Sin interop con .NET:
// el ancho es solo de presentación y se recuerda en este navegador.
(function () {
    const PASO = 16;
    const PASO_GRANDE = 64;
    const VARIABLE = '--bandeja-lista-ancho';
    const claveAlmacen = 'redimensionable:' + VARIABLE;

    const limites = (divisor) => ({
        min: Number(divisor.dataset.min) || 240,
        max: Number(divisor.dataset.max) || 640
    });

    const anchoActual = (divisor) => {
        const panel = divisor.parentElement;
        return panel ? Math.round(panel.getBoundingClientRect().width) : 320;
    };

    const aplicar = (divisor, valor) => {
        const { min, max } = limites(divisor);
        const ancho = Math.min(max, Math.max(min, Math.round(valor)));
        document.documentElement.style.setProperty('--bandeja-lista-ancho', ancho + 'px');
        divisor.setAttribute('aria-valuenow', String(ancho));
        try { window.localStorage.setItem(claveAlmacen, String(ancho)); } catch { /* sin almacenamiento: solo no se recuerda */ }
        return ancho;
    };

    // Restaura el ancho recordado antes del primer render de la página.
    const restaurar = () => {
        try {
            const guardado = Number(window.localStorage.getItem(claveAlmacen));
            if (guardado > 0) {
                document.documentElement.style.setProperty('--bandeja-lista-ancho', guardado + 'px');
            }
        } catch { /* sin almacenamiento */ }
    };
    restaurar();

    let arrastre = null;

    document.addEventListener('pointerdown', (e) => {
        const divisor = e.target instanceof Element ? e.target.closest('[data-redimensionable]') : null;
        if (!divisor || e.button !== 0) return;
        arrastre = { divisor, inicioX: e.clientX, inicioAncho: anchoActual(divisor) };
        divisor.setPointerCapture?.(e.pointerId);
        divisor.classList.add('divisor-arrastrando');
        document.body.style.userSelect = 'none';
        e.preventDefault();
    });

    document.addEventListener('pointermove', (e) => {
        if (!arrastre) return;
        aplicar(arrastre.divisor, arrastre.inicioAncho + (e.clientX - arrastre.inicioX));
    });

    const soltar = () => {
        if (!arrastre) return;
        arrastre.divisor.classList.remove('divisor-arrastrando');
        document.body.style.userSelect = '';
        arrastre = null;
    };
    document.addEventListener('pointerup', soltar);
    document.addEventListener('pointercancel', soltar);

    document.addEventListener('keydown', (e) => {
        const divisor = e.target instanceof Element ? e.target.closest('[data-redimensionable]') : null;
        if (!divisor) return;
        const { min, max } = limites(divisor);
        const actual = anchoActual(divisor);
        const paso = e.shiftKey ? PASO_GRANDE : PASO;
        let nuevo = null;
        if (e.key === 'ArrowLeft') nuevo = actual - paso;
        else if (e.key === 'ArrowRight') nuevo = actual + paso;
        else if (e.key === 'Home') nuevo = min;
        else if (e.key === 'End') nuevo = max;
        if (nuevo === null) return;
        e.preventDefault();
        aplicar(divisor, nuevo);
    });
})();
