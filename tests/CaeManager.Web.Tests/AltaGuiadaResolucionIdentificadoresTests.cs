using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// EmpresaId/ClienteId/CentroId llegan a <see cref="AltaGuiada"/> por query
/// string (ver el comentario normativo de la clase) para que el asistente
/// arranque en un paso posterior cuando otra pantalla ya resolvió esa parte.
/// Hasta 2026-09-12 la página se los creía sin preguntar: pintaba
/// "Empresa: [nombre] — Ya creada" para un identificador que ni siquiera se
/// consultaba, con el nombre sacado directamente del propio query string —
/// un identificador ajeno, inexistente o con un nombre inventado en la barra
/// de direcciones se aceptaba igual. Una coordenada de contexto no es
/// autoridad: cada identificador tiene que resolverse contra su Query
/// (<c>ObtenerEmpresaPorIdQuery</c>/<c>ObtenerClientePorIdQuery</c>/
/// <c>ObtenerCentroPorIdQuery</c>, las mismas que ya aplican tenant y
/// cartera) antes de afirmar nada de él.
///
/// <para>
/// Esta suite prueba la mitad que sí observa un test de componente: que sin
/// resolución no se marca el paso como completado ni se pinta "Ya creada", y
/// que el nombre en pantalla es siempre el de la respuesta de la Query,
/// nunca el que viajó en la URL. La otra mitad —que un identificador de OTRO
/// tenant de verdad no resuelve— no la puede demostrar un bUnit, porque el
/// arnés sustituye <c>IAlcanceDatosService</c> por un doble: esa prueba vive
/// en <c>ObtenerPorIdMultiTenantTests</c>, contra Postgres real.
/// </para>
/// </summary>
public class AltaGuiadaResolucionIdentificadoresTests : BunitContext
{
    /// <summary>
    /// Responde por tipo de Query, igual que
    /// <c>AltaGuiadaCatalogoEmpresasVacioTests.MediatorPorTipo</c>: los
    /// catálogos de los pasos de vinculación siempre vacíos (no son el objeto
    /// de esta suite) y las tres consultas de resolución configurables por
    /// test, cada una por defecto "no resuelve" — el mismo comportamiento que
    /// la Query real ante un Id inexistente o ajeno.
    /// </summary>
    private sealed class MediatorDeResolucion : IMediator
    {
        public EmpresaDetalleDto? Empresa { get; init; }
        public ClienteDetalleDto? Cliente { get; init; }
        public CentroDetalleDto? Centro { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? respuesta = request switch
            {
                ObtenerEmpresasParaSelectorQuery => (IReadOnlyList<EmpresaSelectorDto>)[],
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[],
                ObtenerEmpresaPorIdQuery => Empresa,
                ObtenerClientePorIdQuery => Cliente,
                ObtenerCentroPorIdQuery => Centro,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta!);
        }

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
    /// Navega a la ruta con el query string dado y renderiza — los filtros
    /// <c>[SupplyParameterFromQuery]</c> no se pasan como parámetro de
    /// componente (Blazor lo rechaza), se leen de la URI actual del
    /// <see cref="NavigationManager"/> de prueba, igual que
    /// <c>EmpresasVacioPorFiltroTests</c>.
    /// </summary>
    private IRenderedComponent<AltaGuiada> RenderizarConQuery(string query, MediatorDeResolucion mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IValidator<CrearClienteCommand>>(_ => new InlineValidator<CrearClienteCommand>());
        Services.AddScoped<IValidator<CrearEmpresaCommand>>(_ => new InlineValidator<CrearEmpresaCommand>());
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());
        Services.AddScoped<IValidator<CrearTrabajadorCommand>>(_ => new InlineValidator<CrearTrabajadorCommand>());

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(string.IsNullOrEmpty(query) ? "clientes/alta-guiada" : $"clientes/alta-guiada?{query}");

        return Render<AltaGuiada>();
    }

    [Fact]
    public void Un_empresaId_que_no_resuelve_arranca_en_el_paso_1_sin_marcar_nada_completado()
    {
        var empresaIdAjena = Guid.NewGuid();
        var cut = RenderizarConQuery($"empresaId={empresaIdAjena}", new MediatorDeResolucion { Empresa = null });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("1. Empresa"));
        cut.Markup.Should().NotContain("Ya creada",
            "sin resolver el Id no hay nada que afirmar como ya creado: ni existe para este alcance, ni se puede confirmar que sea de otro");
        cut.FindAll(".indicador-pasos-completado").Should().BeEmpty(
            "un Id que no resuelve —inexistente o de otro tenant— no puede dejar un paso marcado como completado");
    }

    [Fact]
    public void Un_empresaId_que_resuelve_pinta_el_nombre_de_la_respuesta_nunca_el_de_la_url()
    {
        var empresaId = Guid.NewGuid();
        var dto = new EmpresaDetalleDto(empresaId, "Empresa Real S.A.", "B10380186", DateTime.UtcNow, [], Guid.NewGuid());

        // "Suplantada" viaja en la URL exactamente como viajaba antes del fix
        // el parámetro empresaNombre — ya no existe esa propiedad, así que
        // Blazor lo ignora, pero el test deja constancia de qué ataque cierra:
        // el nombre en pantalla nunca puede ser este.
        var cut = RenderizarConQuery($"empresaId={empresaId}&empresaNombre=Suplantada", new MediatorDeResolucion { Empresa = dto });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("2. Cliente"));
        cut.Markup.Should().Contain("Empresa Real S.A.", "el nombre pintado es el que devuelve la Query de resolución");
        cut.Markup.Should().Contain("Ya creada");
        cut.Markup.Should().NotContain("Suplantada", "el nombre nunca puede salir del parámetro de la URL");
    }

    [Fact]
    public void Una_empresa_resuelta_con_un_clienteId_ajeno_no_da_por_vinculado_un_cliente_que_no_resolvio()
    {
        var empresaId = Guid.NewGuid();
        var clienteIdAjeno = Guid.NewGuid();
        var dtoEmpresa = new EmpresaDetalleDto(empresaId, "Empresa Real S.A.", null, DateTime.UtcNow, [], Guid.NewGuid());

        var cut = RenderizarConQuery(
            $"empresaId={empresaId}&clienteId={clienteIdAjeno}",
            new MediatorDeResolucion { Empresa = dtoEmpresa, Cliente = null });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("2. Cliente"));
        cut.FindAll(".indicador-pasos-completado").Should().HaveCount(1,
            "solo la Empresa resolvió; un clienteId que no resuelve no puede completar el paso de Cliente");
    }
}
