// Lleva al aviso de cambios sin guardar (AvisoCambiosSinGuardar.razor) las dos
// navegaciones que el NavigationLock de Blazor no ve en esta aplicación: el
// clic en un enlace interno y «atrás»/«adelante» del navegador.
//
// Por qué no las ve (medido en blazor.web.js de .NET 10): el Router no es
// interactivo (cada página lleva su @rendermode), así que esas dos
// navegaciones las atiende la navegación mejorada en el cliente — pide la
// página nueva y la funde en el documento — sin avisar antes al circuito. Solo
// el NavigateTo que lanza el servidor consulta al NavigationLock. Sin esto, una
// edición a medias se perdía sin preguntar.
//
// Qué hace: antes de que Blazor atienda el clic o el popstate, pregunta al
// circuito (SalidaDelNavegador.ConsultarSalida). Si ningún aviso tiene nada que
// perder, la navegación sigue por el camino de siempre. Si alguno la detiene,
// él enseña la pregunta y aquí:
//   - el clic simplemente no ocurre: «Salir y descartar» navega desde el servidor;
//   - el recorrido del historial se deshace (la barra de direcciones vuelve a
//     la URL de la vista que sigue en pantalla) y «Salir y descartar» lo repite.
//
// Deshacer un recorrido exige saber cuántas entradas se movió el historial. Lo
// dice la Navigation API (Chromium, Safari 26.2+, Firefox 147+). Sin ella no se
// puede saber, y la URL de la vista se reescribe sobre la entrada de destino:
// la edición se conserva igual, a costa de esa entrada del historial.
//
// Sin circuito (página estática, circuito caído) no hay a quién preguntar ni
// formulario interactivo que perder: todo pasa como si este fichero no existiera.
(function () {
    'use strict';

    const ESPERA_MAXIMA_MS = 4000;

    let guardia = null;          // DotNetObjectReference del circuito que contestó por última vez
    let ultimoRecorrido = null;  // { desde, delta } del recorrido que precede al popstate (Navigation API)
    let urlDeLaVista = location.href; // respaldo sin Navigation API: la última URL que Blazor terminó de cargar
    let pendiente = null;        // recorrido detenido, por si «Salir y descartar» lo repite
    let tragarPopstate = 0;      // los popstate de deshacer un recorrido no son una navegación
    let dejarPasarPopstate = 0;  // los popstate que emite este fichero no se vuelven a consultar
    let dejarPasarClic = false;

    if (window.navigation && typeof window.navigation.addEventListener === 'function') {
        window.navigation.addEventListener('currententrychange', function (evento) {
            ultimoRecorrido = evento.navigationType === 'traverse' && evento.from && evento.from.index >= 0
                ? { desde: evento.from.url, delta: window.navigation.currentEntry.index - evento.from.index }
                : null;
        });
    }

    // Este fichero se carga antes que blazor.web.js (ver más abajo): window.Blazor aún no existe, y se engancha
    // la primera vez que un aviso se presenta.
    let cargaEnganchada = false;
    const engancharCarga = function () {
        if (cargaEnganchada || !window.Blazor || typeof window.Blazor.addEventListener !== 'function') return;
        cargaEnganchada = true;
        window.Blazor.addEventListener('enhancedload', function () { urlDeLaVista = location.href; });
    };

    const mismaPagina = function (a, b) {
        const ua = new URL(a), ub = new URL(b);
        return ua.origin === ub.origin && ua.pathname === ub.pathname && ua.search === ub.search;
    };

    const esInterna = function (url) {
        const base = document.baseURI.substring(0, document.baseURI.lastIndexOf('/'));
        const siguiente = url.charAt(base.length);
        return url.startsWith(base) && (siguiente === '' || siguiente === '/' || siguiente === '?' || siguiente === '#');
    };

    // true = algún aviso la detuvo y ya está preguntando. Un circuito que no contesta no detiene nada.
    const consultar = function (destino, esRecorrido) {
        const circuito = guardia;
        if (!circuito) return Promise.resolve(false);
        const espera = new Promise(function (resolver) { setTimeout(function () { resolver(false); }, ESPERA_MAXIMA_MS); });
        // Un rechazo (circuito caído, o un fallo al consultar) no detiene esta navegación, pero tampoco apaga las
        // siguientes: un circuito muerto rechaza enseguida cada vez.
        const respuesta = circuito.invokeMethodAsync('ConsultarSalida', destino, esRecorrido).catch(function () { return false; });
        return Promise.race([respuesta, espera]);
    };

    const enlaceDe = function (evento) {
        const camino = evento.composedPath ? evento.composedPath() : [];
        for (let i = 0; i < camino.length; i++) {
            if (camino[i] instanceof HTMLAnchorElement || camino[i] instanceof SVGAElement) return camino[i];
        }
        return null;
    };

    // Los mismos enlaces que la navegación mejorada intercepta; los demás (otra pestaña, descarga, fuera de
    // la aplicación, data-enhance-nav="false") cargan un documento y los cubre el aviso del propio navegador.
    document.addEventListener('click', function (evento) {
        if (dejarPasarClic || !guardia) return;
        if (evento.button !== 0 || evento.ctrlKey || evento.shiftKey || evento.altKey || evento.metaKey || evento.defaultPrevented) return;
        const enlace = enlaceDe(evento);
        if (!enlace || !enlace.hasAttribute('href') || enlace.hasAttribute('download')) return;
        const destinoDelMarco = enlace.getAttribute('target');
        if (destinoDelMarco && destinoDelMarco !== '_self') return;
        const sinMejora = enlace.closest('[data-enhance-nav]');
        if (sinMejora) {
            const valor = sinMejora.getAttribute('data-enhance-nav');
            if (valor !== '' && valor.toLowerCase() !== 'true') return;
        }
        const destino = enlace.href;
        if (typeof destino !== 'string' || !esInterna(destino) || mismaPagina(destino, location.href)) return;
        // Un fichero (exportar.xlsx, plantilla.xlsx) se descarga sin abandonar la página: no hay nada que preguntar.
        if (/\.[a-z0-9]{2,5}$/i.test(new URL(destino).pathname)) return;

        // El clic se retira entero: si la navegación sigue, se repite tal cual y sus manejadores corren una sola vez.
        evento.preventDefault();
        evento.stopImmediatePropagation();
        consultar(destino, false).then(function (detenida) {
            if (detenida) return;
            if (!enlace.isConnected) {
                window.Blazor.navigateTo(destino);
                return;
            }
            dejarPasarClic = true;
            try {
                enlace.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, composed: true, view: window }));
            } finally {
                dejarPasarClic = false;
            }
        });
    }, true);

    const deshacer = function (recorrido) {
        if (recorrido.delta === 0) {
            // Sin Navigation API: la entrada de destino pasa a decir la vista que sigue en pantalla.
            history.replaceState(history.state, '', recorrido.desde);
            return;
        }
        tragarPopstate++;
        history.go(-recorrido.delta);
    };

    // Tiene que ser el PRIMER oyente de popstate de window: corren en el orden en que se registraron (medido: con
    // window como destino el modo captura no adelanta a nadie), y los de Blazor avisan al circuito del cambio de URL
    // en cuanto lo ven — una ficha del Context Workspace se cerraba, con lo escrito, antes de poder preguntar. Por
    // eso App.razor carga este fichero antes que blazor.web.js.
    window.addEventListener('popstate', function (evento) {
        if (tragarPopstate > 0) {
            tragarPopstate--;
            ultimoRecorrido = null;
            evento.stopImmediatePropagation();
            return;
        }
        const recorrido = ultimoRecorrido;
        ultimoRecorrido = null;
        if (dejarPasarPopstate > 0) {
            dejarPasarPopstate--;
            return;
        }
        if (!guardia) return;

        const destino = location.href;
        const desde = recorrido ? recorrido.desde : urlDeLaVista;
        // Solo cambia el fragmento: nada se desmonta, y Blazor lo resuelve con un desplazamiento.
        if (mismaPagina(desde, destino)) return;

        evento.stopImmediatePropagation();
        const estado = evento.state;
        const este = { destino: destino, desde: desde, delta: recorrido ? recorrido.delta : 0 };
        pendiente = este;
        consultar(destino, true).then(function (detenida) {
            if (detenida) {
                deshacer(este);
                return;
            }
            if (pendiente === este) pendiente = null;
            // Nadie la detiene: Blazor recibe el popstate que no llegó a ver.
            dejarPasarPopstate++;
            window.dispatchEvent(new PopStateEvent('popstate', { state: estado }));
        });
    }, true);

    window.talvegAvisoSalida = {
        // Lo llama cada aviso al montarse: a partir de aquí hay circuito al que preguntar.
        conectar: function (referencia) {
            guardia = referencia;
            engancharCarga();
        },
        // «Salir y descartar» tras un recorrido detenido: se repite, ya sin consultar.
        reanudar: function () {
            const recorrido = pendiente;
            pendiente = null;
            if (!recorrido) return;
            if (recorrido.delta !== 0) {
                dejarPasarPopstate++;
                history.go(recorrido.delta);
                return;
            }
            window.Blazor.navigateTo(recorrido.destino, { replaceHistoryEntry: true });
        }
    };
})();
