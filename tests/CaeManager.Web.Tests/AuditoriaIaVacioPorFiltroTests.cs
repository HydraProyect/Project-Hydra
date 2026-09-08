using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.DocumentosIa.Queries;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Auditoría IA tenía el mismo defecto que <see cref="AuditoriaVacioPorFiltroTests"/>
/// y aquí la lectura equivocada se invierte, que es lo que lo hace caro: con
/// el filtro «Sin proveedor (fallo)» puesto, leer «todavía no se ha procesado
/// ningún documento» sugiere que la IA está parada — cuando ese cero significa
/// justo lo contrario, que no ha habido ni un fallo.
/// </summary>
public class AuditoriaIaVacioPorFiltroTests : BunitContext
{
    private sealed class MediatorConRegistros(IReadOnlyList<RegistroAuditoriaIaDto> registros) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerAuditoriaIaQuery q => new ResultadoPaginado<RegistroAuditoriaIaDto>(
                    registros, registros.Count, q.Pagina, q.TamanoPagina),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <param name="proveedor">Valor del filtro que llega por la URL (?proveedor=).</param>
    private IRenderedComponent<Features.AuditoriaIa.Pages.AuditoriaIa> Renderizar(
        string? proveedor = null, params RegistroAuditoriaIaDto[] registros)
    {
        Services.AddScoped<IMediator>(_ => new MediatorConRegistros(registros));

        // [SupplyParameterFromQuery]: se navega a la URI, no se pasa como parámetro.
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(proveedor is null ? "auditoria-ia" : "auditoria-ia?proveedor=" + Uri.EscapeDataString(proveedor));

        return Render<Features.AuditoriaIa.Pages.AuditoriaIa>();
    }

    [Fact]
    public void Filtrando_por_fallos_un_cero_no_se_lee_como_una_ia_parada()
    {
        var cut = Renderizar(proveedor: "ninguno");

        cut.Markup.Should().Contain("Ningún registro con este filtro");
        cut.Markup.Should().Contain("Quitar el filtro");
        cut.Markup.Should().NotContain("Sin registros de procesamiento IA",
            "cero fallos es una buena noticia, no una IA que no procesa");
    }

    [Fact]
    public void Sin_filtro_y_sin_registros_sigue_diciendo_que_no_hay_ninguno()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Sin registros de procesamiento IA");
        cut.Markup.Should().NotContain("Ningún registro con este filtro");
    }

    [Fact]
    public void El_estado_sin_filtro_ya_no_habla_de_un_filtro_que_no_existe()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Sin registros de procesamiento IA");
        cut.Markup.Should().NotContain("con este filtro");
    }

    [Fact]
    public void Quitar_el_filtro_devuelve_la_pagina_al_estado_sin_filtrar()
    {
        var cut = Renderizar(proveedor: "ninguno");
        cut.Markup.Should().Contain("Ningún registro con este filtro", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        cut.Markup.Should().Contain("Sin registros de procesamiento IA");
        cut.Markup.Should().NotContain("Ningún registro con este filtro");
    }

    [Fact]
    public void Con_registros_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(proveedor: "anthropic", registros: new RegistroAuditoriaIaDto(
            Guid.NewGuid(), "abc123", "Certificado", "anthropic", 1200, null, 0.01m, 3, 92,
            Incidencias: null, DateTime.UtcNow, DocumentoId: null, DecisionHumana: null,
            UsuarioDecisionId: null, FechaDecisionUtc: null));

        cut.Markup.Should().NotContain("Ningún registro con este filtro");
        cut.Markup.Should().NotContain("Sin registros de procesamiento IA");
        cut.Find("table.tabla-datos").TextContent.Should().Contain("Certificado");
    }
}
