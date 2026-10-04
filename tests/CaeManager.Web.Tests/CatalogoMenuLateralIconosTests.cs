using System.Text.RegularExpressions;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Un icono propio por entrada del menú lateral (mejora de la barra lateral, 2026-10-04): antes se
/// repetían el de Visión de cartera, Solicitudes de cartera, Delegaciones y Estado comercial, el de
/// Mi trabajo y Alertas, y otros. Dos entradas distintas con el mismo glifo se leen como la misma
/// cosa. El catálogo tiene que nombrar un icono que exista (uno desconocido se pinta vacío) y no
/// repetirlo.
/// </summary>
public class CatalogoMenuLateralIconosTests
{
    [Fact]
    public void Cada_enlace_del_menu_tiene_un_icono_propio_que_existe()
    {
        var porIcono = CatalogoMenuLateral.Enlaces
            .GroupBy(e => e.Icono)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(e => e.Id))}")
            .ToList();

        porIcono.Should().BeEmpty("cada entrada del menú lleva su propio icono");

        using var ctx = new BunitContext();
        var sinGlifo = CatalogoMenuLateral.Enlaces
            .Where(e => string.IsNullOrWhiteSpace(
                Regex.Replace(ctx.Render<Icono>(p => p.Add(i => i.Nombre, e.Icono)).Find("svg").InnerHtml, @"\s+", "")))
            .Select(e => $"{e.Id} → {e.Icono}")
            .ToList();

        sinGlifo.Should().BeEmpty("un nombre de icono desconocido se pinta vacío");
    }

    [Fact]
    public void El_enlace_de_equipo_del_Coordinador_no_repite_el_rotulo_de_Administracion()
    {
        var equipo = CatalogoMenuLateral.Enlaces.Single(e => e.Id == "usuarios-equipo");
        var usuarios = CatalogoMenuLateral.Enlaces.Single(e => e.Id == "usuarios");

        equipo.Ruta.Should().Be(usuarios.Ruta, "es la misma pantalla, ya autorizada por su propio rol");
        equipo.Rotulo.Should().Be("Mi equipo");
        usuarios.Rotulo.Should().Be("Usuarios");
    }
}
