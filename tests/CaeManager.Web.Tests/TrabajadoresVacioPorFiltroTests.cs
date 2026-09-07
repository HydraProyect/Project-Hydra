using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El mismo defecto que <see cref="EmpresasVacioPorFiltroTests"/> cierra para
/// Empresas, aquí agravado: Trabajadores tiene <b>cuatro</b> filtros —búsqueda,
/// documentación, empresa y subcontrata— y tres de ellos viven dentro de
/// desplegables cerrados, así que ni se ve qué está recortando la lista ni se
/// distingue «aún no hay trabajadores» de «ninguno coincide».
///
/// <para>
/// Ofrecer «crea el primero» a quien acaba de filtrar lo manda a dar de alta un
/// trabajador que probablemente ya existe, con el DNI duplicado que eso
/// arrastra.
/// </para>
/// </summary>
public class TrabajadoresVacioPorFiltroTests : BunitContext
{
    /// <summary>La página importa ./js/atajos-lista.js; ese módulo queda fuera de lo que se observa aquí.</summary>
    public TrabajadoresVacioPorFiltroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid EmpresaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SubcontrataId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>La página lanza cinco consultas distintas al montarse: responde por tipo.</summary>
    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<TrabajadorListaDto> Trabajadores { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
                ObtenerEmpresasParaSelectorQuery => (object)new[] { new EmpresaSelectorDto(EmpresaId, "Montajes Ebro S.L.") },
                ObtenerSubcontratasParaSelectorQuery => new[] { new SubcontrataSelectorDto(SubcontrataId, "Aislamientos Nervión S.L.") },
                ObtenerFiltrosGuardadosQuery => Array.Empty<FiltroGuardadoDto>(),
                ObtenerTrabajadoresQuery q => new ResultadoPaginado<TrabajadorListaDto>(
                    Trabajadores, Trabajadores.Count, q.Pagina, q.TamanoPagina),
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private IRenderedComponent<Trabajadores> Renderizar(string? busqueda = null, string? estado = null,
        params TrabajadorListaDto[] trabajadores)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo { Trabajadores = trabajadores });
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearTrabajadorCommand>>(_ => new InlineValidator<CrearTrabajadorCommand>());

        // Los filtros de URL son [SupplyParameterFromQuery]: se llega a ellos
        // navegando, no pasándolos como parámetros de componente.
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(busqueda)) partes.Add("q=" + Uri.EscapeDataString(busqueda));
        if (!string.IsNullOrWhiteSpace(estado)) partes.Add("estado=" + Uri.EscapeDataString(estado));
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(partes.Count == 0 ? "trabajadores" : "trabajadores?" + string.Join('&', partes));

        return Render<Trabajadores>();
    }

    [Fact]
    public void Sin_resultados_y_con_busqueda_no_invita_a_crear_el_primero()
    {
        var cut = Renderizar(busqueda: "Salas Moreno");

        cut.Markup.Should().Contain("Ningún trabajador con estos filtros");
        cut.Markup.Should().Contain("Quitar los filtros");
        cut.Markup.Should().NotContain("Aún no hay trabajadores",
            "mandar a crear a quien acaba de buscar termina en un DNI duplicado");
    }

    [Fact]
    public void Sin_resultados_y_con_filtro_documental_tambien_lo_distingue()
    {
        var cut = Renderizar(estado: nameof(CaeManager.Domain.Documentos.EstadoDocumento.Vencido));

        cut.Markup.Should().Contain("Ningún trabajador con estos filtros");
        cut.Markup.Should().NotContain("Aún no hay trabajadores");
    }

    [Fact]
    public void La_copia_no_afirma_que_existan_trabajadores_dados_de_alta()
    {
        var cut = Renderizar(busqueda: "Salas Moreno");

        // Barrera primero: sin ella, esta comprobación de ausencia pasaría
        // aunque la rama no se activara nunca.
        cut.Markup.Should().Contain("Ningún trabajador con estos filtros");
        cut.Markup.Should().NotContain("Hay trabajadores dados de alta");
    }

    [Fact]
    public void Sin_filtros_y_sin_registros_sigue_invitando_a_crear_el_primero()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Aún no hay trabajadores");
        cut.Markup.Should().NotContain("Ningún trabajador con estos filtros");
    }

    [Fact]
    public void Los_filtros_activos_se_ven_como_chips()
    {
        var cut = Renderizar(busqueda: "Salas",
            estado: nameof(CaeManager.Domain.Documentos.EstadoDocumento.Vencido));

        var chips = cut.FindAll(".chip-filtro");
        chips.Should().HaveCount(2);
        cut.Markup.Should().Contain("Salas").And.Contain("Vencido");
    }

    [Fact]
    public void Sin_filtros_no_se_pinta_ningun_chip()
    {
        var cut = Renderizar(trabajadores: new TrabajadorListaDto(
            Guid.NewGuid(), "Javier", "Salas Moreno", "12345678Z", "Montajes Ebro S.L."));

        cut.FindAll(".chips-filtros").Should().BeEmpty();
    }

    /// <summary>
    /// Hasta ahora Trabajador 360 solo se alcanzaba abriendo antes la vista
    /// previa. El mockup plantea tres opciones y marca esta como recomendada.
    /// </summary>
    [Fact]
    public void El_menu_de_fila_ofrece_abrir_Trabajador_360()
    {
        var cut = Renderizar(trabajadores: new TrabajadorListaDto(
            Guid.NewGuid(), "Javier", "Salas Moreno", "12345678Z", "Montajes Ebro S.L."));

        // MenuAcciones no pinta sus ítems hasta abrirse: sin el clic, la
        // comprobación siguiente sería verde vacío.
        cut.Find(".menu-acciones-disparador").Click();

        cut.Markup.Should().Contain("Abrir Trabajador 360");
        cut.Markup.Should().Contain("Detalles", "el destino nuevo se suma, no sustituye a la vista previa");
    }
}
