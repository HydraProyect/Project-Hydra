// KeyTips: etiquetas de acceso al estilo de la cinta de Excel. Alt pulsada y soltada SOLA
// enciende una letra sobre cada control declarado con data-keytip; la letra lo ejecuta, la de
// un grupo o un menú baja un nivel, Retroceso sube y Esc sale.
//
// Convivencia con lo que ya existe:
// - Se enciende al SOLTAR Alt, y solo si entre pulsarla y soltarla no hubo otra tecla ni
//   ratón: Alt + clic (copiar la fecha de emisión, CampoCopiable) sigue siendo de ese gesto.
// - Los manejadores van en fase de CAPTURA y, con el modo encendido, cortan la propagación:
//   atajos-lista.js (j k x Enter f) y atajos-globales.js (g…, n, ?) no llegan a ver la tecla.
//   Con el modo apagado este módulo no toca ninguna tecla salvo Alt.
// - No se enciende con el foco en un campo de texto (Alt + teclado numérico escribe caracteres)
//   ni con un Modal o Drawer abierto, salvo que el diálogo lo declare con data-keytips.
// - Se apaga al perder el foco la ventana, con un clic, y con cualquier combinación de teclas.
//
// Windows: Alt suelta enfoca el menú del navegador (en Chrome y Edge, el botón «⋮»), y la
// tecla siguiente se la queda el navegador. preventDefault() en el keydown Y en el keyup de
// Alt lo evita; por eso se cancelan los dos siempre que el foco no esté en un campo de texto.
import { hayDialogoModalAbierto } from './atajos-contexto.js';

// Debe coincidir con CatalogoAtajos.KeyTips (CatalogoAtajosSincronizadoConJsTests). Son las
// letras de los controles compartidos: una letra deducida nunca las ocupa, aunque el control
// no esté en la pantalla, para que signifiquen lo mismo en los diez listados.
const LETRAS_ESTABLES = ['S', 'X', 'K', 'M', 'N', 'F', 'T', 'L', 'G', 'A', 'E'];

const SELECTOR_DIALOGO = '[role="dialog"][aria-modal="true"], [role="alertdialog"][aria-modal="true"], dialog:modal';
const SELECTOR_ACTIVABLE = 'button, a[href], [role="tab"], [role="button"], input, select';
const SELECTOR_ITEM_MENU = '[role="menuitem"], [role="menuitemradio"], [role="menuitemcheckbox"]';
const TIPOS_INPUT_NO_TEXTO = new Set(['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color', 'file', 'image']);
const ALFABETO = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
const ESPERA_MENU_MS = 2000;

const estado = {
    encendido: false,
    // { tipo: 'raiz' } | { tipo: 'grupo', elemento } | { tipo: 'menu', disparador }
    nivel: { tipo: 'raiz' },
    mapa: new Map(),
    altLimpia: false,
    capa: null,
    anuncio: null,
    observador: null,
    repintadoPendiente: false,
    esperaMenu: null,
    // Cada registrarKeyTips() abre una generación: el dispose de un registro viejo (el islote
    // se recreó y su Dispose llega tarde) no desmonta los manejadores del nuevo.
    generacion: 0,
    // Solo se anuncia «desactivado» si antes se anunció «activado».
    anunciado: false,
    textos: { raiz: '', nivel: '', subir: '', salir: '', teclaSubir: 'Retroceso', teclaSalir: 'Esc', encendido: '', apagado: '' }
};

// Un <select> no cuenta: Alt sola no escribe nada en él (Alt + ↓, que lo abre, lleva otra
// tecla y no enciende), y tratarlo como campo impediría encender el modo con el foco en «Mostrar 20».
function esCampoDeTexto(elemento) {
    if (!elemento) return false;
    return elemento.tagName === 'TEXTAREA' || elemento.isContentEditable ||
        (elemento.tagName === 'INPUT' && !TIPOS_INPUT_NO_TEXTO.has(elemento.type));
}

function esVisible(elemento) {
    const cajas = elemento.getClientRects();
    if (cajas.length === 0 || getComputedStyle(elemento).visibility !== 'visible') return false;
    // Fuera de la ventana no recibe etiqueta: una letra que no se ve no se puede elegir.
    const caja = elemento.getBoundingClientRect();
    return caja.bottom > 0 && caja.right > 0 && caja.top < window.innerHeight && caja.left < window.innerWidth;
}

