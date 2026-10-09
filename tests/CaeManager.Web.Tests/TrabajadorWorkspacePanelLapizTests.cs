using Bunit;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Trabajadores.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Patrón de listados (2026-10-08): la fila de /trabajadores no lleva menú y su vista rápida es
/// este panel, que sustituyó a <c>TrabajadorPreviewDrawer</c>. El lápiz de la cabecera está en
/// todas las pestañas, la tecla «e» del listado equivale a pulsarlo, el icono 360 lleva a la
/// página completa y «Información» enseña la documentación base que enseñaba el drawer. Editar
/// acaba en <c>EditarTrabajadorCommand</c>: un rol de Consulta no entra por ninguno de los dos
/// caminos.
/// </summary>
public class TrabajadorWorkspacePanelLapizTests : BunitContext
{
    private const string Lapiz = "button[aria-label='Editar información del trabajador']";
    private static readonly Guid Id = Guid.NewGuid();

    private MediadorPorFuncion _mediador = null!;
    private int _cargasQueFallan;
    private readonly List<string> _pestanasPedidas = [];

    private static TrabajadorDetalleDto Detalle(Guid id, string nombre, string apellidos) =>
        new(id, Guid.NewGuid(), null, "Refrielectric S.A.", nombre, apellidos, "12884021K",
            new DateOnly(1990, 1, 1), null, null, null, null, null, Guid.NewGuid());

    private IRenderedComponent<TrabajadorWorkspacePanel> Renderizar(string pestana, string rol = Roles.GestorCae)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(rol);
        Services.AddLocalization();
        _mediador = new MediadorPorFuncion(p => p switch
        {
            ObtenerTrabajadorPorIdQuery when _cargasQueFallan-- > 0 => throw new InvalidOperationException("Fallo simulado de la carga."),
            ObtenerTrabajadorPorIdQuery => Detalle(Id, "Marco", "Vila"),
            // La documentación base y las pestañas piden lo suyo: aquí no se mide, y cada una
            // pinta su propio error.
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {p.GetType().Name}.")
        });
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.TryAddScoped<ContextWorkspaceService>();

