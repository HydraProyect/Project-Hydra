// Destello de la fila de una tabla de datos que acaba de entrar o de cambiar
// por una acción del usuario (ficha 14 del acabado). Un solo observador
// delegado, sin marcado en línea por la CSP (ver microinteracciones.js).
//
// Blazor reutiliza las filas por posición: al ordenar, filtrar o paginar cambia
// el contenido de muchas a la vez, y la carga inicial inserta todas juntas.
// Eso NO es una fila que entra por una acción del usuario, y por eso solo se
// destella cuando se cumplen las cuatro condiciones a la vez:
//   1. hubo una acción del usuario (clic en un control, Intro) hace poco;
//   2. en esa tanda de cambios la tabla solo tocó UNA fila;
//   3. no se quitó ninguna fila (filtrar hasta dejar una fila no cuenta);
//   4. si la fila es nueva, la tabla ya tenía otras (no es la primera carga).
// La heurística puede quedarse corta (no destella) pero no debe destellar de más.
(function () {
    const VENTANA_MS = 4000;
    const reducir = window.matchMedia('(prefers-reduced-motion: reduce)');
    let ultimaAccion = -Infinity;

    const marcar = function () { ultimaAccion = performance.now(); };
    document.addEventListener('pointerdown', function (evento) {
        if (evento.target.closest && evento.target.closest('button, [role="button"], a, [type="submit"]')) marcar();
    }, true);
    document.addEventListener('keydown', function (evento) {
        if (evento.key === 'Enter') marcar();
    }, true);

    const esFilaReal = function (fila) {
        return fila.nodeType === 1 && fila.tagName === 'TR' && fila.textContent.trim() !== '';
    };

    const destellar = function (fila) {
        fila.classList.remove('fila-destello');
        void fila.offsetWidth; // reinicia la animación si ya estaba puesta
        fila.classList.add('fila-destello');
        const quitar = function () { fila.classList.remove('fila-destello'); };
        fila.addEventListener('animationend', quitar, { once: true });
        setTimeout(quitar, 2000); // red de seguridad: sin animationend no se acumula la clase
    };

    const observador = new MutationObserver(function (mutaciones) {
        if (reducir.matches || performance.now() - ultimaAccion > VENTANA_MS) return;

        const porCuerpo = new Map();
        const de = function (cuerpo) {
            let t = porCuerpo.get(cuerpo);
            if (!t) { t = { nuevas: new Set(), modificadas: new Set(), quitada: false }; porCuerpo.set(cuerpo, t); }
            return t;
        };

        for (const m of mutaciones) {
            const origen = m.target.nodeType === 1 ? m.target : m.target.parentElement;
            const cuerpo = origen && origen.closest('.tabla-datos tbody');
            if (!cuerpo) continue;

            if (m.type === 'childList' && origen === cuerpo) {
                m.addedNodes.forEach(function (n) { if (esFilaReal(n)) de(cuerpo).nuevas.add(n); });
                m.removedNodes.forEach(function (n) { if (n.nodeType === 1 && n.tagName === 'TR') de(cuerpo).quitada = true; });
            } else {
                const fila = origen.closest('tr');
                if (fila && esFilaReal(fila)) de(cuerpo).modificadas.add(fila);
            }
        }

        porCuerpo.forEach(function (t, cuerpo) {
            if (t.quitada) return;
            const tocadas = new Set([...t.nuevas, ...t.modificadas]);
            if (tocadas.size !== 1) return;
            const fila = [...tocadas][0];
            if (t.nuevas.has(fila)) {
                const previas = Array.from(cuerpo.children).filter(function (f) { return f !== fila && esFilaReal(f); });
                if (previas.length === 0) return;
            }
            destellar(fila);
        });
    });

    observador.observe(document.body, { childList: true, subtree: true, characterData: true });
})();