function estaHabilitado(elemento) {
    return !elemento.disabled && elemento.getAttribute('aria-disabled') !== 'true';
}

// Con un diálogo modal abierto solo cuentan los controles de un diálogo que se declare con
// data-keytips. Sin diálogo, todo el documento.
function ambito() {
    if (!hayDialogoModalAbierto()) return document;
    const declarados = Array.from(document.querySelectorAll(SELECTOR_DIALOGO))
        .filter(d => d.hasAttribute('data-keytips') && d.getClientRects().length > 0);
    return declarados.length > 0 ? declarados[declarados.length - 1] : null;
}

// data-keytip-contenedor="N" delega la letra en el primer control activable de dentro: es
// como una pieza compartida (CabeceraListado) etiqueta la acción primaria que le pasa la página.
function resolverContenedor(contenedor) {
    return Array.from(contenedor.querySelectorAll(SELECTOR_ACTIVABLE)).find(e => esVisible(e) && estaHabilitado(e)) ?? null;
}

function panelDelMenu(disparador) {
    const id = disparador.getAttribute('aria-controls');
    return id ? document.getElementById(id) : null;
}

// Devuelve [{ elemento, letra (declarada o ''), nombre }] del nivel actual.
function objetivos() {
    const nivel = estado.nivel;

    if (nivel.tipo === 'menu') {
        const panel = panelDelMenu(nivel.disparador);
        if (!panel) return [];
        return Array.from(panel.querySelectorAll(SELECTOR_ITEM_MENU))
            .filter(e => esVisible(e) && estaHabilitado(e))
            .map(e => ({ elemento: e, letra: e.getAttribute('data-keytip') ?? '', nombre: nombreDe(e) }));
    }

    if (nivel.tipo === 'grupo') {
        if (!nivel.elemento.isConnected) return [];
        return Array.from(nivel.elemento.querySelectorAll(SELECTOR_ACTIVABLE))
            .filter(e => esVisible(e) && estaHabilitado(e))
            .map(e => ({ elemento: e, letra: e.getAttribute('data-keytip') ?? '', nombre: nombreDe(e) }));
    }

    const raiz = ambito();
    if (!raiz) return [];
    const lista = [];
    for (const declarado of raiz.querySelectorAll('[data-keytip], [data-keytip-contenedor]')) {
        // Lo de dentro de un grupo se etiqueta al bajar a él, no en la raíz.
        const grupo = declarado.parentElement?.closest('[data-keytip-grupo]');
        if (grupo) continue;

        if (declarado.hasAttribute('data-keytip-contenedor')) {
            const interior = resolverContenedor(declarado);
            // Si el control de dentro ya declara su letra, manda la suya.
            if (interior && !interior.hasAttribute('data-keytip')) {
                lista.push({ elemento: interior, letra: declarado.getAttribute('data-keytip-contenedor') ?? '', nombre: nombreDe(interior) });
            }
            continue;
        }

        if (!esVisible(declarado)) continue;
        if (!declarado.hasAttribute('data-keytip-grupo') && !estaHabilitado(declarado)) continue;
        lista.push({ elemento: declarado, letra: declarado.getAttribute('data-keytip') ?? '', nombre: nombreDe(declarado) });
    }
    return lista;
}

// El nombre del que se deduce la letra. data-keytip-nombre lo fija cuando el texto visible
// cambia con el estado («Estado: Con vencidos»): la letra sale del nombre del filtro.
function nombreDe(elemento) {
    return elemento.getAttribute('data-keytip-nombre') || elemento.getAttribute('aria-label') || elemento.textContent || '';
}

function candidatas(nombre) {
    const limpio = nombre.normalize('NFD').replace(/[̀-ͯ]/g, '').toUpperCase().replace(/[^A-Z0-9 ]/g, ' ').trim();
    const palabras = limpio.split(/ +/).filter(Boolean);
    return [...palabras.map(p => p[0]), ...limpio.replace(/ /g, ''), ...ALFABETO];
}

