using Bunit;
using CaeManager.Application.Common;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La marca de procedencia atribuye un dato al producto. Sin título explícito,
/// el título es «Leído por» y el nombre del producto (<see cref="Marca.Nombre"/>),
/// que sale de TextosComunes: nunca una marca escrita en el componente.
/// </summary>
public class MarcaProcedenciaTests : BunitContext
{
    public MarcaProcedenciaTests() => Services.AddLocalization();

    [Fact]
    public void Sin_titulo_explicito_el_titulo_nombra_al_producto()
    {
        var cut = Render<MarcaProcedencia>(parametros => parametros
            .Add(p => p.Etiqueta, "Fecha leída del PDF, confianza 94%")
            .Add(p => p.Detalle, (RenderFragment)(b => b.AddContent(0, "Confianza 94%"))));

        cut.Find(".ventana-contexto-titulo").TextContent.Should().Be($"Leído por {Marca.Nombre}");
    }

    [Fact]
    public void Un_titulo_explicito_sustituye_al_de_por_defecto()
    {
        var cut = Render<MarcaProcedencia>(parametros => parametros
            .Add(p => p.Titulo, "Detectado automáticamente")
            .Add(p => p.Etiqueta, "Detectado automáticamente — posible solicitud de visita")
            .Add(p => p.Detalle, (RenderFragment)(b => b.AddContent(0, "Confianza 80%"))));

        cut.Find(".ventana-contexto-titulo").TextContent.Should().Be("Detectado automáticamente");
    }
}
