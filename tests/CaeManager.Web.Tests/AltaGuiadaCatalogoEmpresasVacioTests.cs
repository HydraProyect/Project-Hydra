using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Paso 2 del asistente de alta guiada: "usar una empresa que ya existe"
/// tiene que distinguir <b>catálogo vacío</b> de <b>catálogo con datos</b>.
///
/// <para>
/// Hasta 2026-09-07 no lo distinguía: con cero empresas se dibujaba el mismo
/// <c>&lt;select&gt;</c>, cuya única opción era "Selecciona una empresa…" —
/// invitando a elegir algo que no existe, y respondiendo "Selecciona una
/// empresa." al pulsar Vincular, que es verdad con catálogo lleno y mentira
/// aquí. El propio mockup lo marcaba como el único hueco de la pantalla.
/// </para>
///
/// <para>
/// El E2E <c>AltaGuiadaTests</c> no puede cubrirlo: recorre el camino feliz
/// sobre un entorno sembrado, donde el catálogo nunca está vacío. Esta es la
/// capa que sí observa la propiedad.
/// </para>
/// </summary>
public class AltaGuiadaCatalogoEmpresasVacioTests : BunitContext
{
    /// <summary>
    /// La página lanza dos consultas distintas por el mismo IMediator, así
    /// que responde por tipo: el catálogo del paso 2, y el alta del Cliente
    /// que hace falta para llegar hasta él.
    /// </summary>
    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<EmpresaSelectorDto> Catalogo { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerEmpresasParaSelectorQuery => Catalogo,
                CrearClienteCommand => (object)Result.Exito(Guid.NewGuid()),
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

    /// <summary>
    /// Renderiza la página y la deja en el paso 2 con la casilla "usar una
    /// empresa que ya existe" marcada. Los campos del paso 1 se dejan vacíos
    /// a propósito: quien decide si el alta prospera es el Command, y aquí
    /// está doblado — validar el paso 1 es competencia de otra capa.
    /// </summary>
    private IRenderedComponent<AltaGuiada> RenderizarEnPaso2ConCatalogo(params EmpresaSelectorDto[] catalogo)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo { Catalogo = catalogo });
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IValidator<CrearClienteCommand>>(_ => new InlineValidator<CrearClienteCommand>());
        Services.AddScoped<IValidator<CrearEmpresaCommand>>(_ => new InlineValidator<CrearEmpresaCommand>());
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());

        var cut = Render<AltaGuiada>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Guardar y continuar a Empresa")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("2. Empresa"));

        cut.Find("input[type=checkbox]").Change(true);
        return cut;
    }

    [Fact]
    public void Sin_ninguna_empresa_dada_de_alta_el_paso_2_no_ofrece_un_desplegable_vacio()
    {
        var cut = RenderizarEnPaso2ConCatalogo();

        cut.Markup.Should().Contain("Todavía no hay ninguna empresa dada de alta",
            "con cero empresas hay que decir que el catálogo entero está vacío, no ofrecer un select donde elegir");
        cut.FindAll("select").Should().BeEmpty(
            "un desplegable cuya única opción es «Selecciona una empresa…» invita a elegir algo que no existe");
    }

    [Fact]
    public void Sin_ninguna_empresa_el_boton_de_vincular_queda_desactivado()
    {
        var cut = RenderizarEnPaso2ConCatalogo();

        var vincular = cut.FindAll("button").First(b => b.TextContent.Contains("Vincular y continuar a Centro"));
        vincular.HasAttribute("disabled").Should().BeTrue(
            "activo llevaría a «Selecciona una empresa.», que es verdad con catálogo lleno y mentira sin ninguna empresa");
    }

    [Fact]
    public void Con_empresas_en_el_catalogo_el_paso_2_sigue_ofreciendo_el_desplegable()
    {
        var cut = RenderizarEnPaso2ConCatalogo(new EmpresaSelectorDto(Guid.NewGuid(), "Montajes Ebro S.L."));

        cut.FindAll("select").Should().NotBeEmpty("con catálogo lleno el desplegable es lo correcto");
        cut.Markup.Should().Contain("Montajes Ebro S.L.");
        cut.Markup.Should().NotContain("Todavía no hay ninguna empresa dada de alta");

        var vincular = cut.FindAll("button").First(b => b.TextContent.Contains("Vincular y continuar a Centro"));
        vincular.HasAttribute("disabled").Should().BeFalse();
    }
}