// Las declaradas primero; después las deducidas, que nunca ocupan una letra ya tomada ni, en
// la raíz, una estable. Dos controles con la misma letra declarada: gana el primero del
// documento y el segundo recibe una deducida (el test del catálogo impide que ocurra).
export function asignarLetras(lista, esRaiz) {
    const usadas = new Set();
    const resultado = new Map();
    const pendientes = [];

    for (const objetivo of lista) {
        const letra = (objetivo.letra || '').trim().toUpperCase().slice(0, 1);
        if (letra && !usadas.has(letra)) {
            usadas.add(letra);
            resultado.set(letra, objetivo.elemento);
        } else {
            pendientes.push(objetivo);
        }
    }

    const vetadas = esRaiz ? new Set(LETRAS_ESTABLES) : new Set();
    for (const objetivo of pendientes) {
        const letra = candidatas(objetivo.nombre).find(c => !usadas.has(c) && !vetadas.has(c));
        if (!letra) continue;
        usadas.add(letra);
        resultado.set(letra, objetivo.elemento);
    }
    return resultado;
}

function asegurarCapa() {
    if (!estado.capa?.isConnected) {
        // aria-hidden: las letras son una ayuda visual, no contenido. Lo que se anuncia es el
        // cambio de modo, en la región de estado de abajo.
        estado.capa = document.createElement('div');
        estado.capa.className = 'keytips-capa';
        estado.capa.setAttribute('aria-hidden', 'true');
        document.body.appendChild(estado.capa);
    }

    // La región viva se crea VACÍA al registrar, no junto con su primer texto: un lector de
    // pantalla no suele anunciar una región que nace ya con contenido.
    if (!estado.anuncio?.isConnected) {
        estado.anuncio = document.createElement('div');
        estado.anuncio.className = 'keytips-anuncio';
        estado.anuncio.setAttribute('role', 'status');
        estado.anuncio.setAttribute('aria-live', 'polite');
        document.body.appendChild(estado.anuncio);
    }
}

function pintar() {
    estado.repintadoPendiente = false;
    if (!estado.encendido) return;

    // El menú del nivel actual se cerró (se eligió una opción con el ratón, Esc del propio
    // menú…): no queda nada que etiquetar ahí.
    // Mientras el panel no ha llegado todavía (lo abre el servidor) no hay nada que pintar ni
    // motivo para salir: de eso se ocupa la espera de ejecutar().
    if (estado.nivel.tipo === 'menu') {
        const abierto = estado.nivel.disparador.getAttribute('aria-expanded') === 'true';
        if (abierto) {
            estado.nivel.visto = true;
        } else {
            if (estado.nivel.visto || !estado.nivel.disparador.isConnected) apagar();
            return;
        }
    }

    estado.mapa = asignarLetras(objetivos(), estado.nivel.tipo === 'raiz');
    // Un menú abierto sin opciones habilitadas se queda con la barra sola (Retroceso o Esc).
    if (estado.mapa.size === 0 && estado.nivel.tipo !== 'menu') {
        // Un grupo o un menú sin nada dentro devuelve a la raíz; una raíz vacía apaga.
        if (estado.nivel.tipo === 'raiz') { apagar(); return; }
        estado.nivel = { tipo: 'raiz' };
        estado.mapa = asignarLetras(objetivos(), true);
        if (estado.mapa.size === 0) { apagar(); return; }
    }

    asegurarCapa();
    const fragmento = document.createDocumentFragment();
    for (const [letra, elemento] of estado.mapa) {
        const caja = elemento.getBoundingClientRect();
        const etiqueta = document.createElement('span');
        etiqueta.className = 'keytip';
        etiqueta.textContent = letra;
        etiqueta.style.left = `${Math.round(caja.left + Math.min(10, Math.max(0, caja.width / 2 - 8)))}px`;
        etiqueta.style.top = `${Math.round(caja.bottom - 9)}px`;
        fragmento.appendChild(etiqueta);
    }

    const barra = document.createElement('div');
    barra.className = 'keytips-barra';
    const texto = document.createElement('span');
    texto.textContent = estado.nivel.tipo === 'raiz' ? estado.textos.raiz : estado.textos.nivel;
    barra.appendChild(texto);
    for (const [tecla, descripcion] of [[estado.textos.teclaSubir, estado.textos.subir], [estado.textos.teclaSalir, estado.textos.salir]]) {
        const kbd = document.createElement('kbd');
        kbd.textContent = tecla;
        barra.append(' · ', kbd, ` ${descripcion}`);
    }
    fragmento.appendChild(barra);

    estado.capa.replaceChildren(fragmento);
    document.documentElement.dataset.keytips = estado.nivel.tipo;
}

