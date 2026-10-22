// Vega, el asistente IA flotante (marco-talveg.js, 2026-10-21). Lo que Blazor no puede
// hacer sin interop: el atajo global Ctrl/Cmd+I con el foco en cualquier parte, recordar
// abierto o plegado por pestaña (sessionStorage), la pista de primera vez por usuario
// (localStorage: no hay preferencias de usuario en servidor), estrechar el contenido con
// el panel acoplado y devolver el foco al botón flotante al plegar.
//
// La conversación NO se guarda aquí: vive en el circuito (AsistenteIaService). Guardar en
// el navegador lo que se habla con Vega dejaría datos de la Empresa gestionada fuera del
// Tenant.
import { hayDialogoModalAbierto } from './atajos-contexto.js';

const CLAVE_ABIERTO = 'talveg-vega-abierto';
const CLAVE_PISTA = 'talveg-vega-pista';
const ATRIBUTO_ABIERTO = 'data-vega-abierto';

function leer(almacen, clave) {
    try { return almacen.getItem(clave); } catch { return null; }
}

function escribir(almacen, clave, valor) {
    try { almacen.setItem(clave, valor); } catch { /* almacenamiento bloqueado: se sigue sin recordar */ }
}

export function registrarAtajo(dotNetRef) {
    const manejador = (evento) => {
        const esAtajo = (evento.ctrlKey || evento.metaKey) && !evento.altKey && !evento.shiftKey
            && evento.key.toLowerCase() === 'i';
        if (!esAtajo) return;
        if (evento.defaultPrevented || evento.isComposing || hayDialogoModalAbierto()) return;
        // En la pantalla completa de Vega el atajo no pliega nada: Vega ya ocupa la pantalla.
        if (document.querySelector('[data-vega-pantalla]')) return;

        evento.preventDefault();
        dotNetRef.invokeMethodAsync('AlternarDesdeJs');
    };

    document.addEventListener('keydown', manejador);
    return { dispose: () => document.removeEventListener('keydown', manejador) };
}

export function estaAbierto() {
    return leer(sessionStorage, CLAVE_ABIERTO) === '1';
}

export function marcarAbierto(abierto) {
    escribir(sessionStorage, CLAVE_ABIERTO, abierto ? '1' : '0');
    if (abierto) document.documentElement.setAttribute(ATRIBUTO_ABIERTO, '');
    else document.documentElement.removeAttribute(ATRIBUTO_ABIERTO);
}

/** Al plegar, el foco vuelve al botón flotante (que reaparece al quitar el atributo). */
export function enfocarBoton() {
    document.querySelector('.boton-asistente-ia')?.focus();
}

export function enfocar(elemento) {
    elemento?.focus();
}

export function pistaVista(usuario) {
    return leer(localStorage, `${CLAVE_PISTA}:${usuario}`) === '1';
}

export function marcarPistaVista(usuario) {
    escribir(localStorage, `${CLAVE_PISTA}:${usuario}`, '1');
}

/**
 * El campo crece con el texto hasta 120 px. Enter (sin Mayús) no añade la línea: el
 * envío lo hace Blazor con el mismo keydown, y aquí solo se evita el salto de línea.
 */
export function prepararCampo(campo) {
    if (!campo) return { dispose: () => { } };
    const ajustar = () => {
        campo.style.height = 'auto';
        campo.style.height = `${Math.min(campo.scrollHeight, 120)}px`;
    };
    const alTeclear = (evento) => {
        if (evento.key === 'Enter' && !evento.shiftKey && !evento.isComposing) evento.preventDefault();
    };
    campo.addEventListener('input', ajustar);
    campo.addEventListener('keydown', alTeclear);
    return {
        ajustar,
        dispose: () => {
            campo.removeEventListener('input', ajustar);
            campo.removeEventListener('keydown', alTeclear);
        }
    };
}

export function reajustarCampo(campo) {
    if (!campo) return;
    campo.style.height = 'auto';
    campo.style.height = `${Math.min(campo.scrollHeight, 120)}px`;
}
