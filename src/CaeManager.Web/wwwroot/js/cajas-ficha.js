// Alturas de las cajas de la pestaña «Ficha» (CajasFicha.razor), en el orden en que están
// pintadas. El componente las usa para colocarlas de la más alta a la más baja.
export function medirAlturas(raiz) {
    return raiz ? Array.from(raiz.children, (caja) => caja.offsetHeight) : [];
}
