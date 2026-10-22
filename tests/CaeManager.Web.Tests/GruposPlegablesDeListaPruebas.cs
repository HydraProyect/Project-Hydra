using Bunit;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// Apoyo de los tests de listados con «Agrupar»: la cabecera de cada grupo es <c>CabeceraGrupoLista</c> (un botón
/// plegable con el nombre y «· N»), y todos los grupos nacen plegados: sus filas no se pintan hasta abrirlos.
/// </summary>
internal static class GruposPlegablesDeListaPruebas
{
    /// <summary>Nombre de cada grupo, en el orden en que se pintan (sin el contador).</summary>
    public static List<string> NombresDeGrupo<TComponente>(this IRenderedComponent<TComponente> cut)
        where TComponente : IComponent =>
        cut.FindAll(".grupo-lista-nombre").Select(e => e.TextContent.Trim()).ToList();

    /// <summary>Recuento de cada grupo, tal como lo dice su cabecera («· 3»).</summary>
    public static List<string> ContadoresDeGrupo<TComponente>(this IRenderedComponent<TComponente> cut)
        where TComponente : IComponent =>
        cut.FindAll(".grupo-lista-contador").Select(e => e.TextContent.Trim()).ToList();

    /// <summary>Título completo de cada grupo: «nombre · N».</summary>
    public static List<string> TitulosDeGrupo<TComponente>(this IRenderedComponent<TComponente> cut)
        where TComponente : IComponent
    {
        var nombres = cut.NombresDeGrupo();
        var contadores = cut.ContadoresDeGrupo();
        return nombres.Select((nombre, i) => $"{nombre} {contadores[i]}").ToList();
    }

    /// <summary>
    /// Abre los grupos que estén plegados. Cada pulsación repinta, así que se vuelve a buscar el botón en vez de
    /// reutilizar elementos viejos.
    /// </summary>
    public static void AbrirGrupos<TComponente>(this IRenderedComponent<TComponente> cut)
        where TComponente : IComponent
    {
        for (var i = 0; i < cut.FindAll("button.grupo-lista-cabecera").Count; i++)
        {
            var boton = cut.FindAll("button.grupo-lista-cabecera")[i];
            if (boton.GetAttribute("aria-expanded") == "false")
                boton.Click();
        }
    }
}
