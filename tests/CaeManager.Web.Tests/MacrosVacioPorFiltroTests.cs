using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Macros es la excepción del defecto sistémico: <b>su filtro ensancha el
/// resultado en vez de estrecharlo</b>. <c>ObtenerMacrosQuery</c> sin cliente
/// devuelve <b>solo las genéricas</b>; con un cliente devuelve las genéricas
/// <b>más</b> las suyas — está escrito en el XML doc de la propia query, porque
/// el selector de macro al responder una conversación necesita las dos juntas.
///
/// <para>
/// Dos consecuencias que rompen el patrón mecánico de las otras ocho listas:
/// </para>
/// <list type="bullet">
/// <item><b>«Aún no hay macros» era falso sin filtro</b>: sin cliente elegido
/// la pantalla no está viendo las macros específicas de ningún cliente, así que
/// puede haber varias y decir que no hay ninguna es mentira.</item>
/// <item><b>«Quitar el filtro» no es el remedio</b> y por eso esta pantalla no
/// lo ofrece: el sin-filtro es un subconjunto del con-filtro, así que quitarlo
/// enseñaría menos. Es la misma trampa que Visitas — copiar el patrón la habría
/// empeorado.</item>
/// </list>
/// </summary>
public class MacrosVacioPorFiltroTests : BunitContext
{
    private static readonly ClienteSelectorDto ClienteDePrueba =
        new(Guid.NewGuid(), "Refrielectric S.A.");

    /// <summary>
    /// Reproduce la semántica real de <c>ObtenerMacrosQuery</c>: sin cliente,
    /// solo las genéricas; con cliente, las genéricas más las suyas. Un doble
    /// que devolviera siempre lo mismo dejaría este test sin observar lo único
    /// que hace rara a esta pantalla.
    /// </summary>
    private sealed class MediatorConSemanticaDeMacros(IReadOnlyList<MacroListaDto> todas) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerClientesParaSelectorQuery:
                    return Task.FromResult((TResponse)(object)(IReadOnlyList<ClienteSelectorDto>)[ClienteDePrueba]);

                case ObtenerMacrosQuery q:
                    var visibles = q.ClienteId is null
                        ? todas.Where(m => m.ClienteId is null).ToList()
                        : todas.Where(m => m.ClienteId is null || m.ClienteId == q.ClienteId).ToList();
                    return Task.FromResult((TResponse)(object)(IReadOnlyList<MacroListaDto>)visibles);

                default:
                    throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");
            }
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private static MacroListaDto Macro(string titulo, Guid? clienteId = null) =>
        new(Guid.NewGuid(), clienteId, clienteId is null ? null : ClienteDePrueba.RazonSocial,
            titulo, "<p>cuerpo</p>", Guid.NewGuid());

    private IRenderedComponent<Macros> Renderizar(params MacroListaDto[] todas)
    {
        Services.AddScoped<IMediator>(_ => new MediatorConSemanticaDeMacros(todas));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddSingleton<ILogger<Macros>>(_ => NullLogger<Macros>.Instance);

        // El módulo está congelado por defecto salvo configuración: sin esto la
        // página navega a /not-found y el test observaría una pantalla que no es.
        Services.AddSingleton<IOptions<ComunicacionesOptions>>(
            Options.Create(new ComunicacionesOptions { Activo = true }));

        return Render<Macros>();
    }

    /// <summary>El selector de cliente es el único control de la barra.</summary>
    private static void FiltrarPorCliente(IRenderedComponent<Macros> cut)
        => cut.Find(".barra-filtros select").Change(ClienteDePrueba.Id.ToString());

    [Fact]
    public void Sin_cliente_elegido_no_se_afirma_que_no_haya_ninguna_macro()
    {
        // Hay una macro, pero es de un cliente: sin cliente elegido la consulta
        // no la devuelve, y decir "aún no hay macros" sería falso.
        var cut = Renderizar(Macro("Reclamación de TC2", ClienteDePrueba.Id));

        cut.Markup.Should().Contain("No hay macros genéricas");
        cut.Markup.Should().Contain("elige un cliente arriba para ver las suyas");
        cut.Markup.Should().NotContain("Aún no hay macros",
            "hay una macro en el tenant; lo que no hay es ninguna genérica");
    }

    [Fact]
    public void Elegir_ese_cliente_hace_aparecer_su_macro()
    {
        var cut = Renderizar(Macro("Reclamación de TC2", ClienteDePrueba.Id));
        cut.Markup.Should().Contain("No hay macros genéricas", "es el punto de partida de este caso");

        FiltrarPorCliente(cut);

        // La prueba de que el filtro ENSANCHA: el mismo tenant, un filtro más,
        // y ahora sí se ve. En cualquier otra lista esto sería al revés.
        cut.Find("table.tabla-datos").TextContent.Should().Contain("Reclamación de TC2");
        cut.Markup.Should().NotContain("No hay macros genéricas");
    }

    [Fact]
    public void Con_cliente_elegido_y_nada_que_ver_la_copia_afirma_las_dos_ausencias()
    {
        var cut = Renderizar();

        FiltrarPorCliente(cut);

        cut.Markup.Should().Contain("Ni macros genéricas ni de este cliente");
        cut.Markup.Should().NotContain("No hay macros genéricas",
            "con cliente elegido, el vacío dice algo más fuerte: tampoco tiene propias");
    }

    /// <summary>
    /// El estado con cliente elegido <b>no</b> ofrece «quitar el filtro», a
    /// diferencia de las otras ocho listas: el sin-filtro es un subconjunto del
    /// con-filtro, así que quitarlo enseñaría menos. La única acción es crear.
    /// </summary>
    [Fact]
    public void El_vacio_con_cliente_no_ofrece_quitar_el_filtro_porque_ensenaria_menos()
    {
        var cut = Renderizar();

        FiltrarPorCliente(cut);

        cut.Markup.Should().Contain("Ni macros genéricas ni de este cliente", "es la barrera de este caso");
        cut.Find(".estado-vacio button").TextContent.Should().Contain("Nueva macro");
        cut.Markup.Should().NotContain("Quitar el filtro");
        cut.Markup.Should().NotContain("Quitar los filtros");
    }

    [Fact]
    public void Sin_cliente_y_sin_ninguna_macro_sigue_invitando_a_crear_la_primera()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("No hay macros genéricas");
        cut.Find(".estado-vacio button").TextContent.Should().Contain("Nueva macro");
        cut.Markup.Should().NotContain("Ni macros genéricas ni de este cliente");
    }

    [Fact]
    public void Con_una_macro_generica_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(Macro("Saludo estándar"));

        cut.Markup.Should().NotContain("No hay macros genéricas");
        cut.Markup.Should().NotContain("Ni macros genéricas ni de este cliente");
        cut.Find("table.tabla-datos").TextContent.Should().Contain("Saludo estándar");
    }
}