function programarRepintado() {
    if (!estado.encendido || estado.repintadoPendiente) return;
    estado.repintadoPendiente = true;
    requestAnimationFrame(pintar);
}

function anunciar(texto) {
    if (estado.anuncio) estado.anuncio.textContent = texto;
}

function encender() {
    if (estado.encendido) return;
    if (esCampoDeTexto(document.activeElement)) return;
    if (ambito() === null) return;

    estado.encendido = true;
    estado.nivel = { tipo: 'raiz' };
    pintar();
    if (!estado.encendido) return; // no había nada que etiquetar

    // Blazor repinta por el circuito: las etiquetas siguen a los controles. Se observa el
    // cuerpo entero menos la capa propia (si no, cada pintado dispararía otro).
    estado.observador = new MutationObserver(mutaciones => {
        if (mutaciones.some(m => !estado.capa?.contains(m.target) && m.target !== estado.capa && m.target !== estado.anuncio)) {
            programarRepintado();
        }
    });
    estado.observador.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['aria-expanded', 'aria-pressed', 'disabled', 'class', 'hidden'] });
    estado.anunciado = true;
    anunciar(estado.textos.encendido);
}

function apagar() {
    cancelarEsperaMenu();
    if (!estado.encendido) return;
    estado.encendido = false;
    estado.nivel = { tipo: 'raiz' };
    estado.mapa = new Map();
    estado.observador?.disconnect();
    estado.observador = null;
    estado.capa?.replaceChildren();
    delete document.documentElement.dataset.keytips;
    // Una Alt en una pantalla sin nada que etiquetar enciende y apaga en el acto: no se anuncia.
    if (estado.anunciado) anunciar(estado.textos.apagado);
    estado.anunciado = false;
}

function cancelarEsperaMenu() {
    if (estado.esperaMenu) {
        clearTimeout(estado.esperaMenu);
        estado.esperaMenu = null;
    }
}

// Si el panel todavía no ha llegado (el clic que lo abre va de camino al servidor), cerrarlo
// ahora no haría nada y el menú se abriría después, ya sin modo: se espera a que anuncie
// aria-expanded="true" y se cierra entonces.
function cerrarMenuDelNivel() {
    if (estado.nivel.tipo !== 'menu') return;
    const disparador = estado.nivel.disparador;
    if (disparador.getAttribute('aria-expanded') === 'true') {
        disparador.click();
        return;
    }
    if (estado.nivel.visto || !disparador.isConnected) return;

    const vigia = new MutationObserver(() => {
        if (disparador.getAttribute('aria-expanded') !== 'true') return;
        terminar();
        disparador.click();
    });
    const caducidad = setTimeout(() => terminar(), ESPERA_MENU_MS);
    function terminar() {
        vigia.disconnect();
        clearTimeout(caducidad);
    }
    vigia.observe(disparador, { attributes: true, attributeFilter: ['aria-expanded'] });
}

function ejecutar(elemento) {
    if (elemento.hasAttribute('data-keytip-grupo')) {
        estado.nivel = { tipo: 'grupo', elemento };
        pintar();
        return;
    }

    // Un campo o un desplegable no se «pulsan»: su letra les da el foco.
    if (esCampoDeTexto(elemento) || elemento.tagName === 'SELECT') {
        apagar();
        elemento.focus();
        elemento.select?.();
        return;
    }

    if (elemento.getAttribute('aria-haspopup') === 'menu') {
        // El menú lo abre el servidor (Blazor): el nivel baja ya, y las letras de sus opciones
        // aparecen cuando el panel llega (el observador repinta). Si no llega, se apaga.
        estado.nivel = { tipo: 'menu', disparador: elemento };
        estado.mapa = new Map();
        estado.capa?.replaceChildren();
        if (elemento.getAttribute('aria-expanded') !== 'true') elemento.click();
        cancelarEsperaMenu();
        estado.esperaMenu = setTimeout(() => {
            estado.esperaMenu = null;
            if (estado.encendido && estado.nivel.tipo === 'menu' && elemento.getAttribute('aria-expanded') !== 'true') apagar();
        }, ESPERA_MENU_MS);
        programarRepintado();
        return;
    }

    // Dentro de un grupo (la franja de estado, «Agrupar») elegir no saca del modo: se puede
    // encadenar otra opción. En la raíz y en un menú, ejecutar apaga.
    const sigueEnGrupo = estado.nivel.tipo === 'grupo';
    if (!sigueEnGrupo) apagar();
    elemento.click();
    if (sigueEnGrupo) programarRepintado();
}

