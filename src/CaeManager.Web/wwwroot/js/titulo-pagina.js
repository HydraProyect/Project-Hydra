// Fija el título de la pestaña desde una página sin prerender. El HeadOutlet del documento es estático:
// el <PageTitle> de una página que solo existe en el circuito no le llega, y la pestaña se quedaba sin título.
export function fijarTitulo(titulo) {
    document.title = titulo;
}
