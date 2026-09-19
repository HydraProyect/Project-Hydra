// Aplica la preferencia de tema del usuario sobre <html data-theme="...">.
// "sistema" quita el atributo por completo para que gane el CSS por defecto
// de tokens.css (claro) — es el comportamiento que ya tenía la app antes de
// existir este selector.
//
// También escribe una cookie de solo lectura para el servidor (ver
// TemaCookie.cs, Web): el HTML prerenderizado no conoce el tema de la
// cuenta hasta que este módulo se importa (después de que el circuito de
// Blazor conecta), así que sin la cookie cada recarga mostraba un parpadeo
// de claro a oscuro. Con la cookie, la SIGUIENTE petición ya sirve
// data-theme correcto desde el propio HTML — este módulo solo necesita
// seguir aplicándolo en vivo para el documento actual y mantener la cookie
// al día. document.cookie, no HttpContext.Response: este componente es
// @rendermode="InteractiveServer" y cambiar el tema no debe generar ninguna
// petición HTTP (REC-137).
export function aplicarTema(tema) {
    aplicarAlDom(tema);
    guardarCookie(tema);
}

function aplicarAlDom(tema) {
    if (tema === 'claro' || tema === 'oscuro') {
        document.documentElement.setAttribute('data-theme', tema);
    } else {
        document.documentElement.removeAttribute('data-theme');
    }
}

function guardarCookie(tema) {
    const segura = location.protocol === 'https:' ? '; Secure' : '';
    if (tema === 'claro' || tema === 'oscuro') {
        document.cookie = `tema=${tema}; Path=/; Max-Age=31536000; SameSite=Lax${segura}`;
    } else {
        // "sistema": server (TemaCookie) también debe dejar de ver un valor
        // explícito, si no el próximo prerender aplicaría un tema que la
        // cuenta ya no tiene elegido.
        document.cookie = `tema=; Path=/; Max-Age=0; SameSite=Lax${segura}`;
    }
}
