using Bunit;
using CaeManager.Web.Features.Centros.Pages;

namespace CaeManager.Web.Tests;

/// <summary>
/// /centros agrupa por Cliente empresarial y los grupos arrancan contraídos (rediseño de
/// listados, fase 1). Los tests que miran las filas, y no la agrupación, las abren primero.
/// </summary>
internal static class GruposDeCentrosTestExtensions
{
    private const string GrupoCerrado = ".grupo-lista-cabecera[aria-expanded='false']";

    /// <summary>
    /// Abre, uno a uno, todos los grupos contraídos. Se vuelve a buscar tras cada clic: el
    /// repintado deja obsoletos los manejadores de los elementos ya encontrados.
    /// </summary>
    public static IRenderedComponent<Centros> AbrirGruposDeCentros(this IRenderedComponent<Centros> cut)
    {
        for (var vueltas = 0; vueltas < 100 && cut.FindAll(GrupoCerrado).FirstOrDefault() is { } cabecera; vueltas++)
            cabecera.Click();

        return cut;
    }
}