function alPulsar(evento) {
    if (evento.key === 'Alt') {
        if (esCampoDeTexto(document.activeElement)) {
            estado.altLimpia = false;
            return;
        }
        if (!evento.repeat) estado.altLimpia = !evento.ctrlKey && !evento.metaKey && !evento.shiftKey;
        evento.preventDefault();
        return;
    }

    estado.altLimpia = false;
    if (!estado.encendido) return;

    // Una combinación (Ctrl+K, Alt+←…) o una tecla de navegación no es de KeyTips: apaga y
    // deja que siga su curso.
    const esLetra = evento.key.length === 1 && !evento.ctrlKey && !evento.metaKey && !evento.altKey;
    if (!esLetra && evento.key !== 'Escape' && evento.key !== 'Backspace') {
        if (evento.key !== 'Shift' && evento.key !== 'Control' && evento.key !== 'Meta') apagar();
        return;
    }

    evento.preventDefault();
    evento.stopImmediatePropagation();

    if (evento.key === 'Escape') {
        cerrarMenuDelNivel();
        apagar();
        return;
    }

    if (evento.key === 'Backspace') {
        if (estado.nivel.tipo === 'raiz') return;
        cerrarMenuDelNivel();
        cancelarEsperaMenu();
        estado.nivel = { tipo: 'raiz' };
        pintar();
        return;
    }

    const elemento = estado.mapa.get(evento.key.toUpperCase());
    if (elemento?.isConnected) ejecutar(elemento);
}

function alSoltar(evento) {
    if (evento.key !== 'Alt') return;
    const limpia = estado.altLimpia;
    estado.altLimpia = false;
    if (esCampoDeTexto(document.activeElement)) return;
    evento.preventDefault();
    if (!limpia) return;
    if (estado.encendido) {
        cerrarMenuDelNivel();
        apagar();
    } else {
        encender();
    }
}

function alPulsarRaton() {
    estado.altLimpia = false;
    apagar();
}

function alPerderFoco() {
    estado.altLimpia = false;
    apagar();
}

// Entrada sin teclado (botón «Mostrar las letras» de la chuleta): espera a que el diálogo que
// lo aloja termine de cerrarse antes de encender.
export function encenderKeyTips(intentos = 30) {
    if (hayDialogoModalAbierto() && ambito() === null && intentos > 0) {
        requestAnimationFrame(() => encenderKeyTips(intentos - 1));
        return;
    }
    // El botón tenía el foco; al cerrarse el diálogo puede quedar en un campo. Las letras no
    // dependen del foco, pero encender() no arranca dentro de un campo de texto.
    if (esCampoDeTexto(document.activeElement)) document.activeElement.blur();
    encender();
}

export function registrarKeyTips(textos) {
    Object.assign(estado.textos, textos);
    const generacion = ++estado.generacion;
    asegurarCapa();

    document.addEventListener('keydown', alPulsar, true);
    document.addEventListener('keyup', alSoltar, true);
    document.addEventListener('mousedown', alPulsarRaton, true);
    window.addEventListener('blur', alPerderFoco);
    window.addEventListener('resize', programarRepintado);
    window.addEventListener('scroll', programarRepintado, true);

    return {
        dispose: () => {
            if (generacion !== estado.generacion) return;
            apagar();
            document.removeEventListener('keydown', alPulsar, true);
            document.removeEventListener('keyup', alSoltar, true);
            document.removeEventListener('mousedown', alPulsarRaton, true);
            window.removeEventListener('blur', alPerderFoco);
            window.removeEventListener('resize', programarRepintado);
            window.removeEventListener('scroll', programarRepintado, true);
            estado.capa?.remove();
            estado.anuncio?.remove();
            estado.capa = null;
            estado.anuncio = null;
        }
    };
}
