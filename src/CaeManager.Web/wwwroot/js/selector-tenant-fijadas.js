// Empresas gestionadas fijadas del selector de la barra lateral (entrega de Design del 2026-10-17):
// la estrella fija una empresa arriba, y Alt+1…5 cambia a la 1.ª…5.ª fijada.
//
// LIMITACIÓN CONOCIDA: las fijadas viven en el localStorage del navegador, por usuario y por navegador;
// no viajan con la cuenta. Es la solución provisional que pidió Design hasta que haya dónde guardarlas
// en el servidor. Lo guardado son Ids de Tenant y es solo una preferencia: nunca concede acceso. El
// componente recorta las fijadas al conjunto que el servidor ya autorizó a ese usuario, y cambiar de
// empresa sigue siendo el POST con antiforgery a /cuenta/cliente-activo, que el servidor revalida
// (Asignación de Cartera y de Operación) igual que cuando se elige una fila de la lista.
import { hayDialogoModalAbierto } from './atajos-contexto.js';

const PREFIJO = 'talveg.selector-tenant.fijadas.';
const MAXIMO_ATAJOS = 5;

function leer(clave) {
    try {
        const valor = JSON.parse(localStorage.getItem(PREFIJO + clave) ?? '[]');
        return Array.isArray(valor) ? valor.filter(id => typeof id === 'string') : [];
    } catch {
        // Almacenamiento bloqueado o JSON corrupto: sin fijadas, no un error.
        return [];
    }
}

export function leerFijadas(clave) {
    return leer(clave);
}

export function guardarFijadas(clave, ids) {
    try {
        localStorage.setItem(PREFIJO + clave, JSON.stringify(ids));
    } catch {
        // Cuota agotada o almacenamiento bloqueado: la fijada vale solo hasta recargar.
    }
}

export function registrarAtajos() {
    const manejador = (evento) => {
        // Solo Alt + cifra, sin otro modificador. Se mira el código físico: en macOS Alt+1 produce otro carácter.
        if (!evento.altKey || evento.ctrlKey || evento.metaKey || evento.shiftKey) return;
        const coincidencia = /^(?:Digit|Numpad)([1-5])$/.exec(evento.code);
        if (!coincidencia) return;
        if (evento.defaultPrevented || evento.isComposing || hayDialogoModalAbierto()) return;

        // Ninguna tecla actúa mientras se escribe en un campo.
        const activo = document.activeElement;
        if (activo && (activo.tagName === 'INPUT' || activo.tagName === 'TEXTAREA' || activo.tagName === 'SELECT' || activo.isContentEditable)) return;

        const raiz = document.querySelector('[data-selector-tenant]');
        const formulario = raiz?.querySelector('[data-selector-atajo]');
        if (!raiz || !formulario) return;

        // Las fijadas son las que el componente pinta ahora (data-fijadas: su orden, ya recortadas a lo que el
        // servidor autorizó, sin la organización propia), no una nueva lectura del almacenamiento: así Alt+n va a
        // la empresa que la persona ve en esa posición aunque otra pestaña del navegador haya cambiado la lista.
        // El recorte a data-autorizados se repite por si alguien tocó el atributo en el DOM.
        const autorizados = (raiz.getAttribute('data-autorizados') ?? '').split(',').filter(Boolean);
        const fijadas = (raiz.getAttribute('data-fijadas') ?? '').split(',').filter((id, posicion, todas) => autorizados.includes(id) && todas.indexOf(id) === posicion);
        const destino = fijadas[Number(coincidencia[1]) - 1];
        if (!destino || Number(coincidencia[1]) > MAXIMO_ATAJOS) return;

        evento.preventDefault();
        if (destino === raiz.getAttribute('data-activo')) return;

        const campo = formulario.querySelector('input[name="tenantId"]');
        if (!campo) return;
        campo.value = destino;
        formulario.requestSubmit();
    };

    document.addEventListener('keydown', manejador);
    return { dispose: () => document.removeEventListener('keydown', manejador) };
}
