// Luz de agua de la pantalla de acceso (AccesoLayout). Traslada 1:1 el artefacto
// aprobado «Luz de agua»: luz difusa que cruza, cuatro capas con periodos primos
// entre sí (7/11/17/26 s) para que el conjunto no repita un patrón reconocible,
// todas avanzando en el mismo sentido — un río fluye hacia un lado; oscilar es
// lo que hace el viento. Velocidad «Elegida» (×1) e intensidad «El río» (×1).
//
// 1:1 también en escala. El artefacto dibuja el río en un recuadro de 920×575
// (16:10, el ancho útil de su página de 1000 px) con un desenfoque fijo de 7 px
// y el lienzo a un cuarto de resolución. Dibujado a pantalla completa con esos
// mismos 7 px y un cuarto de resolución, el agua salía más nítida, más densa y
// con otra proporción que la elegida. Por eso aquí se dibuja SIEMPRE esa escena
// de referencia —mismo lienzo de 258×161, mismo desenfoque, mismo velo— y se
// amplía entera hasta cubrir la pantalla (--acceso-escala), recortando lo que
// sobre, como una fotografía a sangre.
//
// Progresivo: sin JavaScript la pantalla se lee y funciona entera. El lienzo
// queda vacío sobre el verde y el interruptor de movimiento nace con [hidden],
// porque sin este guion no haría nada.
//
// Guion global (App.razor) y no un módulo por página: el acceso se sirve en
// renderizado estático y la navegación mejorada de Blazor parchea el DOM sin
// ejecutar los <script> que traiga la página, así que se monta en la carga
// inicial y otra vez en cada «enhancedload».
(function () {
    'use strict';

    // Se guarda por navegador y dispositivo: el acceso es previo a
    // autenticarse, así que no hay usuario al que asociar la preferencia.
    var CLAVE = 'talveg.movimiento';
    var TAU = Math.PI * 2;
    var DIV = 4;
    // Recuadro del artefacto: min(1000, ancho) − 2 × 40 px de margen, 16:10.
    var REF_ANCHO = 920;
    var REF_ALTO = 575;

    var capas = [
        { y: .60, ciclos: 1.3, amp: .042, periodo: 7, grosor: .20, a: .085, fase: 0 },
        { y: .70, ciclos: 0.8, amp: .058, periodo: 11, grosor: .28, a: .065, fase: 2.1 },
        { y: .48, ciclos: 2.1, amp: .030, periodo: 17, grosor: .15, a: .048, fase: 4.7 },
        { y: .82, ciclos: 0.6, amp: .072, periodo: 26, grosor: .36, a: .038, fase: 1.3 }
    ];

    var escena = null;

    function leerPreferencia() {
        // En modo privado o con el almacenamiento bloqueado, localStorage
        // lanza: se degrada a la preferencia del sistema.
        try { return localStorage.getItem(CLAVE); } catch (e) { return null; }
    }

    function guardarPreferencia(valor) {
        try { localStorage.setItem(CLAVE, valor); } catch (e) { }
    }

    function montar() {
        var lienzo = document.querySelector('[data-acceso-agua]');
        if (escena && escena.lienzo === lienzo) return;
        desmontar();
        if (!lienzo) return;

        var e = {
            lienzo: lienzo,
            referencia: document.querySelector('[data-acceso-referencia]'),
            x: lienzo.getContext('2d'),
            W: 0,
            H: 0,
            raf: null,
            ultimo: 0,
            tAviso: null,
            quieto: window.matchMedia('(prefers-reduced-motion: reduce)').matches,
            pref: leerPreferencia(),
            boton: document.querySelector('[data-acceso-mov]'),
            texto: document.querySelector('[data-acceso-mov-texto]'),
            aviso: document.querySelector('[data-acceso-aviso]')
        };
        escena = e;

        medir(e);
        // La escala sigue al contenedor, no solo a la ventana: la escena crece
        // cuando aparece un error o la validación, y hay cambios de tamaño que
        // no disparan «resize» (emulación de dispositivo, paneles).
        if (window.ResizeObserver && e.referencia && e.referencia.parentElement) {
            e.observador = new ResizeObserver(function () {
                if (escena !== e) return;
                medir(e);
                if (!enMovimiento(e)) pintar(e, e.ultimo);
            });
            e.observador.observe(e.referencia.parentElement);
        }
        if (e.boton) {
            e.boton.hidden = false;
            e.boton.addEventListener('click', function () { alternar(e); });
        }
        sincronizar(e);
    }

    function desmontar() {
        if (!escena) return;
        if (escena.raf !== null) cancelAnimationFrame(escena.raf);
        if (escena.observador) escena.observador.disconnect();
        clearTimeout(escena.tAviso);
        escena = null;
    }

    function medir(e) {
        // La escena de referencia se amplía hasta cubrir la de verdad.
        var escala = 1;
        if (e.referencia && e.referencia.parentElement) {
            var contenedor = e.referencia.parentElement;
            escala = Math.max(contenedor.clientWidth / REF_ANCHO, contenedor.clientHeight / REF_ALTO) || 1;
            e.referencia.style.setProperty('--acceso-escala', String(escala));
        }
        // Tamaño del lienzo SIN la ampliación: el de la escena de referencia
        // (112 % de 920×575), así su resolución es siempre la del artefacto.
        var r = e.lienzo.getBoundingClientRect();
        var W = Math.max(2, Math.ceil(r.width / escala / DIV));
        var H = Math.max(2, Math.ceil(r.height / escala / DIV));
        if (W !== e.W || H !== e.H) {
            e.W = W;
            e.H = H;
            e.lienzo.width = W;
            e.lienzo.height = H;
        }
    }

    function pintar(e, t) {
        var x = e.x, W = e.W, H = e.H;
        x.clearRect(0, 0, W, H);
        // Respiración global: un ciclo cada 90 s, apenas perceptible.
        var resp = 0.88 + 0.12 * Math.sin(t * TAU / 90000);
        x.globalCompositeOperation = 'lighter';
        for (var i = 0; i < capas.length; i++) {
            var L = capas[i], yc = H * L.y, amp = H * L.amp, gr = H * L.grosor;
            var desp = t * TAU / (L.periodo * 1000) + L.fase, px;
            x.beginPath();
            for (px = 0; px <= W; px++) {
                var y = yc + Math.sin((px / W) * TAU * L.ciclos + desp) * amp;
                if (px === 0) x.moveTo(px, y - gr / 2); else x.lineTo(px, y - gr / 2);
            }
            for (px = W; px >= 0; px--) {
                var y2 = yc + Math.sin((px / W) * TAU * L.ciclos + desp) * amp;
                x.lineTo(px, y2 + gr / 2);
            }
            x.closePath();
            x.fillStyle = 'rgba(143,199,188,' + (L.a * resp).toFixed(4) + ')';
            x.fill();
        }
    }

    // Sin elección explícita manda el sistema operativo; si la persona ha
    // decidido, su decisión pesa más que la preferencia del sistema.
    function enMovimiento(e) {
        if (e.pref === '0') return false;
        if (e.pref === '1') return true;
        return !e.quieto;
    }

    function bucle(e) {
        return function paso(t) {
            // La navegación mejorada puede retirar el lienzo sin avisar: sin
            // esta guarda el bucle seguiría pintando en un nodo huérfano.
            if (!e.lienzo.isConnected) { desmontar(); return; }
            e.ultimo = t;
            pintar(e, t);
            e.raf = requestAnimationFrame(paso);
        };
    }

    function sincronizar(e) {
        var on = enMovimiento(e);
        if (e.boton) e.boton.setAttribute('aria-pressed', on ? 'true' : 'false');
        if (e.texto) {
            e.texto.textContent = on
                ? 'Desactivar el movimiento del fondo'
                : 'Activar el movimiento del fondo';
        }
        if (on) {
            if (e.raf === null) e.raf = requestAnimationFrame(bucle(e));
        } else {
            if (e.raf !== null) { cancelAnimationFrame(e.raf); e.raf = null; }
            // Quieto no es apagado: queda un fotograma, la luz sin moverse.
            pintar(e, e.ultimo);
        }
    }

    function alternar(e) {
        var iba = enMovimiento(e);
        e.pref = iba ? '0' : '1';
        guardarPreferencia(e.pref);
        sincronizar(e);
        avisar(e, iba
            ? 'Movimiento desactivado. Se recordará en este navegador.'
            : 'Movimiento activado.');
    }

    function avisar(e, texto) {
        if (!e.aviso) return;
        e.aviso.textContent = texto;
        e.aviso.classList.add('acceso-aviso-visible');
        clearTimeout(e.tAviso);
        e.tAviso = setTimeout(function () { e.aviso.classList.remove('acceso-aviso-visible'); }, 2600);
    }

    // Si la persona cambia «reducir movimiento» en el sistema con la pantalla
    // abierta, se obedece al momento. Un solo oyente global, no uno por
    // montaje: tras varias navegaciones mejoradas se acumularían.
    var consultaQuieto = window.matchMedia('(prefers-reduced-motion: reduce)');
    if (typeof consultaQuieto.addEventListener === 'function') {
        consultaQuieto.addEventListener('change', function (ev) {
            if (!escena) return;
            escena.quieto = ev.matches;
            sincronizar(escena);
        });
    }

    window.addEventListener('resize', function () {
        if (!escena) return;
        medir(escena);
        if (!enMovimiento(escena)) pintar(escena, escena.ultimo);
    });

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montar);
    else montar();

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', montar);
    }
})();
