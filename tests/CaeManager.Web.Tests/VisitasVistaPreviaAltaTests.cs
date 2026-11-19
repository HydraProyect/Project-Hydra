using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Centros.Queries.ObtenerTrabajadoresAsignadosDeCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Visitas.AltaDeVisita;
using CaeManager.Application.Visitas.Commands.CrearVisita;
using CaeManager.Application.Visitas.Queries.ObtenerPlanAltaVisita;
using CaeManager.Application.Visitas.Queries.ObtenerVisitaPorId;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Visitas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Vista previa de «Nueva visita» (decisión de Chris, 2026-11-19): el servidor dice qué Trabajadores quedarán asignados al
/// Centro y qué Gestiones nacerán, y crear la Visita es la única confirmación. El doble del mediador guarda el
/// <see cref="CrearVisitaCommand"/> que recibe: lo que el formulario promete y lo que envía tienen que coincidir.
/// </summary>
public class VisitasVistaPreviaAltaTests : BunitContext
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();
    private static readonly Guid Centro = Guid.NewGuid();
    private static readonly Guid OtroCentro = Guid.NewGuid();
    private static readonly Guid Trabajador = Guid.NewGuid();
    private static readonly Guid TipoPrl = Guid.NewGuid();
    private static readonly Guid TipoMedico = Guid.NewGuid();

    public VisitasVistaPreviaAltaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private sealed class MediatorAlta : IMediator
    {
        public PlanAltaVisitaPreviaDto? Previa { get; set; }
        public List<ObtenerPlanAltaVisitaQuery> ConsultasPlan { get; } = [];
        public List<CrearVisitaCommand> Creaciones { get; } = [];
        public VisitaListaDto? FilaExistente { get; set; }

        /// <summary>Hace que la consulta de la vista previa lance, como una caída del servidor.</summary>
        public bool FallaLaConsultaDelPlan { get; set; }

        /// <summary>Si se informa, crear la Visita devuelve este rechazo del servidor.</summary>
        public Error? RechazoAlCrear { get; set; }

        /// <summary>La vista previa que responde el servidor una vez que ya se ha intentado crear (lo que cambió mientras tanto).</summary>
        public PlanAltaVisitaPreviaDto? PreviaTrasCrear { get; set; }

        /// <summary>Respuestas de la vista previa retenidas por número de consulta (1 = la primera) hasta que el test las libere.</summary>
        public Dictionary<int, TaskCompletionSource<PlanAltaVisitaPreviaDto?>> PlanRetenido { get; } = [];

        /// <summary>Si se informa, la lista de asignados de ese Centro no responde hasta que el test la libere.</summary>
        public (Guid CentroId, TaskCompletionSource<bool> Puerta)? AsignadosRetenidos { get; set; }

        private static Task<T> Respuesta<T>(object? valor) => Task.FromResult((T)valor!);

        private static async Task<T> EsperarPlan<T>(Task<PlanAltaVisitaPreviaDto?> pendiente) => (T)(await pendiente as object)!;

        private static async Task<T> EsperarAsignados<T>(Task puerta, object valor)
        {
            await puerta;
            return (T)valor;
        }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerVisitasQuery consulta:
                    var filas = FilaExistente is null ? new List<VisitaListaDto>() : [FilaExistente];
                    return Respuesta<TResponse>(new ResultadoPaginado<VisitaListaDto>(filas, filas.Count, consulta.Pagina, consulta.TamanoPagina));

                case ObtenerCentrosParaSelectorQuery:
                    return Respuesta<TResponse>((IReadOnlyList<CentroSelectorDto>)[
                        new CentroSelectorDto(Centro, "Planta Norte", "Cliente A", "Empresa A"),
                        new CentroSelectorDto(OtroCentro, "Planta Sur", "Cliente A", "Empresa A")]);

                case ObtenerTrabajadoresParaSelectorQuery:
                    return Respuesta<TResponse>((IReadOnlyList<TrabajadorSelectorDto>)[TrabajadorSelectorFalso.Crear(Trabajador, "Ana García Ruiz")]);

                case ObtenerTrabajadoresAsignadosDeCentroQuery asignados:
                    IReadOnlyList<TrabajadorAsignadoDto> lista = [new TrabajadorAsignadoDto(Trabajador, "x", "y", Hoy)];
                    return AsignadosRetenidos is { } retenidos && retenidos.CentroId == asignados.CentroId
                        ? EsperarAsignados<TResponse>(retenidos.Puerta.Task, lista)
                        : Respuesta<TResponse>(lista);

                case ObtenerVisitaPorIdQuery edicion:
                    return Respuesta<TResponse>(new VisitaDetalleDto(
                        edicion.Id, Centro, "Planta Norte", "Cliente A", "Empresa A", Hoy, Hoy.AddDays(2),
                        TrabajadorIds: [Trabajador], NotificadoCliente: false, Notas: null, HoraEstimadaAcceso: null, Version: Guid.NewGuid()));

                case ObtenerPlanAltaVisitaQuery plan:
                    ConsultasPlan.Add(plan);
                    if (FallaLaConsultaDelPlan)
                        throw new InvalidOperationException("caída simulada de la vista previa");
                    return PlanRetenido.TryGetValue(ConsultasPlan.Count, out var retenido)
                        ? EsperarPlan<TResponse>(retenido.Task)
                        : Respuesta<TResponse>(Creaciones.Count > 0 && PreviaTrasCrear is not null ? PreviaTrasCrear : Previa);

                case CrearVisitaCommand crear:
                    Creaciones.Add(crear);
                    return Respuesta<TResponse>(RechazoAlCrear is { } rechazo ? Result.Fallo<Guid>(rechazo) : Result.Exito(Guid.NewGuid()));

                default:
                    throw new NotSupportedException(request.GetType().Name);
            }
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private static PlanAltaVisitaDto PlanConAsignacionYGestiones() => new(
        [new AsignacionPrevistaDto(Trabajador, "Ana García Ruiz")],
        [
            new GestionPrevistaDto(Trabajador, "Ana García Ruiz", TipoPrl, "Formación PRL", MotivoGestionPrevista.Falta),
            new GestionPrevistaDto(Trabajador, "Ana García Ruiz", TipoMedico, "Reconocimiento médico", MotivoGestionPrevista.CaducaAntesDelFin),
        ],
        Solapadas: []);

    /// <summary>Ana se asignaría y abriría Gestiones, pero Bruno tuvo una Asignación anterior al Centro cuyo periodo coincide con hoy.</summary>
    private static PlanAltaVisitaDto PlanConUnaSolapada() => PlanConAsignacionYGestiones() with
    {
        Solapadas = [new AsignacionPrevistaDto(Guid.NewGuid(), "Bruno Pérez")],
    };

    private IRenderedComponent<Visitas> Renderizar(MediatorAlta mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        return Render<Visitas>();
    }

    /// <summary>Abre «Nueva visita», elige el Centro y marca a la Trabajadora: lo mínimo para pedir la vista previa.</summary>
    private async Task<IRenderedComponent<Visitas>> NuevaVisitaConTrabajadoraAsync(MediatorAlta mediator)
    {
        var cut = Renderizar(mediator);
        await cut.FindAll(".acciones-cabecera button").First(b => b.TextContent.Contains("Nueva visita")).ClickAsync(new MouseEventArgs());
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = Centro.ToString() });
        await cut.Find(".drawer-panel .lista-seleccion-multiple input").ChangeAsync(new ChangeEventArgs { Value = true });
        return cut;
    }

    private static IElement[] BloquesDeVistaPrevia(IRenderedComponent<Visitas> cut) => cut
        .FindAll(".drawer-panel [role=status].vista-previa-alta .alerta-info")
        .ToArray();

    /// <summary>La fecha de fin es el segundo campo de fecha del formulario (el primero es el de inicio).</summary>
    private static Task CambiarFechaFinAsync(IRenderedComponent<Visitas> cut, DateOnly fecha)
    {
        var campo = cut.FindComponents<CampoTexto>().Where(c => c.Instance.Type == "date").ElementAt(1);
        return cut.InvokeAsync(() => campo.Instance.ValorChanged.InvokeAsync(fecha.ToString("yyyy-MM-dd")));
    }

    /// <summary>
    /// Intenta guardar saltándose el botón deshabilitado (que bUnit no deja pulsar): entrega el evento de «Guardar» del kit
    /// directamente al formulario, como haría un doble envío o un repintado tardío. Ejercita la defensa de GuardarAsync.
    /// </summary>
    private static Task IntentarGuardarSaltandoElBotonAsync(IRenderedComponent<Visitas> cut) =>
        cut.InvokeAsync(() => cut.FindComponent<DrawerFormulario>().Instance.OnGuardar.InvokeAsync());

    private static Task GuardarAsync(IRenderedComponent<Visitas> cut) =>
        cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task La_vista_previa_dice_quien_queda_asignado_y_que_gestiones_se_abren()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };

        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        var texto = BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent;
        texto.Should().Contain("Se asignará a este centro a Ana García Ruiz");
        texto.Should().Contain("Se abrirán 2 gestiones:");
        texto.Should().Contain("Formación PRL de Ana García Ruiz: falta");
        texto.Should().Contain("Reconocimiento médico de Ana García Ruiz: caduca antes del fin de la visita");
        texto.Should().NotContainAny("Falta", "CaducaAntesDelFin", "Tenant", "Inbound", "Outbound", "Gestor");
        var consulta = mediator.ConsultasPlan.Should().ContainSingle().Which;
        consulta.CentroId.Should().Be(Centro);
        consulta.FechaFin.Should().Be(Hoy);
        consulta.TrabajadorIds.Should().Equal([Trabajador], "el Centro, la fecha de fin y la selección viajan tal cual");
        cut.Find(".drawer-panel .vista-previa-alta").ClassList.Should().NotContain("vista-previa-alta--vacia", "con aviso la región no se encoge");
    }

    [Fact]
    public async Task Con_plan_y_autoridad_crear_la_visita_pide_asignar_y_crear_las_gestiones()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle().Which.AsignarYCrearGestiones.Should().BeTrue();
    }

    private static IElement BotonGuardar(IRenderedComponent<Visitas> cut) => cut.Find(".drawer-pie .boton-primario");

    private static void GuardarDebeEstarDeshabilitado(IRenderedComponent<Visitas> cut, string motivo)
    {
        var guardar = BotonGuardar(cut);
        guardar.HasAttribute("disabled").Should().BeTrue("la vista previa impide crear la Visita");
        guardar.GetAttribute("title").Should().Be(motivo, "un Guardar deshabilitado dice por qué");
    }

    [Fact]
    public async Task Sin_autoridad_para_asignar_el_aviso_dice_que_hacer_y_Guardar_queda_deshabilitado()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: false) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        var texto = BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent;
        texto.Should().Contain("No tienes permiso para asignar a este centro a Ana García Ruiz").And.Contain("la visita no se puede crear")
            .And.Contain("Quítalos de la visita o pide a quien tenga autoridad que los asigne");
        texto.Should().NotContain("Se abrirán", "sin permiso no se promete nada");
        GuardarDebeEstarDeshabilitado(cut, "Resuelve el aviso de la vista previa: con estos trabajadores la visita no se puede crear.");

        await IntentarGuardarSaltandoElBotonAsync(cut);

        mediator.Creaciones.Should().BeEmpty("la defensa de Guardar tampoco deja crear la Visita sin autoridad");
    }

    [Fact]
    public async Task Con_una_asignacion_solapada_el_aviso_nombra_a_la_persona_dice_que_hacer_y_Guardar_queda_deshabilitado()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConUnaSolapada(), PuedeAsignar: true) };

        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        var texto = BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent;
        texto.Should().Contain("Bruno Pérez no se puede asignar hoy a este centro: tiene una asignación cerrada cuyo periodo coincide con hoy.");
        texto.Should().Contain("La visita no se puede crear así. Quita de la visita a quien no se pueda asignar o revisa su asignación.");
        texto.Should().NotContainAny("Se asignará", "Se abrirá", "Se abrirán", "Tenant", "Inbound", "Outbound", "Gestor");
        GuardarDebeEstarDeshabilitado(cut, "Resuelve el aviso de la vista previa: con estos trabajadores la visita no se puede crear.");

        await IntentarGuardarSaltandoElBotonAsync(cut);

        mediator.Creaciones.Should().BeEmpty("el servidor rechazaría la Visita entera: la pantalla ni lo intenta");
    }

    [Fact]
    public async Task Con_varias_solapadas_el_aviso_las_nombra_a_todas_y_sin_permiso_prima_el_aviso_de_solape()
    {
        var plan = PlanConAsignacionYGestiones() with
        {
            Solapadas = [new AsignacionPrevistaDto(Guid.NewGuid(), "Bruno Pérez"), new AsignacionPrevistaDto(Guid.NewGuid(), "Carla Sanz")],
        };
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(plan, PuedeAsignar: false) };

        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        var texto = BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent;
        texto.Should().Contain("Bruno Pérez no se puede asignar hoy").And.Contain("Carla Sanz no se puede asignar hoy");
        texto.Should().NotContain("No tienes permiso");
        BotonGuardar(cut).HasAttribute("disabled").Should().BeTrue();

        await IntentarGuardarSaltandoElBotonAsync(cut);

        mediator.Creaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_el_plan_vacio_y_valido_no_hay_aviso_y_crear_pide_asignar_y_crear_igualmente()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanAltaVisitaDto.Vacio, PuedeAsignar: true) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        mediator.ConsultasPlan.Should().ContainSingle("se pidió, pero no había nada que decir");
        BloquesDeVistaPrevia(cut).Should().BeEmpty();
        BotonGuardar(cut).HasAttribute("disabled").Should().BeFalse();

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle().Which.AsignarYCrearGestiones.Should().BeTrue("con la vista previa válida se pide siempre; el servidor aplica la misma regla");
    }

    [Fact]
    public async Task Si_el_servidor_no_devuelve_vista_previa_el_aviso_lo_dice_y_Guardar_queda_deshabilitado()
    {
        var mediator = new MediatorAlta { Previa = null };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent
            .Should().Contain("La visita no se puede crear con esta selección")
            .And.Contain("no tienes acceso a este centro").And.Contain("algún trabajador no puede entrar en una visita de este centro")
            .And.Contain("Revisa el centro y los trabajadores elegidos");
        GuardarDebeEstarDeshabilitado(cut, "No se puede guardar sin la vista previa de lo que se asignará.");

        await IntentarGuardarSaltandoElBotonAsync(cut);

        mediator.Creaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_solo_gestiones_el_aviso_dice_una_gestion_en_singular_y_crear_pide_asignar_y_crear()
    {
        var plan = new PlanAltaVisitaDto(
            [], [new GestionPrevistaDto(Trabajador, "Ana García Ruiz", TipoPrl, "Formación PRL", MotivoGestionPrevista.Falta)], Solapadas: []);
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(plan, PuedeAsignar: true) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        var texto = BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent;
        texto.Should().Contain("Se abrirá 1 gestión:").And.Contain("Formación PRL de Ana García Ruiz: falta");
        texto.Should().NotContain("Se asignará", "no hay nadie que asignar");

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle().Which.AsignarYCrearGestiones.Should().BeTrue("hay Gestiones prometidas aunque no haya Asignaciones");
    }

    [Fact]
    public async Task Si_la_consulta_de_la_vista_previa_falla_Guardar_queda_deshabilitado_y_Reintentar_la_vuelve_a_pedir()
    {
        var mediator = new MediatorAlta
        {
            FallaLaConsultaDelPlan = true,
            Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true),
        };

        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        mediator.ConsultasPlan.Should().ContainSingle("se pidió y falló");
        BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent.Should().Contain("No se pudo calcular la vista previa. Inténtalo de nuevo.");
        GuardarDebeEstarDeshabilitado(cut, "No se puede guardar sin la vista previa de lo que se asignará.");

        // Reintentar que vuelve a fallar deja el mismo estado y el botón Reintentar a mano.
        await cut.Find(".vista-previa-alta button").ClickAsync(new MouseEventArgs());
        mediator.ConsultasPlan.Should().HaveCount(2);
        BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent.Should().Contain("No se pudo calcular la vista previa");
        GuardarDebeEstarDeshabilitado(cut, "No se puede guardar sin la vista previa de lo que se asignará.");
        cut.FindAll(".vista-previa-alta button").Should().ContainSingle("Reintentar sigue disponible tras un segundo fallo");

        mediator.FallaLaConsultaDelPlan = false;
        await cut.Find(".vista-previa-alta button").ClickAsync(new MouseEventArgs());

        JSInterop.VerifyFocusAsyncInvoke(2);
        mediator.ConsultasPlan.Should().HaveCount(3, "Reintentar vuelve a pedir la vista previa");
        BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent.Should().Contain("Se asignará a este centro a Ana García Ruiz");
        BotonGuardar(cut).HasAttribute("disabled").Should().BeFalse();

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle().Which.AsignarYCrearGestiones.Should().BeTrue();
    }

    [Fact]
    public async Task Mientras_se_calcula_la_vista_previa_el_aviso_lo_dice_y_Guardar_queda_deshabilitado()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };
        var enVuelo = new TaskCompletionSource<PlanAltaVisitaPreviaDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.PlanRetenido[1] = enVuelo;
        var cut = Renderizar(mediator);
        await cut.FindAll(".acciones-cabecera button").First(b => b.TextContent.Contains("Nueva visita")).ClickAsync(new MouseEventArgs());
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = Centro.ToString() });

        var marcar = cut.Find(".drawer-panel .lista-seleccion-multiple input").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.WaitForAssertion(() => BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent.Should().Contain("Calculando a quién se asignará"));
        GuardarDebeEstarDeshabilitado(cut, "Espera a que termine de calcularse la vista previa.");
        mediator.Creaciones.Should().BeEmpty();

        enVuelo.SetResult(mediator.Previa);
        await marcar;

        BotonGuardar(cut).HasAttribute("disabled").Should().BeFalse("llegó una vista previa válida");
        BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent.Should().Contain("Se asignará");
    }

    [Fact]
    public async Task Con_mas_trabajadores_que_el_tope_el_aviso_dice_el_maximo_y_Guardar_queda_deshabilitado()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };
        var cut = Renderizar(mediator);
        await cut.FindAll(".acciones-cabecera button").First(b => b.TextContent.Contains("Nueva visita")).ClickAsync(new MouseEventArgs());
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = Centro.ToString() });
        var selector = cut.FindComponent<SelectorMultiple>().Instance;
        var tope = CrearAsignacionesCommandValidator.MaximoTrabajadoresPorLote;
        var ids = new List<Guid>();

        for (var i = 0; i <= tope; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await cut.InvokeAsync(() => selector.OnAlternar.InvokeAsync((id, true)));
        }

        var texto = BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent;
        texto.Should().Contain($"Una visita admite como mucho {tope} trabajadores").And.Contain("Quita a algunos de la visita");
        texto.Should().NotContain("asigna", "el aviso no usa la jerga de la regla interna");
        GuardarDebeEstarDeshabilitado(cut, $"Quita trabajadores: una visita admite como mucho {tope}.");
        mediator.ConsultasPlan.Should().HaveCount(tope, "con un Trabajador más que el tope ya no se pide");

        await IntentarGuardarSaltandoElBotonAsync(cut);

        mediator.Creaciones.Should().BeEmpty();

        // Quitando al último Trabajador se vuelve a pedir la vista previa y Guardar se habilita.
        await cut.InvokeAsync(() => selector.OnAlternar.InvokeAsync((ids[^1], false)));

        mediator.ConsultasPlan.Should().HaveCount(tope + 1);
        BotonGuardar(cut).HasAttribute("disabled").Should().BeFalse("con el tope exacto hay vista previa válida");
        BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent.Should().Contain("Se asignará");
    }

    [Fact]
    public async Task Con_la_fecha_de_fin_vacia_no_se_pide_la_vista_previa_ni_se_bloquea_Guardar()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);
        mediator.ConsultasPlan.Should().ContainSingle();

        var campoFin = cut.FindComponents<CampoTexto>().Where(c => c.Instance.Type == "date").ElementAt(1);
        await cut.InvokeAsync(() => campoFin.Instance.ValorChanged.InvokeAsync(string.Empty));

        BloquesDeVistaPrevia(cut).Should().BeEmpty("sin fecha de fin válida no hay nada que calcular ni que enseñar");
        BotonGuardar(cut).HasAttribute("disabled").Should().BeFalse("las validaciones de siempre avisan de la fecha que falta");
    }

    [Fact]
    public async Task Un_rechazo_del_servidor_se_ve_enseguida_mientras_se_recalcula_la_vista_previa()
    {
        var mediator = new MediatorAlta
        {
            Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true),
            RechazoAlCrear = Error.Crear("Visita.SinAutoridadParaAsignar", "No tienes autoridad para asignar."),
        };
        var enVuelo = new TaskCompletionSource<PlanAltaVisitaPreviaDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.PlanRetenido[2] = enVuelo;
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        var guardado = GuardarAsync(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No tienes autoridad para asignar."), TimeSpan.FromSeconds(5));
        mediator.ConsultasPlan.Should().HaveCount(2, "el recálculo sigue en vuelo: el mensaje no esperó a que terminara");
        enVuelo.Task.IsCompleted.Should().BeFalse();

        enVuelo.SetResult(mediator.Previa);
        await guardado;
    }

    [Fact]
    public async Task Al_cambiar_de_centro_la_vista_previa_del_centro_anterior_desaparece_enseguida_y_no_se_envia()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);
        BloquesDeVistaPrevia(cut).Should().ContainSingle();

        // Los Trabajadores asignados del otro Centro tardan en llegar: mientras tanto no puede verse ni enviarse la promesa del Centro anterior.
        var puerta = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.AsignadosRetenidos = (OtroCentro, puerta);
        var cambio = cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = OtroCentro.ToString() });

        cut.WaitForAssertion(() => BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent
            .Should().Contain("Calculando a quién se asignará").And.NotContain("Se asignará a este centro", "la vista previa era de otro Centro"));
        GuardarDebeEstarDeshabilitado(cut, "Espera a que termine de calcularse la vista previa.");
        mediator.Creaciones.Should().BeEmpty("no se puede enviar una promesa que se enseñó para otro Centro");

        puerta.SetResult(true);
        await cambio;

        BotonGuardar(cut).HasAttribute("disabled").Should().BeFalse("ya hay vista previa del Centro nuevo");
        await GuardarAsync(cut);

        var creacion = mediator.Creaciones.Should().ContainSingle().Which;
        creacion.CentroId.Should().Be(OtroCentro);
        creacion.AsignarYCrearGestiones.Should().BeTrue();
    }

    [Fact]
    public async Task Una_respuesta_vieja_de_la_vista_previa_no_pisa_a_la_nueva()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        // La segunda consulta (fecha de fin a 10 días) se queda en vuelo; la tercera (a 20 días) responde antes que ella.
        var vieja = new TaskCompletionSource<PlanAltaVisitaPreviaDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.PlanRetenido[2] = vieja;
        var cambioViejo = CambiarFechaFinAsync(cut, Hoy.AddDays(10));
        cut.WaitForAssertion(() => mediator.ConsultasPlan.Should().HaveCount(2));
        await CambiarFechaFinAsync(cut, Hoy.AddDays(20));

        vieja.SetResult(new PlanAltaVisitaPreviaDto(PlanConUnaSolapada(), PuedeAsignar: true));
        await cambioViejo;

        var texto = BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent;
        texto.Should().Contain("Se asignará").And.NotContain("no se puede asignar hoy", "la respuesta tardía era de una fecha de fin que ya no es la del formulario");

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle().Which.AsignarYCrearGestiones.Should().BeTrue();
    }

    [Fact]
    public async Task Tras_un_rechazo_del_servidor_la_vista_previa_se_vuelve_a_pedir()
    {
        var mediator = new MediatorAlta
        {
            Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true),
            RechazoAlCrear = Error.Crear("Visita.SinAutoridadParaAsignar", "No tienes autoridad para asignar."),
            // Entre la vista previa y Guardar se perdió la autoridad: la segunda respuesta ya dice que no se puede asignar.
            PreviaTrasCrear = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: false),
        };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);
        mediator.ConsultasPlan.Should().ContainSingle();

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle();
        mediator.ConsultasPlan.Should().HaveCount(2, "el rechazo puede venir de una vista previa que se quedó vieja");
        BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent.Should().Contain("No tienes permiso para asignar");

        GuardarDebeEstarDeshabilitado(cut, "Resuelve el aviso de la vista previa: con estos trabajadores la visita no se puede crear.");
        mediator.Creaciones.Should().ContainSingle("la vista previa nueva ya no deja intentarlo otra vez");
    }

    [Fact]
    public async Task Al_abrir_otra_visita_nueva_no_queda_la_vista_previa_de_la_anterior()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);
        BloquesDeVistaPrevia(cut).Should().ContainSingle();

        await cut.FindAll(".acciones-cabecera button").First(b => b.TextContent.Contains("Nueva visita")).ClickAsync(new MouseEventArgs());

        BloquesDeVistaPrevia(cut).Should().BeEmpty("el formulario nuevo no tiene Centro ni Trabajadores");
        cut.Find(".drawer-panel .vista-previa-alta").ClassList.Should().Contain("vista-previa-alta--vacia", "sin vista previa la región se encoge");
    }

    [Fact]
    public async Task La_vista_previa_vive_en_una_region_viva_fija_y_en_catalan_se_lee_bien()
    {
        var culturaAnterior = System.Globalization.CultureInfo.CurrentUICulture;
        var formatoAnterior = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.CurrentUICulture =
                System.Globalization.CultureInfo.GetCultureInfo("ca-ES");
            var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };
            var cut = Renderizar(mediator);
            await cut.FindAll(".acciones-cabecera button").First(b => b.TextContent.Contains("Nova visita")).ClickAsync(new MouseEventArgs());

            // La región viva ya está en el DOM, vacía, antes de que haya vista previa: así el lector de pantalla anuncia su contenido al llegar.
            cut.Find(".drawer-panel .vista-previa-alta").GetAttribute("role").Should().Be("status");
            cut.FindAll(".drawer-panel .vista-previa-alta .alerta-info").Should().BeEmpty();

            await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = Centro.ToString() });
            await cut.Find(".drawer-panel .lista-seleccion-multiple input").ChangeAsync(new ChangeEventArgs { Value = true });

            var texto = BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent;
            texto.Should().Contain("S'assignarà Ana García Ruiz a aquest centre.").And.Contain("S'obriran 2 gestions:");
            texto.Should().NotContainAny("Falta", "CaducaAntesDelFin", "Tenant", "Inbound", "Outbound");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = formatoAnterior;
            System.Globalization.CultureInfo.CurrentUICulture = culturaAnterior;
        }
    }

    [Fact]
    public async Task Al_cambiar_la_fecha_de_fin_se_recalcula_la_vista_previa()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        await CambiarFechaFinAsync(cut, Hoy.AddDays(30));

        mediator.ConsultasPlan.Should().HaveCount(2);
        mediator.ConsultasPlan[1].FechaFin.Should().Be(Hoy.AddDays(30));
    }

    [Fact]
    public async Task En_edicion_no_se_pide_la_vista_previa()
    {
        var existente = new VisitaListaDto(
            Guid.NewGuid(), Centro, "Planta Norte", Guid.NewGuid(), "Cliente A", Guid.NewGuid(), "Empresa A",
            Hoy, Hoy.AddDays(2), TotalTrabajadores: 1, DocumentacionCompleta: false, NotificadoCliente: false,
            OrigenVisita.Manual, NivelUrgenciaVisita.Urgente, Version: Guid.NewGuid());
        var mediator = new MediatorAlta { FilaExistente = existente, Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };
        var cut = Renderizar(mediator);
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("j"));
        await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("e"));
        cut.WaitForAssertion(() => cut.Find(".drawer-panel .lista-seleccion-multiple input").HasAttribute("checked").Should().BeTrue("la edición abre con su Trabajadora marcada"));

        await cut.Find(".drawer-panel .lista-seleccion-multiple input").ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.Find(".drawer-panel .lista-seleccion-multiple input").ChangeAsync(new ChangeEventArgs { Value = true });
        await CambiarFechaFinAsync(cut, Hoy.AddDays(3));

        mediator.ConsultasPlan.Should().BeEmpty();
        BloquesDeVistaPrevia(cut).Should().BeEmpty();
        cut.FindAll(".drawer-panel .vista-previa-alta").Should().BeEmpty("en edición no hay vista previa ni región viva");
    }
}
