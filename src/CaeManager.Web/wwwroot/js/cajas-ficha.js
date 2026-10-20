// Alturas de las cajas de la pestaña «Ficha» (CajasFicha.razor), en el orden en que están
// pintadas. El componente las usa para colocarlas de la más alta a la más baja.
export function medirAlturas(raiz) {
    return raiz ? Array.from(raiz.children, (caja) => caja.offsetHeight) : [];
}

export function leerOrden(clave) {
    try {
        const valor = JSON.parse(localStorage.getItem('talveg:tarjetas:' + clave) || '[]');
        return Array.isArray(valor) ? valor.filter(x => typeof x === 'string') : [];
    } catch { return []; }
}

export function guardarOrden(clave, orden) {
    try {
        if (orden.length) localStorage.setItem('talveg:tarjetas:' + clave, JSON.stringify(orden));
        else localStorage.removeItem('talveg:tarjetas:' + clave);
    } catch { /* El almacenamiento es opcional; la ordenación sigue funcionando. */ }
}
