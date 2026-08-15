// Captura del trazo manuscrito de una firma en campo sobre un <canvas>
// HTML5 (ver FirmaEnCampoTab.razor.cs). Puntero, no ratón/touch por
// separado: pointerdown/pointermove/pointerup cubren ratón, dedo y lápiz
// con el mismo código.
//
// El estado "¿ya se avisó del primer trazo?" se guarda en el propio
// elemento (dataset), no en una variable de módulo: el módulo se importa
// una sola vez y se comparte entre reaperturas de la pestaña "Firma", así
// que una variable de módulo arrastraría el estado de un canvas ya
// descartado al canvas nuevo.

let dibujando = false;

function coordenadas(canvas, evento) {
    const rect = canvas.getBoundingClientRect();
    return {
        x: (evento.clientX - rect.left) * (canvas.width / rect.width),
        y: (evento.clientY - rect.top) * (canvas.height / rect.height),
    };
}

export function iniciar(referencia, idCanvas) {
    const canvas = document.getElementById(idCanvas);
    if (!canvas) return;

    canvas.dataset.trazoAvisado = 'false';

    const contexto = canvas.getContext('2d');
    contexto.lineWidth = 2;
    contexto.lineCap = 'round';
    contexto.strokeStyle = '#1a1a1a';

    canvas.addEventListener('pointerdown', evento => {
        dibujando = true;
        canvas.setPointerCapture(evento.pointerId);
        const { x, y } = coordenadas(canvas, evento);
        contexto.beginPath();
        contexto.moveTo(x, y);

        if (canvas.dataset.trazoAvisado !== 'true') {
            canvas.dataset.trazoAvisado = 'true';
            referencia.invokeMethodAsync('MarcarTrazoIniciadoAsync');
        }
    });

    canvas.addEventListener('pointermove', evento => {
        if (!dibujando) return;
        const { x, y } = coordenadas(canvas, evento);
        contexto.lineTo(x, y);
        contexto.stroke();
    });

    const terminarTrazo = () => { dibujando = false; };
    canvas.addEventListener('pointerup', terminarTrazo);
    canvas.addEventListener('pointerleave', terminarTrazo);
    canvas.addEventListener('pointercancel', terminarTrazo);
}

// Firma "escrita": el nombre tecleado se dibuja con la tipografía cursiva
// 'Dancing Script' (ver @font-face en workspace.css) sobre el mismo canvas
// del trazo — de ahí en adelante sigue el mismo camino que un trazo dibujado
// a mano (exportarPng captura el canvas tal cual, sin distinguir origen).
// Autoajuste de tamaño para que un nombre largo no se salga del lienzo.
export async function escribirNombre(idCanvas, texto) {
    const canvas = document.getElementById(idCanvas);
    if (!canvas) return;

    const contexto = canvas.getContext('2d');
    contexto.clearRect(0, 0, canvas.width, canvas.height);
    canvas.dataset.trazoAvisado = 'false';

    const limpio = (texto || '').trim();
    if (!limpio) return;

    try {
        await document.fonts.load("64px 'Dancing Script'");
    } catch {
        // Sin la fuente cargada, el navegador cae a su cursiva por defecto
        // — sigue siendo una firma válida, solo con otro trazo visual.
    }

    const margen = 20;
    const maxAncho = canvas.width - margen * 2;
    contexto.textBaseline = 'middle';
    contexto.textAlign = 'center';
    contexto.fillStyle = '#1a1a1a';

    let tamano = 64;
    do {
        contexto.font = `${tamano}px 'Dancing Script', cursive`;
        tamano -= 2;
    } while (contexto.measureText(limpio).width > maxAncho && tamano > 20);

    contexto.fillText(limpio, canvas.width / 2, canvas.height / 2);
    canvas.dataset.trazoAvisado = 'true';
}

export function exportarPng(idCanvas) {
    const canvas = document.getElementById(idCanvas);
    if (!canvas) return null;

    const dataUrl = canvas.toDataURL('image/png');
    return dataUrl.substring(dataUrl.indexOf(',') + 1);
}

// Coordenadas redondeadas a ~1km de precisión, nunca la posición exacta
// que devuelve el navegador — suficiente para dar contexto ("firmado en la
// obra de Sevilla") sin guardar un dato de geolocalización preciso por
// defecto (RGPD). null si el usuario deniega el permiso o no hay soporte.
export function obtenerUbicacion() {
    return new Promise(resolve => {
        if (!navigator.geolocation) { resolve(null); return; }

        navigator.geolocation.getCurrentPosition(
            posicion => {
                const lat = posicion.coords.latitude.toFixed(2);
                const lon = posicion.coords.longitude.toFixed(2);
                resolve(`${lat}, ${lon}`);
            },
            () => resolve(null),
            { timeout: 5000 }
        );
    });
}

export function limpiar(idCanvas) {
    const canvas = document.getElementById(idCanvas);
    if (!canvas) return;

    canvas.getContext('2d').clearRect(0, 0, canvas.width, canvas.height);
    canvas.dataset.trazoAvisado = 'false';
}
