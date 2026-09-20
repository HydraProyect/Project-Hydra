using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La casilla de selección múltiple decía «Seleccionar todos» y solo marcaba los
/// de la página actual. Se contradecía con la propia barra de acciones en lote,
/// que ya dice «N seleccionado(s) <b>en esta página</b>».
///
/// <para>
/// Con 20 por página y 200 coincidencias, quien la marcaba y pulsaba «Eliminar
/// seleccionados» borraba 20 creyendo que borraba 200. El daño real estaba
/// acotado porque la barra sí decía la verdad — pero dos rótulos que se
/// contradicen sobre una acción destructiva es exactamente lo que nadie relee.
/// </para>
/// </summary>
public class SeleccionarTodosDiceQueEsLaPaginaTests : BunitContext
{
    public SeleccionarTodosDiceQueEsLaPaginaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<EmpresaListaDto> Pagina { get; init; }
        public required int TotalFiltrado { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
                ObtenerEmpresasQuery q => (object)new ResultadoPaginado<EmpresaListaDto>(
                    Pagina, TotalFiltrado, q.Pagina, q.TamanoPagina),
                _ => throw new NotSupportedException($"Consulta no prevista: {request.GetType().Name}.")
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private static EmpresaListaDto Empresa(string razonSocial) =>
        new(Guid.NewGuid(), razonSocial, "B-48.220.917", DateTime.UtcNow);

    private IRenderedComponent<Empresas> Renderizar(int totalFiltrado, params EmpresaListaDto[] pagina)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo { Pagina = pagina, TotalFiltrado = totalFiltrado });
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearEmpresaCommand>>(_ => new InlineValidator<CrearEmpresaCommand>());

        var cut = Render<Empresas>();

        // La casilla solo existe con la selección múltiple encendida.
        cut.FindAll("button").First(b => b.TextContent.Contains("Selección múltiple")).Click();
        return cut;
    }

    [Fact]
    public void La_casilla_no_promete_seleccionar_todos()
    {
        var cut = Renderizar(totalFiltrado: 2, Empresa("Montajes Ebro S.L."), Empresa("Aislamientos Nervión S.L."));

        cut.Markup.Should().Contain("Seleccionar los de esta página");
        cut.Markup.Should().NotContain("Seleccionar todos",
            "solo marca la página, y la barra de lote ya dice «en esta página»: dos rótulos que se contradicen "
            + "sobre una acción destructiva son peores que uno impreciso");
    }

    [Fact]
    public void Con_mas_coincidencias_que_las_de_la_pagina_dice_cuantas_hay_en_total()
    {
        var cut = Renderizar(totalFiltrado: 137, Empresa("Montajes Ebro S.L."), Empresa("Aislamientos Nervión S.L."));

        cut.Markup.Should().Contain("137 en total con estos filtros",
            "sin el total, nada indica que la selección se queda corta");
    }

    [Fact]
    public void Cuando_la_pagina_ya_es_todo_no_se_dice_ningun_total()
    {
        var cut = Renderizar(totalFiltrado: 2, Empresa("Montajes Ebro S.L."), Empresa("Aislamientos Nervión S.L."));

        cut.Markup.Should().NotContain("en total con estos filtros",
            "con todo en una página el aviso sobraría, y un aviso que sobra se aprende a ignorar");
    }
}
