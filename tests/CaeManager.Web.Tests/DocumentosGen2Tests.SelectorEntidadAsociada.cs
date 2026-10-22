using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerEntidadesAsociadasDeDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class DocumentosGen2Tests
{
    [Fact]
    public async Task El_selector_distingue_el_vacio_inicial_del_vacio_por_busqueda()
    {
        var mediador = new MediadorControlado
        {
            Interceptar = _ => Task.FromResult<object?>(new ResultadoPaginado<EntidadAsociadaDocumentoDto>([], 0, 1, 20))
        };
        Services.AddSingleton<IMediator>(mediador);
        Services.AddLocalization();
        var cut = Render<PastillaEntidadAsociadaDocumento>();
        await cut.Find(".selector-disparador").ClickAsync(new MouseEventArgs());
        cut.Find("[role=status]").TextContent.Should().Be("Todavía no hay propietarios con documentos.");
        await cut.FindComponent<CaeManager.Web.Components.DesignSystem.CampoTexto>().InvokeAsync(() =>
            cut.FindComponent<CaeManager.Web.Components.DesignSystem.CampoTexto>().Instance.ValorChanged.InvokeAsync("inexistente"));
        cut.Find("[role=status]").TextContent.Should().Be("Ningún propietario coincide con la búsqueda.");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Quitar búsqueda").ClickAsync(new MouseEventArgs());
        cut.Find("[role=status]").TextContent.Should().Be("Todavía no hay propietarios con documentos.");
    }

    [Fact]
    public async Task El_selector_consulta_solo_al_abrir_y_elegir_notifica_la_entidad_asociada()
    {
        var entidad = new EntidadAsociadaDocumentoDto(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Empresa de ejemplo");
        var mediador = new MediadorControlado
        {
            Interceptar = _ => Task.FromResult<object?>(new ResultadoPaginado<EntidadAsociadaDocumentoDto>([entidad], 1, 1, 20))
        };
        Services.AddSingleton<IMediator>(mediador);
        Services.AddLocalization();
        EntidadAsociadaDocumentoDto? elegida = null;
        var cut = Render<PastillaEntidadAsociadaDocumento>(p => p.Add(c => c.ValorChanged, e => elegida = e));
        mediador.Enviadas.Should().BeEmpty();
        await cut.Find(".selector-disparador").ClickAsync(new MouseEventArgs());
        mediador.Enviadas.Should().ContainSingle().Which.Should().BeOfType<ObtenerEntidadesAsociadasDeDocumentosQuery>()
            .Which.TamanoPagina.Should().Be(20);
        await cut.FindAll(".selector-opcion").Single(b => b.TextContent.Contains(entidad.Nombre)).ClickAsync(new MouseEventArgs());
        elegida.Should().Be(entidad);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task El_selector_pagina_y_recupera_errores_sin_aplicar_valores()
    {
        var fallo = true;
        var mediador = new MediadorControlado
        {
            Interceptar = q => fallo
                ? Task.FromException<object?>(new InvalidOperationException("Fallo de prueba"))
                : Task.FromResult<object?>(new ResultadoPaginado<EntidadAsociadaDocumentoDto>(
                    [new(Guid.NewGuid(), AmbitoAplicacion.Trabajador, "Persona")], 21,
                    ((ObtenerEntidadesAsociadasDeDocumentosQuery)q).Pagina, 20))
        };
        Services.AddSingleton<IMediator>(mediador);
        Services.AddLocalization();
        var aplicaciones = 0;
        var cut = Render<PastillaEntidadAsociadaDocumento>(p => p.Add(c => c.ValorChanged, _ => aplicaciones++));
        await cut.Find(".selector-disparador").ClickAsync(new MouseEventArgs());
        cut.Find("[role=alert]").TextContent.Should().Contain("No pudimos cargar");
        fallo = false;
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Siguiente").ClickAsync(new MouseEventArgs());
        mediador.Enviadas.OfType<ObtenerEntidadesAsociadasDeDocumentosQuery>().Last().Pagina.Should().Be(2);
        await cut.Find(".selector-entidad-documento").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        aplicaciones.Should().Be(0);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }
}
