using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Ficha por Id cuya consulta devolvió null en el Tenant activo (contrato del selector de Tenant, § 4.5, I15).
/// Caso de la demo: el Gestor CAE abre una ficha en la Empresa gestionada A, cambia a B en el selector de la
/// cabecera y pulsa «Atrás»; la URL vuelve a la ficha de A. Lo que se prueba es que, con selector, se vuelve a
/// la lista con replace y un toast que nombra la empresa ACTIVA; sin selector (un solo Tenant) se queda un
/// estado vacío con «Ir a la lista»; y que nunca hay «Reintentar». El componente no conoce la entidad: su
/// salida depende solo de la lista autorizada del usuario y de la URL (sin oráculo de existencia).
/// </summary>
public class FichaNoEncontradaTests : BunitContext
{
    private static readonly Guid Origen = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid Norte = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
    private static readonly Guid Sur = Guid.Parse("dddddddd-0000-0000-0000-000000000003");

    private const string Ficha = "trabajadores/aaaaaaaa-0000-0000-0000-000000000001";

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    private sealed class Mediador(Func<IReadOnlyList<ClienteAutorizadoDto>> lista) : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return request is ObtenerClientesAutorizadosQuery
                ? Task.FromResult((T)(object)lista())
                : throw new NotSupportedException(request.GetType().Name);
        }

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    private static readonly IReadOnlyList<ClienteAutorizadoDto> Cartera =
    [
        new(Origen, "Operador de prueba", EsOrigen: true),
        new(Norte, "Empresa Norte", EsOrigen: false, EsGestionadoPorOperacion: true),
        new(Sur, "Empresa Sur", EsOrigen: false, EsGestionadoPorOperacion: true),
    ];

    private static readonly IReadOnlyList<ClienteAutorizadoDto> UnSoloTenant =
    [
        new(Origen, "Empresa única", EsOrigen: true),
    ];

    private Func<IReadOnlyList<ClienteAutorizadoDto>> _lista = () => Cartera;
    private readonly Mediador _mediador;
    private readonly ToastService _toasts = new();

    public FichaNoEncontradaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        _mediador = new Mediador(() => _lista());
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        Services.AddSingleton(_toasts);
    }

    private BunitNavigationManager Navegacion => Services.GetRequiredService<BunitNavigationManager>();

    private IRenderedComponent<FichaNoEncontrada> Renderizar(string url, Guid activa, string rutaLista = "/trabajadores")
    {
        Services.AddScoped<ITenantActual>(_ => new TenantActualFijo(activa));
        Navegacion.NavigateTo(url);
        return Render<FichaNoEncontrada>(p => p.Add(c => c.RutaLista, rutaLista));
    }

    [Fact]
    public void Con_selector_vuelve_a_la_lista_con_replace_y_un_toast_con_la_empresa_activa()
    {
        var cut = Renderizar(Ficha, activa: Sur);

        cut.WaitForAssertion(() => Navegacion.History.First().Uri.Should().Be("/trabajadores"));
        Navegacion.History.First().Options.ReplaceHistoryEntry.Should().BeTrue(
            "«Atrás» no debe reabrir la ficha que acaba de llevar a la lista");
        _toasts.Mensajes.Should().ContainSingle().Which.Should().Match<ToastMensaje>(t =>
            t.Mensaje == "Esa ficha no está en Empresa Sur; te llevamos a la lista." && t.Tono == TonoToast.Info);
        cut.Markup.Should().NotContain("Reintentar");
    }

    [Fact]
    public void La_redireccion_ocurre_una_sola_vez_aunque_el_componente_se_vuelva_a_pintar()
    {
        var cut = Renderizar(Ficha, activa: Norte);
        cut.WaitForAssertion(() => _toasts.Mensajes.Should().HaveCount(1));

        cut.Render();

        _toasts.Mensajes.Should().HaveCount(1);
        Navegacion.History.Count(h => h.Uri == "/trabajadores").Should().Be(1);
    }

    [Fact]
    public void Sin_selector_un_solo_Tenant_pinta_estado_vacio_con_Ir_a_la_lista_y_no_redirige()
    {
        _lista = () => UnSoloTenant;

        var cut = Renderizar(Ficha, activa: Origen, rutaLista: "/centros");

        cut.WaitForAssertion(() => cut.Find("[data-ficha-no-encontrada=vacio]"));
        cut.Markup.Should().Contain("Esta ficha no está disponible");
        cut.Find("a[href='/centros']").TextContent.Should().Contain("Ir a la lista");
        cut.Markup.Should().NotContain("Reintentar");
        Navegacion.History.Should().NotContain(h => h.Uri == "/centros");
        _toasts.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public void Si_la_lista_autorizada_no_se_puede_leer_no_redirige_y_deja_la_salida_a_la_lista()
    {
        _lista = () => throw new InvalidOperationException("caída");

        var cut = Renderizar(Ficha, activa: Sur);

        cut.WaitForAssertion(() => cut.Find("a[href='/trabajadores']"));
        _toasts.Mensajes.Should().BeEmpty();
        Navegacion.History.Should().NotContain(h => h.Uri == "/trabajadores");
    }

    [Fact]
    public void Con_una_pista_tenant_autorizada_conserva_Abrir_en_y_no_redirige()
    {
        var cut = Renderizar($"{Ficha}?tenant={Norte}", activa: Sur);

        cut.WaitForAssertion(() => cut.Find("form[data-enlace-otra-empresa]"));
        cut.Find("form[data-enlace-otra-empresa]").TextContent.Should().Contain("Abrir en Empresa Norte");
        Navegacion.History.Should().NotContain(h => h.Uri == "/trabajadores");
        _toasts.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public void Una_pista_igual_a_la_empresa_activa_no_ofrece_Abrir_en_y_redirige()
    {
        var cut = Renderizar($"{Ficha}?tenant={Sur}", activa: Sur);

        cut.WaitForAssertion(() => Navegacion.History.First().Uri.Should().Be("/trabajadores"));
        cut.FindAll("form[data-enlace-otra-empresa]").Should().BeEmpty();
        _toasts.Mensajes.Should().ContainSingle().Which.Mensaje.Should().Contain("Empresa Sur");
    }

    [Fact]
    public void Una_pista_no_autorizada_se_ignora_y_redirige_igual()
    {
        var ajena = Guid.Parse("dddddddd-0000-0000-0000-000000000009");

        var cut = Renderizar($"{Ficha}?tenant={ajena}", activa: Sur);

        cut.WaitForAssertion(() => Navegacion.History.First().Uri.Should().Be("/trabajadores"));
        _toasts.Mensajes.Should().ContainSingle().Which.Mensaje.Should().Contain("Empresa Sur");
    }

    [Fact]
    public void Solo_consulta_la_lista_autorizada_nunca_la_entidad_ni_otro_Tenant()
    {
        var cut = Renderizar(Ficha, activa: Sur);
        cut.WaitForAssertion(() => _toasts.Mensajes.Should().HaveCount(1));

        _mediador.Enviadas.Should().AllBeOfType<ObtenerClientesAutorizadosQuery>(
            "averiguar si la ficha existe en otro Tenant sería un oráculo de existencia");
    }
}

/// <summary>
/// Para las pruebas de cada ficha: con <c>ComponentFactories.AddStub&lt;FichaNoEncontrada&gt;()</c> (o
/// <c>PanelNoEncontrado</c>) la página se prueba sin el comportamiento del componente, que ya cubren
/// <see cref="FichaNoEncontradaTests"/> y <see cref="PanelNoEncontradoTests"/>.
/// </summary>
internal static class FichaNoEncontradaEnPaginas
{
    /// <summary>La lista a la que la ficha pide volver cuando su consulta devolvió null.</summary>
    public static string RutaListaDeFichaNoEncontrada<T>(this IRenderedComponent<T> cut) where T : class, IComponent =>
        cut.FindComponent<Stub<FichaNoEncontrada>>().Instance.Parameters.Get(p => p.RutaLista);

    /// <summary>El panel pintó <see cref="PanelNoEncontrado"/> (se cerrará con un toast).</summary>
    public static bool PintaPanelNoEncontrado<T>(this IRenderedComponent<T> cut) where T : class, IComponent =>
        cut.FindComponents<Stub<PanelNoEncontrado>>().Count == 1;
}

/// <summary>
/// Panel de contexto (?ctx=) cuya consulta por Id devolvió null: se quita ese nivel con un toast, sin «Reintentar».
/// </summary>
public class PanelNoEncontradoTests : BunitContext
{
    private static readonly Guid Origen = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid Sur = Guid.Parse("eeeeeeee-0000-0000-0000-000000000003");

    private sealed class Mediador(Func<IReadOnlyList<ClienteAutorizadoDto>> lista) : IMediator
    {
        public Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default) =>
            request is ObtenerClientesAutorizadosQuery
                ? Task.FromResult((T)(object)lista())
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    private Func<IReadOnlyList<ClienteAutorizadoDto>> _lista = () =>
    [
        new(Origen, "Operador de prueba", EsOrigen: true),
        new(Sur, "Empresa Sur", EsOrigen: false, EsGestionadoPorOperacion: true),
    ];

    private readonly ToastService _toasts = new();
    private readonly ContextWorkspaceService _workspace = new();

    public PanelNoEncontradoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => new Mediador(() => _lista()));
        Services.AddScoped<ITenantActual>(_ => new TenantActualFijo(Sur));
        Services.AddSingleton(_toasts);
        Services.AddSingleton(_workspace);
    }

    [Fact]
    public async Task Cierra_el_panel_con_un_toast_que_nombra_la_empresa_activa()
    {
        await _workspace.AbrirAsync(EntidadWorkspace.Trabajador, Guid.NewGuid(), "Trabajador", "informacion");

        var cut = Render<PanelNoEncontrado>();

        cut.WaitForAssertion(() => _workspace.EstaAbierto.Should().BeFalse());
        _toasts.Mensajes.Should().ContainSingle().Which.Should().Match<ToastMensaje>(t =>
            t.Mensaje == "Esa ficha no está en Empresa Sur." && t.Tono == TonoToast.Info);
        cut.Markup.Should().NotContain("Reintentar");
    }

    [Fact]
    public async Task Sin_selector_cierra_igual_con_un_toast_generico()
    {
        _lista = () => [new(Origen, "Empresa única", EsOrigen: true)];
        await _workspace.AbrirAsync(EntidadWorkspace.Empresa, Guid.NewGuid(), "Empresa", "informacion");

        var cut = Render<PanelNoEncontrado>();

        cut.WaitForAssertion(() => _workspace.EstaAbierto.Should().BeFalse());
        _toasts.Mensajes.Should().ContainSingle().Which.Mensaje.Should().Be("Esa ficha ya no está disponible.");
    }

    [Fact]
    public async Task Si_el_panel_venia_de_otra_ficha_vuelve_a_ella_en_vez_de_cerrarlo_todo()
    {
        var origen = Guid.NewGuid();
        await _workspace.AbrirAsync(EntidadWorkspace.Subcontrata, origen, "Subcontrata", "informacion");
        await _workspace.NavegarAAsync(EntidadWorkspace.Trabajador, Guid.NewGuid(), "Trabajador", "informacion");

        var cut = Render<PanelNoEncontrado>();

        cut.WaitForAssertion(() => _workspace.Pila.Should().ContainSingle().Which.EntidadId.Should().Be(origen));
        _toasts.Mensajes.Should().ContainSingle().Which.Mensaje.Should().Be("Esa ficha no está en Empresa Sur.");
    }
}
