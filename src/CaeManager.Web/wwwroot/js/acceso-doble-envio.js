// Guarda de doble envío para los formularios de la escena de acceso (2FA,
// olvidé/restablecer/cambiar contraseña).
//
// Las cuatro páginas son SSR estática (sin @rendermode): cada POST construye
// una instancia nueva del componente, con su propia bandera de instancia a
// false. Una guarda «if (_enviando) return» en el servidor no puede ver la
// segunda petición — medido con una sonda propia, no supuesto.
//
// OJO con lo que este guion demuestra y lo que no. Medido con Playwright
// (DobleEnvioAccesoTests): un doble clic real en UNA pestaña ya llega al
// servidor como una sola petición POST **con este guion completamente
// retirado de la página** — el propio envío de formulario de Blazor hace
// una recarga completa del documento (se comprobó que `window` se destruye
// tras el POST, no una navegación «enhanced» que preserve el JS), y esa
// recarga serializa cualquier segundo clic dentro de la misma pestaña antes
// de que este guion llegue a intervenir. Es decir: para el doble clic en una
// pestaña, el guion es defensa en profundidad, no la barrera que lo impide.
//
// Lo que este guion NO cubre, medido igual de directo: dos pestañas o dos
// contextos de navegador distintos SÍ producen dos POST reales — cada uno
// con su propio WeakSet, sin nada en común — y tampoco cubre un segundo POST
// disparado sin pasar por el navegador (curl, un script, JS desactivado).
// Esos huecos quedan donde corresponda documentarlos, no resueltos aquí.
//
// Por qué se mantiene de todos modos: es barato, no rompe nada, corta el
// Enter repetido y el requestSubmit() disparado sin pasar por el botón, y es
// la única red que queda si algún día esta app deja de caer a recarga
// completa (otro navegador, otra versión de blazor.web.js) y una navegación
// «enhanced» de verdad llega a solapar dos fetch en la misma pestaña.
//
// Guion global (App.razor) y no un módulo por página, mismo motivo que
// acceso-agua.js: la navegación mejorada de Blazor parchea el DOM sin
// reejecutar los <script> que traiga la página, así que el oyente se
// delega en document y no hay que volver a montarlo tras cada
// «enhancedload» — un formulario nuevo simplemente no está en el WeakSet.
(function () {
    'use strict';

    var ESCENA = '.acceso-tarjeta';
    var enviando = new WeakSet();

    // Fase de captura: si ya hay un envío en curso para este formulario, el
    // segundo evento de submit se corta aquí, antes de que blazor.web.js
    // (registrado en fase de burbuja) llegue a convertirlo en una petición
    // real de navegación mejorada. Ningún envío duplicado sale del navegador.
    document.addEventListener('submit', function (evento) {
        var formulario = evento.target;
        if (!(formulario instanceof HTMLFormElement)) return;
        if (!formulario.closest(ESCENA)) return;

        if (enviando.has(formulario)) {
            evento.preventDefault();
            evento.stopImmediatePropagation();
            return;
        }

        enviando.add(formulario);

        // El disabled es lo que de verdad impide el doble clic o el Enter
        // repetido: un botón deshabilitado no vuelve a disparar submit. El
        // WeakSet de arriba cubre además un segundo submit disparado sin
        // pasar por el botón (por ejemplo, otro requestSubmit()).
        var botones = formulario.querySelectorAll('button[type="submit"]');
        for (var i = 0; i < botones.length; i++) botones[i].disabled = true;
    }, true);
})();
