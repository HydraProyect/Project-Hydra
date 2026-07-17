// Aplica la preferencia de tema del usuario sobre <html data-theme="...">.
// "sistema" quita el atributo por completo para que gane el
// @media (prefers-color-scheme) de tokens.css — es el comportamiento que
// ya tenía la app antes de existir este selector.
export function aplicarTema(tema) {
    if (tema === 'claro' || tema === 'oscuro') {
        document.documentElement.setAttribute('data-theme', tema);
    } else {
        document.documentElement.removeAttribute('data-theme');
    }
}
