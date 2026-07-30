// Único listener global para el ripple de .boton — delegado en document en
// vez de JS interop por instancia: Boton.razor se renderiza decenas de
// veces por página (tablas, formularios), así que cargar un módulo JS por
// cada botón habría ido en contra del propio objetivo de fluidez (ver
// DESIGN_SYSTEM.md, "Micro-interacciones"). Todo lo demás (spring, glow,
// gradient-mesh, stagger, toast) es CSS puro.
document.addEventListener('click', function (evento) {
    const boton = evento.target.closest('.boton');
    if (!boton || boton.disabled) return;

    // Coherente con el @media (prefers-reduced-motion: reduce) de base.css
    // (que oculta el span vía display:none) — si no se crea el nodo, no hay
    // nada que limpiar y no se acumula nada en el DOM.
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

    const rect = boton.getBoundingClientRect();
    const tamano = Math.max(rect.width, rect.height) * 1.4;

    // Activación por teclado (Enter/Espacio) no trae coordenadas de ratón
    // reales (clientX/clientY llegan a 0) — en ese caso el ripple se centra
    // en vez de saltar a la esquina superior izquierda.
    const tieneCoordenadas = evento.clientX !== 0 || evento.clientY !== 0;
    const x = tieneCoordenadas ? evento.clientX - rect.left : rect.width / 2;
    const y = tieneCoordenadas ? evento.clientY - rect.top : rect.height / 2;

    const onda = document.createElement('span');
    onda.className = 'boton-ondulacion';
    onda.style.width = onda.style.height = tamano + 'px';
    onda.style.left = (x - tamano / 2) + 'px';
    onda.style.top = (y - tamano / 2) + 'px';

    boton.appendChild(onda);
    // Red de seguridad además de animationend: si por lo que sea la
    // animación no llega a completar (pestaña en segundo plano, etc.), el
    // span no debe quedarse acumulado en el DOM para siempre.
    let quitado = false;
    const quitar = () => { if (!quitado) { quitado = true; onda.remove(); } };
    onda.addEventListener('animationend', quitar, { once: true });
    setTimeout(quitar, 650);
});

// Envío automático de los <form> que hacen las veces de selector (hoy solo
// SelectorClienteActivo). Antes era un onchange="this.form.submit()" en el
// marcado; con la CSP de UseCabecerasSeguridad, que no admite
// 'unsafe-inline' en script-src, un manejador inline ya no se ejecutaría.
// Mismo patrón delegado que el ripple de arriba, por el mismo motivo.
document.addEventListener('change', function (evento) {
    const control = evento.target.closest('[data-enviar-al-cambiar]');
    if (control && control.form) control.form.requestSubmit();
});
