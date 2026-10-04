// Cifra que cuenta hasta su valor (ficha 09, TarjetaMetrica.ContarHastaValor).
// Una sola vez, al primer pintado de la tarjeta: Blazor ya escribió el valor FINAL en el DOM (prerender, tests, sin JS el número
// es el correcto), y esto solo lo hace subir desde 0. Un refresco posterior que cambie el valor lo escribe Blazor de golpe, sin
// animar; si lo hace en mitad de la cuenta, esta se aparta. Con prefers-reduced-motion: reduce no anima nada.
// Se escribe en nodeValue del nodo de texto, no en textContent: Blazor guarda una referencia a ese nodo y la perdería.
const DURACION_MS = 900;

export function contarHasta(elemento, valorFinal) {
    const nodo = elemento?.firstChild;
    if (!nodo || nodo.nodeType !== Node.TEXT_NODE || elemento.dataset.contado === 'true') {
        return false;
    }

    elemento.dataset.contado = 'true';
    if (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches || !Number.isFinite(valorFinal) || valorFinal <= 0) {
        return false;
    }

    let ultimo = '0';
    nodo.nodeValue = ultimo;
    let inicio = null;
    const paso = (t) => {
        inicio ??= t;
        if (nodo.nodeValue !== ultimo) {
            return; // Blazor escribió otro valor: no se pisa.
        }

        const p = Math.min(1, (t - inicio) / DURACION_MS);
        ultimo = String(Math.round(valorFinal * (1 - Math.pow(1 - p, 3))));
        nodo.nodeValue = ultimo;
        if (p < 1) {
            requestAnimationFrame(paso);
        }
    };

    requestAnimationFrame(paso);
    return true;
}
