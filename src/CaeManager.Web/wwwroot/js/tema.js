// Aplica la preferencia de tema del usuario sobre <html data-theme="...">.
// "sistema" quita el atributo por completo para que gane el
// @media (prefers-color-scheme) de tokens.css — es el comportamiento que
// ya tenía la app antes de existir este selector.
//
// La navegación "enhanced" de Blazor (la que dispara cualquier clic en el
// menú lateral) vuelve a pedir el HTML de la página al servidor y hace un
// morph del DOM contra ese HTML nuevo — que nunca incluye data-theme,
// porque solo lo pone este módulo en el cliente — así que el morph lo borra
// de <html> en cada navegación. SelectorTema.razor vive en el layout
// persistente y sobrevive la navegación, así que su OnAfterRenderAsync con
// firstRender=true no vuelve a dispararse para reaplicarlo. Se soluciona
// escuchando el evento 'enhancedload' de Blazor y reaplicando el último
// tema conocido tras cada navegación, sin depender de que el componente C#
// se vuelva a montar.
let temaActual = null;
let escuchandoNavegacion = false;

export function aplicarTema(tema) {
    temaActual = tema;
    aplicarAlDom(tema);

    if (!escuchandoNavegacion && window.Blazor?.addEventListener) {
        window.Blazor.addEventListener('enhancedload', () => aplicarAlDom(temaActual));
        escuchandoNavegacion = true;
    }
}

function aplicarAlDom(tema) {
    if (tema === 'claro' || tema === 'oscuro') {
        document.documentElement.setAttribute('data-theme', tema);
    } else {
        document.documentElement.removeAttribute('data-theme');
    }
}
