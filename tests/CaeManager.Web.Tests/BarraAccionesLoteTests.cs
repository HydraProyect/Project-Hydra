using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// REC-127 / DEC-29: el contador debe decir inequívocamente que la
/// selección es de la página actual, no de los resultados filtrados.
/// </summary>
public class BarraAccionesLoteTests : BunitContext
{
    [Fact]
    public void El_contador_en_singular_indica_que_la_seleccion_es_de_esta_pagina()
    {
        var cut = Render<BarraAccionesLote>(parametros => parametros
            .Add(p => p.Cantidad, 1));

        cut.Find(".barra-acciones-lote-cantidad").TextContent
            .Should().Be("1 seleccionado en esta página");
    }

    [Fact]
    public void El_contador_en_plural_indica_que_la_seleccion_es_de_esta_pagina()
    {
        var cut = Render<BarraAccionesLote>(parametros => parametros
            .Add(p => p.Cantidad, 3));

        cut.Find(".barra-acciones-lote-cantidad").TextContent
            .Should().Be("3 seleccionados en esta página");
    }
}
