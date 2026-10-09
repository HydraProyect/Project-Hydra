using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerProveedoresPlataformaCae;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;
using CaeManager.Domain.Centros;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08), en la vista rápida de un Centro: editar
/// se hace con el lápiz de la cabecera —que está en todas las pestañas— o con la tecla «e» del
/// listado, que llega al panel como una petición de edición de un solo uso. Un rol de Consulta no
/// entra en edición por ninguno de los dos caminos. La cabecera lleva además el icono 360 a la
/// página completa del Centro.
/// </summary>
public class CentroWorkspacePanelLapizTests : BunitContext
{
    private const string Lapiz = "button[aria-label='Editar información del centro']";
    private static readonly Guid Id = Guid.NewGuid();

    private MediadorPorFuncion _mediador = null!;
    private int _cargasQueFallan;
    private readonly Dictionary<Guid, TaskCompletionSource> _cargasRetenidas = [];
    private readonly Dictionary<Guid, string> _otrosCentros = [];
    private readonly List<string> _pestanasPedidas = [];

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class ResolucionProveedorQueNadieDebeTocar : IResolucionProveedorPlataformaCaeService
    {
        private static Exception NoDeberia() => new NotSupportedException("Este test no resuelve proveedores.");
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorUrlAsync(string url, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorDominioCorreoAsync(string email, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Este test no sube archivos.");
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    /// <summary>
    /// El mediador de siempre, salvo que la carga de la ficha de los Centros anotados no responde
    /// hasta que el test la suelta: así dos cargas se cruzan en el orden que el test decide.
    /// </summary>
    private sealed class MediadorQueRetieneCargas(MediadorPorFuncion interior, Dictionary<Guid, TaskCompletionSource> retenidas) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerCentroPorIdQuery q && retenidas.TryGetValue(q.Id, out var puerta))
                await puerta.Task;
            return await interior.Send(request, cancellationToken);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            interior.Send(request, cancellationToken);

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => interior.Send(request, cancellationToken);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            interior.CreateStream(request, cancellationToken);

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            interior.CreateStream(request, cancellationToken);

        public Task Publish(object notification, CancellationToken cancellationToken = default) => interior.Publish(notification, cancellationToken);

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => interior.Publish(notification, cancellationToken);
    }

    private IRenderedComponent<CentroWorkspacePanel> Renderizar(string pestana, string rol = Roles.GestorCae)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(rol);
        Services.AddLocalization();
        var detalle = new CentroDetalleDto(
            Id, Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), "Montajes Ebro S.L.",
            "Centro Logístico Norte", "C-001", null, null, null, Guid.NewGuid());
        _mediador = new MediadorPorFuncion(p => p switch
        {
            ObtenerCentroPorIdQuery when _cargasQueFallan-- > 0 => throw new InvalidOperationException("Fallo simulado de la carga."),
            ObtenerCentroPorIdQuery q when _otrosCentros.TryGetValue(q.Id, out var otro) => detalle with { Id = q.Id, Nombre = otro },
            ObtenerCentroPorIdQuery => detalle,
            ObtenerUltimaReclamacionClienteQuery => null,
            ObtenerLoteReclamacionQuery => Array.Empty<LoteReclamacionClienteDto>(),
            ObtenerEstadoCentroQuery => new EstadoCentroDto(EstadoCentro.Vigente, [], 100),
            ObtenerCanalesGestionDeCentroQuery => (IReadOnlyList<CanalGestionResumenDto>)[],
            ObtenerProveedoresPlataformaCaeQuery => (IReadOnlyList<ProveedorPlataformaCaeListaDto>)[],
            ObtenerDocumentacionRequeridaDeCentroQuery => (IReadOnlyList<DocumentacionRequeridaCentroDto>)[],
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {p.GetType().Name}.")
        });
        Services.AddScoped<IMediator>(_ => new MediadorQueRetieneCargas(_mediador, _cargasRetenidas));
        Services.AddScoped<ToastService>();
        Services.TryAddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IResolucionProveedorPlataformaCaeService, ResolucionProveedorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/centros");

