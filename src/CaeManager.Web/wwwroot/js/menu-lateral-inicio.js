// Primer pintado del modo compacto del menú lateral. Va en <head>, síncrono y mínimo, para que
// la barra nazca ya con su ancho y no parpadee de 260px a 64px cuando carga menu-lateral.js (que
// va al final y es quien lo mantiene después; lee la misma clave).
try {
    if (localStorage.getItem('hydra-menu-compacto') === '1') {
        document.documentElement.setAttribute('data-menu-compacto', '');
    }
} catch {
    // Sin almacenamiento: barra ancha, que es el valor por defecto.
}