        return Render<TrabajadorWorkspacePanel>(p => p
            .Add(x => x.EntidadId, Id)
            .Add(x => x.PestanaActiva, pestana)
            .Add(x => x.PestanaActivaChanged, EventCallback.Factory.Create<string>(this, _pestanasPedidas.Add)));
    }

    /// <summary>En edición, «Guardar» y «Cancelar» sustituyen al lápiz.</summary>
    private static bool EnEdicion(IRenderedComponent<TrabajadorWorkspacePanel> cut) =>
        cut.FindAll(".workspace-acciones-edicion button").Any(b => b.TextContent.Trim() == "Guardar");

    [Fact]
    public void La_cabecera_lleva_el_lapiz_y_el_icono_360_a_la_pagina_completa()
    {
        var cut = Renderizar("informacion");

        cut.Find(Lapiz).GetAttribute("title").Should().Be("Editar (e)");
        cut.Find("a.boton-360-pagina").GetAttribute("href").Should().Be($"/trabajadores/{Id}");
    }

    /// <summary>Lo que el drawer anterior enseñaba encima de sus pestañas: la documentación base del trabajador.</summary>
    [Fact]
    public void Informacion_ensena_la_documentacion_base_y_la_edicion_la_retira()
    {
        var cut = Renderizar("informacion");

        cut.FindComponent<PanelDocumentacionBase>().Instance.TrabajadorId.Should().Be(Id);

        cut.Find(Lapiz).Click();

        EnEdicion(cut).Should().BeTrue("control positivo: se está editando");
        cut.FindComponents<PanelDocumentacionBase>().Should().BeEmpty("el formulario ocupa la pestaña");
    }

    [Fact]
    public async Task El_lapiz_entra_en_edicion_y_desaparece_mientras_se_edita()
    {
        var cut = Renderizar("informacion");
        EnEdicion(cut).Should().BeFalse("punto de partida: en lectura");

        await cut.Find(Lapiz).ClickAsync(new MouseEventArgs());

        EnEdicion(cut).Should().BeTrue();
        cut.FindAll(Lapiz).Should().BeEmpty("ya se está editando");
        _pestanasPedidas.Should().BeEmpty("ya estaba en «Información»");
    }

    /// <summary>El formulario vive en «Información»: desde otra pestaña, el lápiz lleva primero allí.</summary>
    [Fact]
    public async Task Desde_otra_pestana_el_lapiz_lleva_a_Informacion()
    {
        var cut = Renderizar("vehiculos");

        await cut.Find(Lapiz).ClickAsync(new MouseEventArgs());

        _pestanasPedidas.Should().Equal(["informacion"]);
        cut.FindAll(Lapiz).Should().BeEmpty("la edición quedó abierta para cuando llegue la pestaña");
    }

    [Fact]
    public async Task La_peticion_de_edicion_de_la_tecla_e_equivale_a_pulsar_el_lapiz()
    {
        var cut = Renderizar("informacion");
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Trabajador, Id, "Marco Vila"));

        cut.WaitForAssertion(() => EnEdicion(cut).Should().BeTrue());
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Trabajador, Id).Should().BeFalse("el panel la consumió");
    }

    /// <summary>La petición puede llegar antes que el panel: la atiende el final de su carga.</summary>
    [Fact]
    public async Task Una_peticion_que_llego_antes_de_montar_el_panel_se_atiende_al_cargar()
    {
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Trabajador, Id, "Marco Vila");

        var cut = Renderizar("informacion");

        cut.WaitForAssertion(() => EnEdicion(cut).Should().BeTrue("entró en edición al terminar de cargar"));
    }

    /// <summary>
    /// Una petición de edición no sobrevive a la carga fallida de su ficha: si se quedara
    /// pendiente, un reintento con éxito entraría en edición sin que nadie lo pidiera.
    /// </summary>
    [Fact]
    public async Task Si_la_ficha_no_carga_la_peticion_de_edicion_se_descarta()
    {
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Trabajador, Id, "Marco Vila");
        _cargasQueFallan = 1;

        var cut = Renderizar("informacion");

        cut.WaitForAssertion(() => _mediador.Enviadas.OfType<ObtenerTrabajadorPorIdQuery>().Should().ContainSingle(
            "control positivo: la carga se intentó y falló"));
        cut.FindAll(Lapiz).Should().BeEmpty("control positivo: sin detalle no hay cabecera");
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Trabajador, Id).Should().BeFalse("la carga fallida la descartó");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());

        cut.Find(Lapiz).Should().NotBeNull("control positivo: el reintento cargó la ficha");
        EnEdicion(cut).Should().BeFalse("nadie pidió editar tras el fallo");
    }

    [Fact]
    public async Task Una_peticion_para_otra_ficha_no_pone_esta_en_edicion()
    {
        var cut = Renderizar("informacion");
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Trabajador, Guid.NewGuid(), "Ana Moreno"));

        EnEdicion(cut).Should().BeFalse();
        cut.FindAll(Lapiz).Should().ContainSingle();
    }

    [Fact]
    public async Task Un_rol_de_Consulta_no_tiene_lapiz_ni_entra_en_edicion_con_la_tecla_e()
    {
        var cut = Renderizar("informacion", Roles.Consulta);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        cut.Find("a.boton-360-pagina").Should().NotBeNull("control positivo: la cabecera está pintada");
        cut.FindAll(Lapiz).Should().BeEmpty();

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Trabajador, Id, "Marco Vila"));

        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Trabajador, Id).Should().BeFalse(
            "control positivo: la petición llegó al panel, que la consumió sin atenderla");
        EnEdicion(cut).Should().BeFalse("EditarTrabajadorCommand está denegado a Consulta");
    }

    /// <summary>
    /// La vista rápida de A tarda; mientras tanto se abre la de B, que responde en seguida.
    /// Cuando A llega, el panel sigue siendo de B (la guarda que tenía el drawer anterior).
    /// </summary>
    [Fact]
    public async Task La_respuesta_tardia_de_otra_ficha_no_pisa_la_que_esta_abierta()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        Services.AddLocalization();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var respuestaDeA = new TaskCompletionSource<TrabajadorDetalleDto>();
        Services.AddScoped<IMediator>(_ => new MediadorConRetencion(q =>
            q.Id == a ? respuestaDeA.Task : Task.FromResult(Detalle(b, "Ana", "Moreno"))));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();

        var cut = Render<TrabajadorWorkspacePanel>(p => p.Add(x => x.EntidadId, a).Add(x => x.PestanaActiva, "vehiculos"));
        cut.Render(p => p.Add(x => x.EntidadId, b));
        cut.WaitForAssertion(() => cut.Find(".workspace-titulo-entidad").TextContent.Trim().Should().Be("Ana Moreno"));

        await cut.InvokeAsync(() => respuestaDeA.SetResult(Detalle(a, "Bea", "Alonso")));
        cut.Render();

        cut.Find(".workspace-titulo-entidad").TextContent.Trim().Should().Be("Ana Moreno", "la respuesta de Bea era de otra pregunta");
    }

    private sealed class MediadorConRetencion(Func<ObtenerTrabajadorPorIdQuery, Task<TrabajadorDetalleDto>> detalle) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerTrabajadorPorIdQuery q
                ? (Task<TResponse>)(object)detalle(q)
                : Task.FromException<TResponse>(new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}."));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
