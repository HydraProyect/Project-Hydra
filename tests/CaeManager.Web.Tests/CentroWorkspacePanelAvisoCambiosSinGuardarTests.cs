using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Commands.CrearCanalGestion;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerEstadoCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerBorradorPedirPrioridad;
using CaeManager.Application.Contactos;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerProveedoresPlataformaCae;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (lote A): los cuatro drawers del panel de Centro en el Context Workspace
/// (configurar un requisito, acceso de gestión documental, pedir prioridad y reclamar
/// documentación) comparten un solo aviso de cambios sin guardar. Vive fuera de las
/// pestañas del panel, igual que los drawers: cambiar de pestaña no los desmonta y no debe
/// preguntar. La salida a activar el 2FA que decide el propio guardado tampoco pregunta.
/// </summary>
public class CentroWorkspacePanelAvisoCambiosSinGuardarTests : BunitContext
{
    private static readonly Guid ContactoA = Guid.NewGuid();
    private static readonly Guid ContactoB = Guid.NewGuid();

    public CentroWorkspacePanelAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso(CentroDetalleDto detalle) : IMediator
    {
        public bool SinDobleFactor { get; set; }
        public IReadOnlyList<CanalGestionResumenDto> Canales { get; set; } = [];
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (SinDobleFactor && request is IEscrituraDeDatosDeCredencial { EscribeDatosDeCredencial: true })
                return Task.FromException<TResponse>(new SegundoFactorRequeridoParaCredencialesException());

            object? valor = request switch
            {
                ObtenerCentroPorIdQuery => detalle,
                ObtenerUltimaReclamacionClienteQuery => null,
                ObtenerLoteReclamacionQuery => new[]
                {
                    new LoteReclamacionClienteDto(detalle.ClienteId, detalle.ClienteRazonSocial, null,
                        [new DocumentoReclamableDto(Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", Guid.NewGuid(),
                            "Reconocimiento médico", new DateOnly(2026, 10, 1), EstadoDocumento.Proximo)],
                        Destinatarios:
                        [
                            new DestinatarioAgendaDto(ContactoA, "Marta Gil", "marta@example.com", ["Reconocimiento médico"]),
                            new DestinatarioAgendaDto(ContactoB, "Luis Soto", "luis@example.com", ["Reconocimiento médico"]),
                        ])
                },
                ObtenerEstadoCentroQuery => null,
                ObtenerCanalesGestionDeCentroQuery => Canales,
                ObtenerProveedoresPlataformaCaeQuery => (IReadOnlyList<ProveedorPlataformaCaeListaDto>)[],
                ObtenerDocumentacionRequeridaDeCentroQuery => (IReadOnlyList<DocumentacionRequeridaCentroDto>)
                [
                    new DocumentacionRequeridaCentroDto(Guid.NewGuid(), "Reconocimiento médico", AmbitoAplicacion.Trabajador,
                        EsObligatorioGlobal: true, Aplica: true, Incluido: null, PeriodicidadEspecialMeses: null,
                        BloqueaAcceso: false, ArchivoUrl: null, NombreArchivoOriginal: null)
                ],
                ObtenerBorradorPedirPrioridadQuery => Result.Exito(new BorradorPedirPrioridadDto(
                    "validacion@refrielectric.example", "Prioridad de validación", "Os pedimos prioridad.", true, null, 1)),
                CrearCanalGestionCommand => Result.Exito(Guid.NewGuid()),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)valor!);
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
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class ResolucionProveedorQueNadieDebeTocar : IResolucionProveedorPlataformaCaeService
    {
        private static Exception NoDeberia() => new NotSupportedException("Sin URL no debería resolverse ningún proveedor.");
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorUrlAsync(string url, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorDominioCorreoAsync(string email, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    /// <summary>La resolución de la plataforma por URL queda en vuelo hasta que el test la suelta.</summary>
    private sealed class ResolucionProveedorEnEspera : IResolucionProveedorPlataformaCaeService
    {
        public TaskCompletionSource<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> Respuesta { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorUrlAsync(string url, CancellationToken cancellationToken = default) =>
            Respuesta.Task;

        public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorDominioCorreoAsync(string email, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no resuelve por correo.");
    }

    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Este test no sube archivos.");
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private MediatorFalso _mediador = default!;
    private readonly List<string> _pestanasPedidas = [];

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private IResolucionProveedorPlataformaCaeService _resolucion = new ResolucionProveedorQueNadieDebeTocar();
    private IReadOnlyList<CanalGestionResumenDto> _canales = [];

    private IRenderedComponent<CentroWorkspacePanel> Renderizar(string pestana)
    {
        var centroId = Guid.NewGuid();
        _mediador = new MediatorFalso(new CentroDetalleDto(
            centroId, Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), "Montajes Ebro S.L.",
            "Centro Logístico Norte", "C-001", null, null, null, Guid.NewGuid()))
        { Canales = _canales };
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped(_ => _resolucion);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddLocalization();
        Navegacion.NavigateTo("/centros?ficha=norte&pestana=" + pestana);

        var cut = Render<CentroWorkspacePanel>(p => p
            .Add(c => c.EntidadId, centroId)
            .Add(c => c.PestanaActiva, pestana)
            .Add(c => c.PestanaActivaChanged, (string v) => _pestanasPedidas.Add(v)));
        cut.WaitForAssertion(() => cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "Centro Logístico Norte"));
        return cut;
    }

    private static IElement Control(IRenderedComponent<CentroWorkspacePanel> cut, string etiqueta) =>
        cut.Find("#" + cut.FindAll("label").Single(l => l.TextContent.Trim() == etiqueta).GetAttribute("for"));

    private static Task PulsarAsync(IRenderedComponent<CentroWorkspacePanel> cut, string texto) =>
        cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith(texto, StringComparison.Ordinal)).ClickAsync(new MouseEventArgs());

    private async Task<IRenderedComponent<CentroWorkspacePanel>> AbrirPrioridadAsync()
    {
        var cut = Renderizar("informacion");
        await PulsarAsync(cut, "Pedir prioridad");
        cut.WaitForAssertion(() => Control(cut, "Asunto"));
        return cut;
    }

    private async Task<IRenderedComponent<CentroWorkspacePanel>> AbrirReclamacionAsync()
    {
        var cut = Renderizar("informacion");
        await PulsarAsync(cut, "Reclamar documentación (");
        cut.WaitForAssertion(() => cut.FindAll(".reclamacion-destinatarios input[type=checkbox]").Should().HaveCount(2));
        return cut;
    }

    private async Task<IRenderedComponent<CentroWorkspacePanel>> AbrirAltaDeAccesoAsync()
    {
        var cut = Renderizar("plataforma");
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Añadir acceso"));
        await PulsarAsync(cut, "Añadir acceso");
        return cut;
    }

    private async Task<IRenderedComponent<CentroWorkspacePanel>> AbrirConfigurarRequisitoAsync()
    {
        var cut = Renderizar("requisitos");
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Configurar"));
        await PulsarAsync(cut, "Configurar");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Incluido en este centro"));
        return cut;
    }

    [Fact]
    public async Task Pedir_prioridad_con_el_borrador_sin_tocar_no_pregunta()
    {
        var cut = await AbrirPrioridadAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "el borrador que propone la plataforma no es un cambio de quien edita");
    }

    [Fact]
    public async Task Pedir_prioridad_con_el_borrador_editado_pregunta()
    {
        var cut = await AbrirPrioridadAsync();
        await Control(cut, "Asunto").InputAsync(new ChangeEventArgs { Value = "Urgente: prioridad de validación" });
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Reclamar_con_los_destinatarios_de_salida_no_pregunta()
    {
        var cut = await AbrirReclamacionAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "todos marcados de salida es como se abre, no un cambio");
    }

    [Fact]
    public async Task Reclamar_desmarcando_un_destinatario_pregunta()
    {
        var cut = await AbrirReclamacionAsync();
        await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[1].ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Reclamar_desmarcando_y_volviendo_a_marcar_no_es_un_cambio()
    {
        var cut = await AbrirReclamacionAsync();
        await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[1].ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[1].ChangeAsync(new ChangeEventArgs { Value = true });

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la selección vuelve a ser la de partida");
    }

    [Fact]
    public async Task Alta_de_acceso_a_medias_pregunta_al_salir_y_al_cerrar_con_la_X()
    {
        var cut = await AbrirAltaDeAccesoAsync();
        await Control(cut, "Para qué sirve este acceso").InputAsync(new ChangeEventArgs { Value = "Gestión general" });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        await cut.Find(".drawer-panel button.drawer-cerrar").ClickAsync(new MouseEventArgs());
        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
    }

    [Fact]
    public async Task Sin_2FA_la_salida_a_activarlo_tras_guardar_el_acceso_no_pregunta()
    {
        var cut = await AbrirAltaDeAccesoAsync();
        await Control(cut, "Para qué sirve este acceso").InputAsync(new ChangeEventArgs { Value = "Gestión general" });
        await Control(cut, "Usuario").InputAsync(new ChangeEventArgs { Value = "norte.prl" });
        _mediador.SinDobleFactor = true;

        await PulsarAsync(cut, "Guardar");

        _mediador.Enviadas.OfType<CrearCanalGestionCommand>().Should().ContainSingle("barrera: fue el guardado quien pidió el 2FA");
        Navegacion.Uri.Should().Contain("/cuenta/configurar-2fa", "la salida la decide el guardado, no es un abandono");
        cut.FindAll(".modal-pie button").Should().NotContain(b => b.TextContent.Trim() == "Salir y descartar");
    }

    /// <summary>
    /// Deja el alta de un acceso con credenciales lista para guardar sin 2FA, y devuelve los
    /// NavigationLock del panel (el aviso del navegador al recargar o salir de la aplicación
    /// es su ConfirmExternalNavigation, que se decide en el render).
    /// </summary>
    private async Task<(IRenderedComponent<CentroWorkspacePanel> Cut, IReadOnlyList<NavigationLock> Bloqueos)> AltaDeAccesoConCredencialesSin2faAsync()
    {
        var cut = await AbrirAltaDeAccesoAsync();
        await Control(cut, "Para qué sirve este acceso").InputAsync(new ChangeEventArgs { Value = "Gestión general" });
        await Control(cut, "Usuario").InputAsync(new ChangeEventArgs { Value = "norte.prl" });
        _mediador.SinDobleFactor = true;
        var bloqueos = cut.FindComponents<NavigationLock>().Select(c => c.Instance).ToList();
        bloqueos.Should().Contain(b => b.ConfirmExternalNavigation, "barrera: con el alta a medias, el navegador avisa");
        return (cut, bloqueos);
    }

    [Fact]
    public async Task Sin_2FA_el_aviso_del_navegador_ya_esta_apagado_cuando_se_redirige()
    {
        var (cut, bloqueos) = await AltaDeAccesoConCredencialesSin2faAsync();
        bool? avisabaAlRedirigir = null;
        using var registro = Navegacion.RegisterLocationChangingHandler(contexto =>
        {
            if (contexto.TargetLocation.Contains("configurar-2fa", StringComparison.Ordinal))
                avisabaAlRedirigir = bloqueos.Any(b => b.ConfirmExternalNavigation);
            return ValueTask.CompletedTask;
        });

        await PulsarAsync(cut, "Guardar");

        avisabaAlRedirigir.Should().BeFalse(
            "la redirección es forceLoad: si el último render aún pedía confirmación, el navegador la mostraría");
    }

    [Fact]
    public async Task Sin_2FA_si_la_redireccion_no_ocurre_lo_no_guardado_vuelve_a_contar()
    {
        var (cut, bloqueos) = await AltaDeAccesoConCredencialesSin2faAsync();
        using var registro = Navegacion.RegisterLocationChangingHandler(contexto =>
        {
            if (contexto.TargetLocation.Contains("configurar-2fa", StringComparison.Ordinal))
                contexto.PreventNavigation();
            return ValueTask.CompletedTask;
        });

        await PulsarAsync(cut, "Guardar");

        _mediador.Enviadas.OfType<CrearCanalGestionCommand>().Should().ContainSingle("barrera: el guardado se intentó y pidió el 2FA");
        Navegacion.Uri.Should().NotContain("configurar-2fa", "barrera: la redirección no llegó a ocurrir");
        cut.WaitForAssertion(() => bloqueos.Should().Contain(b => b.ConfirmExternalNavigation,
            "el guardado falló: lo escrito sigue sin guardar y el navegador vuelve a avisar"));
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    /// <summary>
    /// Editar un acceso de plataforma sin plataforma resuelta: la resolución por la URL
    /// guardada se hace con el drawer ya abierto y editable. Deja el acceso con la resolución
    /// en vuelo y devuelve con qué resolverla.
    /// </summary>
    private async Task<(IRenderedComponent<CentroWorkspacePanel> Cut, ResolucionProveedorEnEspera Resolucion)> EditarAccesoSinPlataformaAsync()
    {
        var resolucion = new ResolucionProveedorEnEspera();
        _resolucion = resolucion;
        _canales =
        [
            new CanalGestionResumenDto(Guid.NewGuid(), TipoCanalGestion.Plataforma, "Gestión general", EsPrincipal: true,
                ProveedorPlataformaCaeId: null, ProveedorPlataformaCaeNombre: null, UrlAcceso: "https://portal.example.com/login",
                EmailsDestinatarios: null, NombreContacto: null, Notas: null, TieneCredenciales: false, Version: Guid.NewGuid())
        ];
        var cut = Renderizar("plataforma");
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Editar"));
        // Sin await: el manejador espera a la resolución, que el test suelta después.
        _ = cut.FindAll("button").First(b => b.TextContent.Trim() == "Editar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Control(cut, "Para qué sirve este acceso"));
        return (cut, resolucion);
    }

    private static IReadOnlyList<ProveedorPlataformaCaeCandidatoDto> UnaPlataforma() =>
        [new ProveedorPlataformaCaeCandidatoDto(Guid.NewGuid(), "Portal de ejemplo", Activo: true)];

    [Fact]
    public async Task La_plataforma_resuelta_sola_al_editar_un_acceso_no_es_un_cambio()
    {
        var (cut, resolucion) = await EditarAccesoSinPlataformaAsync();

        resolucion.Respuesta.SetResult(UnaPlataforma());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Plataforma: Portal de ejemplo"));

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la plataforma identificada por la URL guardada no la ha elegido quien edita");
    }

    [Fact]
    public async Task Lo_tecleado_mientras_se_resuelve_la_plataforma_si_es_un_cambio()
    {
        var (cut, resolucion) = await EditarAccesoSinPlataformaAsync();
        await Control(cut, "Para qué sirve este acceso").InputAsync(new ChangeEventArgs { Value = "Trabajadores extranjeros" });

        resolucion.Respuesta.SetResult(UnaPlataforma());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Plataforma: Portal de ejemplo"));

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Configurar_un_requisito_sin_tocar_nada_no_pregunta()
    {
        var cut = await AbrirConfigurarRequisitoAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "abrir la configuración no es cambiarla");
    }

    [Fact]
    public async Task Configurar_un_requisito_cambiando_una_casilla_pregunta()
    {
        var cut = await AbrirConfigurarRequisitoAsync();
        var bloquea = cut.FindAll("label.campo-checkbox").Single(l => l.TextContent.Contains("Bloquea el acceso")).QuerySelector("input")!;
        await bloquea.ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Cambiar_de_pestana_con_un_drawer_a_medias_no_pregunta()
    {
        var cut = await AbrirPrioridadAsync();
        await Control(cut, "Asunto").InputAsync(new ChangeEventArgs { Value = "Urgente" });

        // Sin await: si el aviso se registrara en el ámbito de las pestañas, el clic se quedaría
        // esperando una respuesta a la pregunta; así el fallo es rojo y no un cuelgue.
        _ = cut.FindAll("button[role=tab]").Single(b => b.TextContent.Trim() == "Historial").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => _pestanasPedidas.Should().Equal(["historial"],
            "los drawers del panel sobreviven al cambio de pestaña: su aviso no se registra en el ámbito de las pestañas"));
        cut.FindAll(".modal-pie button").Should().NotContain(b => b.TextContent.Trim() == "Salir y descartar");
    }
}
