using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: salir de /empresas con el drawer de alta a medias pregunta antes. Lo que trae
/// la URL (<c>?accion=crear&amp;nombre=</c>) no es un cambio de quien edita, y la
/// navegación del propio formulario al cerrarse (limpiar la URL, «Continuar con el centro») no pregunta.
/// </summary>
public class EmpresasAvisoCambiosSinGuardarTests : BunitContext
{
    private static readonly Guid EmpresaCreadaId = Guid.NewGuid();

    public EmpresasAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
                ObtenerEmpresasQuery q => new ResultadoPaginado<EmpresaListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerAlcanceCeroQuery => false,
                ObtenerCandidatosIncorporacionCarteraQuery => Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>([]),
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[],
                CrearEmpresaCommand => Result.Exito(EmpresaCreadaId),
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
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private (IRenderedComponent<Empresas> Cut, NavigationManager Navegacion) RenderizarConAltaAbierta()
    {
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        Services.AddLocalization();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearEmpresaCommand>>(_ => new InlineValidator<CrearEmpresaCommand>());

        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.NavigateTo("empresas?accion=crear&nombre=Construcciones%20Norte");
        var cut = Render<Empresas>();
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        return (cut, navegacion);
    }

    private static Task EscribirCifAsync(IRenderedComponent<Empresas> cut, string cif) =>
        cut.FindAll(".drawer-panel input").First(i => i.GetAttribute("placeholder") == "CIF, DNI o NIE")
            .InputAsync(new ChangeEventArgs { Value = cif });

    [Fact]
    public async Task Salir_con_el_alta_a_medias_pregunta_y_deja_seguir_editando()
    {
        var (cut, navegacion) = RenderizarConAltaAbierta();
        await EscribirCifAsync(cut, "B12345678");
        var origen = navegacion.Uri;

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));

        navegacion.Uri.Should().Be(origen, "con el alta a medias la navegación se detiene");
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Seguir editando").ClickAsync(new MouseEventArgs());

        cut.FindAll(".modal-contenido").Should().BeEmpty();
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "B12345678");
    }

    [Fact]
    public async Task Salir_y_descartar_navega_al_destino()
    {
        var (cut, navegacion) = RenderizarConAltaAbierta();
        await EscribirCifAsync(cut, "B12345678");

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().EndWith("/trabajadores");
    }

    [Fact]
    public async Task Lo_que_trae_la_URL_no_es_un_cambio()
    {
        var (cut, navegacion) = RenderizarConAltaAbierta();
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "Construcciones Norte",
            "el test necesita que la razón social llegue preseleccionada por la URL");

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));

        navegacion.Uri.Should().EndWith("/trabajadores");
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }

    [Fact]
    public async Task Continuar_con_el_centro_tras_guardar_no_pregunta()
    {
        var (cut, navegacion) = RenderizarConAltaAbierta();
        await EscribirCifAsync(cut, "B12345678");

        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Continuar con el centro").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().Contain($"/centros?accion=crear&empresaId={EmpresaCreadaId}",
            "lo escrito ya está guardado: la navegación del propio formulario no pregunta");
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }

    /// <summary>
    /// Revisión Codex (PR 2): guardar con <c>?accion=crear</c> en la URL cierra el drawer y
    /// limpia la URL en el mismo manejador; esa navegación no pregunta, porque lo escrito ya
    /// está guardado.
    /// </summary>
    [Fact]
    public async Task Guardar_limpia_la_accion_de_la_URL_sin_preguntar()
    {
        var (cut, navegacion) = RenderizarConAltaAbierta();
        await EscribirCifAsync(cut, "B12345678");

        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().NotContain("accion=crear");
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }

    /// <summary>Revisión Codex (PR 2): cancelar a propósito cierra y limpia la URL sin preguntar: no es salir de la página.</summary>
    [Fact]
    public async Task Cancelar_con_cambios_limpia_la_URL_sin_preguntar()
    {
        var (cut, navegacion) = RenderizarConAltaAbierta();
        await EscribirCifAsync(cut, "B12345678");

        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().NotContain("accion=crear");
        cut.FindAll(".modal-contenido").Should().BeEmpty();
        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    /// <summary>
    /// Revisión puente (PR 2): con el alta a medias, el atajo «n» lleva a esta misma página
    /// con <c>?accion=crear</c>; descartar abre un alta nueva y vacía en vez de dejar la
    /// acción por atendida y no abrir nada.
    /// </summary>
    [Fact]
    public async Task Descartar_hacia_la_misma_alta_abre_una_nueva_y_vacia()
    {
        var (cut, navegacion) = RenderizarConAltaAbierta();
        await EscribirCifAsync(cut, "B12345678");

        await cut.InvokeAsync(() => navegacion.NavigateTo("/empresas?accion=crear"));
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        cut.FindComponents<CampoTexto>().Should().NotContain(c => c.Instance.Valor == "B12345678");
    }
}
