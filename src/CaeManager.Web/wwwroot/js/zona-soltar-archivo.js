// Puente entre arrastrar/soltar (o pegar con Ctrl+V) y el <input type=file>
// que ya usa InputFile de Blazor — nunca leemos bytes en JS ni duplicamos el
// pipeline de subida en C#: solo rellenamos input.files con el FileList
// soltado/pegado y disparamos 'change', así InputFile dispara OnChange
// exactamente igual que si el usuario hubiera usado el selector nativo.
//
// Ctrl+V: un único oyente de 'paste' a nivel de documento mientras haya alguna
// zona montada (se engancha con la primera y se suelta con la última). Antes
// el oyente colgaba de la propia zona, que solo recibe el evento con el foco
// dentro: sin hacer clic antes, Ctrl+V no hacía nada. Con varias zonas
// montadas a la vez (un drawer sobre una página), responde la última montada
// que sea visible, salvo que el foco esté dentro de una zona concreta.
// Con el foco en un campo de texto se ignora: ahí Ctrl+V pega texto.

const zonas = [];
let oyenteDocumento = null;

const TIPOS_DE_ENTRADA_SIN_TEXTO = ['file', 'checkbox', 'radio', 'button', 'submit', 'reset', 'image', 'range', 'color'];

function esCampoDeTexto(elemento) {
    if (!elemento || elemento.nodeType !== 1) return false;
    if (elemento.isContentEditable) return true;
    const etiqueta = elemento.tagName;
    if (etiqueta === 'TEXTAREA' || etiqueta === 'SELECT') return true;
    if (etiqueta === 'INPUT') return !TIPOS_DE_ENTRADA_SIN_TEXTO.includes((elemento.type || 'text').toLowerCase());
    return false;
}

function estaVisible(elemento) {
    return elemento.isConnected && elemento.getClientRects().length > 0;
}

function estaActiva(zona) {
    return !zona.zonaElement.classList.contains('zona-soltar-archivo-deshabilitada') && !zona.inputElement.disabled;
}

function admite(archivo, accept) {
    const permitidos = (accept || '').split(',').map(t => t.trim().toLowerCase()).filter(t => t.length > 0);
    if (permitidos.length === 0) return true;

    const nombre = (archivo.name || '').toLowerCase();
    const tipo = (archivo.type || '').toLowerCase();
    return permitidos.some(permitido => {
        if (permitido.startsWith('.')) return nombre.endsWith(permitido);
        if (permitido.endsWith('/*')) return tipo.startsWith(permitido.slice(0, -1));
        return tipo === permitido;
    });
}

function elegirZonaParaPegar(destino) {
    const contenedora = zonas.find(zona => zona.zonaElement.contains(destino));
    if (contenedora) return estaActiva(contenedora) ? contenedora : null;

    // Fuera de las zonas, un campo de texto se queda con su Ctrl+V.
    if (esCampoDeTexto(destino)) return null;

    // Con un diálogo modal abierto (drawer, modal), solo cuentan las zonas de dentro: pegar con el
    // foco en el cuerpo no puede subir el archivo a la página que queda detrás.
    const modal = document.querySelector('[aria-modal="true"]');
    const candidatas = modal ? zonas.filter(zona => modal.contains(zona.zonaElement)) : zonas;

    // Responde la última visible; si está ocupada (cargando) se ignora el pegado, no se cae a otra zona.
    for (let i = candidatas.length - 1; i >= 0; i--) {
        if (estaVisible(candidatas[i].zonaElement)) return estaActiva(candidatas[i]) ? candidatas[i] : null;
    }
    return null;
}

function entregar(zona, archivos, origen) {
    const { inputElement } = zona;
    const multiple = zona.zonaElement.dataset.multiple === 'true';

    // Un <input> sin 'multiple' no admite varios: se queda con el primero, como el selector nativo.
    if (!multiple && archivos.length > 1) {
        const transferencia = new DataTransfer();
        transferencia.items.add(archivos[0]);
        inputElement.files = transferencia.files;
    } else {
        inputElement.files = archivos;
    }

    zona.dotNet?.invokeMethodAsync('AnunciarOrigen', origen).catch(() => { });
    inputElement.dispatchEvent(new Event('change', { bubbles: true }));
}

function rechazar(zona, motivo, nombre) {
    zona.dotNet?.invokeMethodAsync('RechazarEntrada', motivo, nombre ?? null).catch(() => { });
}

function alPegar(e) {
    if (e.defaultPrevented) return;

    const zona = elegirZonaParaPegar(e.target);
    if (!zona) return;

    const archivos = e.clipboardData && e.clipboardData.files;
    if (!archivos || archivos.length === 0) {
        // Texto, o nada: quien pulsó Ctrl+V sobre la zona espera ver qué pasó.
        rechazar(zona, 'sin-archivo');
        return;
    }

    e.preventDefault();

    const accept = zona.zonaElement.dataset.accept;
    const limite = Number(zona.zonaElement.dataset.maxBytes);
    for (const archivo of archivos) {
        if (!admite(archivo, accept)) {
            rechazar(zona, 'tipo', archivo.name);
            return;
        }
        if (limite > 0 && archivo.size > limite) {
            rechazar(zona, 'tamano', archivo.name);
            return;
        }
    }

    entregar(zona, archivos, 'pegado');
}

export function inicializar(zonaElement, inputElement, dotNet) {
    const zona = { zonaElement, inputElement, dotNet };

    const prevenir = e => {
        e.preventDefault();
        e.stopPropagation();
    };

    const marcarActiva = () => zonaElement.classList.add('zona-soltar-archivo-activa');
    const desmarcarActiva = () => zonaElement.classList.remove('zona-soltar-archivo-activa');

    const manejarDrop = e => {
        prevenir(e);
        desmarcarActiva();
        if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length > 0) {
            entregar(zona, e.dataTransfer.files, 'soltado');
        }
    };

    // «Cambiar» abre el selector desde el propio clic del usuario (un clic enviado al
    // servidor y devuelto ya no cuenta como gesto del usuario en todos los navegadores).
    const manejarClic = e => {
        if (e.target instanceof Element && e.target.closest('[data-zona-cambiar]') && !inputElement.disabled) {
            inputElement.click();
        }
    };

    ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(evento => zonaElement.addEventListener(evento, prevenir));
    zonaElement.addEventListener('dragenter', marcarActiva);
    zonaElement.addEventListener('dragover', marcarActiva);
    zonaElement.addEventListener('dragleave', desmarcarActiva);
    zonaElement.addEventListener('drop', manejarDrop);
    zonaElement.addEventListener('click', manejarClic);

    zonas.push(zona);
    if (!oyenteDocumento) {
        oyenteDocumento = alPegar;
        document.addEventListener('paste', oyenteDocumento);
    }

    return {
        dispose() {
            ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(evento => zonaElement.removeEventListener(evento, prevenir));
            zonaElement.removeEventListener('dragenter', marcarActiva);
            zonaElement.removeEventListener('dragover', marcarActiva);
            zonaElement.removeEventListener('dragleave', desmarcarActiva);
            zonaElement.removeEventListener('drop', manejarDrop);
            zonaElement.removeEventListener('click', manejarClic);

            const posicion = zonas.indexOf(zona);
            if (posicion >= 0) zonas.splice(posicion, 1);
            zona.dotNet = null;
            if (zonas.length === 0 && oyenteDocumento) {
                document.removeEventListener('paste', oyenteDocumento);
                oyenteDocumento = null;
            }
        }
    };
}