        return Render<CentroWorkspacePanel>(p => p
            .Add(x => x.EntidadId, Id)
            .Add(x => x.PestanaActiva, pestana)
            .Add(x => x.PestanaActivaChanged, EventCallback.Factory.Create<string>(this, _pestanasPedidas.Add)));
    }

    /// <summary>En edición la cabecera ya no ofrece el lápiz: es la señal observable de «entró en edición».</summary>
    private static bool EnEdicion(IRenderedComponent<CentroWorkspacePanel> cut) =>
        cut.FindAll(Lapiz).Count == 0 && cut.FindAll("a.boton-360-pagina").Count == 1;

    [Fact]
    public void La_cabecera_lleva_el_lapiz_y_el_icono_360_a_la_pagina_completa()
    {
        var cut = Renderizar("informacion");

        cut.Find(Lapiz).GetAttribute("title").Should().Be("Editar (e)");
        cut.Find(".workspace-cabecera-entidad a.boton-360-pagina").GetAttribute("href").Should().Be($"/centros/{Id}");
    }

    [Fact]
    public async Task El_lapiz_entra_en_edicion_y_desaparece_mientras_se_edita()
    {
        var cut = Renderizar("informacion");
        cut.FindAll("input").Should().BeEmpty("punto de partida: en lectura, sin campos");

        await cut.Find(Lapiz).ClickAsync(new MouseEventArgs());

        EnEdicion(cut).Should().BeTrue();
        cut.FindAll("input").Should().NotBeEmpty("el formulario de «Información» está a la vista");
        _pestanasPedidas.Should().BeEmpty("ya estaba en «Información»");
    }

    /// <summary>El formulario vive en «Información»: desde otra pestaña, el lápiz lleva primero allí.</summary>
    [Fact]
    public async Task Desde_otra_pestana_el_lapiz_esta_y_lleva_a_Informacion_y_entra_en_edicion()
    {
        var cut = Renderizar("requisitos");

        await cut.Find(Lapiz).ClickAsync(new MouseEventArgs());

        _pestanasPedidas.Should().Equal(["informacion"]);
        EnEdicion(cut).Should().BeTrue();
    }

    [Fact]
    public async Task La_peticion_de_edicion_de_la_tecla_e_equivale_a_pulsar_el_lapiz()
    {
        var cut = Renderizar("informacion");
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Centro, Id, "Centro Logístico Norte"));

        cut.WaitForAssertion(() => EnEdicion(cut).Should().BeTrue("entró en edición"));
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Centro, Id).Should().BeFalse("el panel la consumió");
    }

    /// <summary>La petición puede llegar antes que el panel: la atiende el final de su carga.</summary>
    [Fact]
    public async Task Una_peticion_que_llego_antes_de_montar_el_panel_se_atiende_al_cargar()
    {
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Centro, Id, "Centro Logístico Norte");

        var cut = Renderizar("informacion");

        cut.WaitForAssertion(() => EnEdicion(cut).Should().BeTrue("entró en edición al terminar de cargar"));
    }

    /// <summary>
    /// Una petición de edición no sobrevive a la carga fallida de su ficha: si se quedara
    /// pendiente, un reintento con éxito entraría en edición sin que nadie lo pidiera.
    /// </summary>
    [Fact]
    public async Task Si_la_ficha_no_carga_la_peticion_de_edicion_se_descarta_y_el_reintento_queda_en_lectura()
    {
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Centro, Id, "Centro Logístico Norte");
        _cargasQueFallan = 1;

        var cut = Renderizar("informacion");

        cut.WaitForAssertion(() => cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Reintentar",
            "control positivo: la carga se intentó y falló"));
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Centro, Id).Should().BeFalse("la carga fallida la descartó");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(Lapiz).Should().ContainSingle("la ficha cargó y sigue en lectura"));
    }

    /// <summary>
    /// Dos fichas seguidas (dos «e», o dos clics) cruzan sus cargas: la respuesta de la primera, que
    /// llega tarde, no pisa el detalle de la segunda.
    /// </summary>
    [Fact]
    public async Task La_carga_tardia_de_la_ficha_anterior_no_pisa_la_actual()
    {
        var otro = Guid.NewGuid();
        _otrosCentros[otro] = "Planta Bilbao";
        var puerta = new TaskCompletionSource();
        _cargasRetenidas[Id] = puerta;
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        var cut = Renderizar("informacion");
        cut.FindAll(".workspace-titulo-entidad").Should().BeEmpty("control positivo: la primera ficha sigue cargando");

        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Centro, otro, "Planta Bilbao");
        cut.Render(p => p.Add(x => x.EntidadId, otro));
        cut.WaitForAssertion(() => cut.Find(".workspace-titulo-entidad").TextContent.Should().Be("Planta Bilbao"));
        EnEdicion(cut).Should().BeTrue("la segunda ficha atendió su petición de edición");

        await cut.InvokeAsync(() => puerta.SetResult());

        cut.WaitForAssertion(() => _mediador.Enviadas.OfType<ObtenerCentroPorIdQuery>().Should().HaveCount(2,
            "control positivo: la carga retenida llegó a responder"));
        cut.Find(".workspace-titulo-entidad").TextContent.Should().Be("Planta Bilbao");
        cut.Find(".workspace-cabecera-entidad a.boton-360-pagina").GetAttribute("href").Should().Be($"/centros/{otro}");
    }

    [Fact]
    public async Task Una_peticion_para_otra_ficha_no_pone_esta_en_edicion()
    {
        var cut = Renderizar("informacion");
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Centro, Guid.NewGuid(), "Planta Bilbao"));

        cut.FindAll(Lapiz).Should().ContainSingle();
    }

    [Fact]
    public async Task Un_rol_de_Consulta_no_tiene_lapiz_ni_entra_en_edicion_con_la_tecla_e()
    {
        var cut = Renderizar("informacion", Roles.Consulta);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        cut.Find("a.boton-360-pagina").Should().NotBeNull("control positivo: la cabecera está pintada");
        cut.FindAll(Lapiz).Should().BeEmpty();

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Centro, Id, "Centro Logístico Norte"));

        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Centro, Id).Should().BeFalse(
            "control positivo: la petición llegó al panel, que la consumió sin atenderla");
        cut.FindAll("input").Should().BeEmpty("sigue en lectura: sin campos de edición");
    }
}
