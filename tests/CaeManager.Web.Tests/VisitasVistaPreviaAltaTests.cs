using AngleSharp.Dom;
using Bunit;
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

        private static Task<T> Respuesta<T>(object? valor) => Task.FromResult((T)valor!);

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerVisitasQuery consulta:
                    var filas = FilaExistente is null ? new List<VisitaListaDto>() : [FilaExistente];
                    return Respuesta<TResponse>(new ResultadoPaginado<VisitaListaDto>(filas, filas.Count, consulta.Pagina, consulta.TamanoPagina));

                case ObtenerCentrosParaSelectorQuery:
                    return Respuesta<TResponse>((IReadOnlyList<CentroSelectorDto>)[new CentroSelectorDto(Centro, "Planta Norte", "Cliente A", "Empresa A")]);

                case ObtenerTrabajadoresParaSelectorQuery:
                    return Respuesta<TResponse>((IReadOnlyList<TrabajadorSelectorDto>)[TrabajadorSelectorFalso.Crear(Trabajador, "Ana García Ruiz")]);

                case ObtenerTrabajadoresAsignadosDeCentroQuery:
                    return Respuesta<TResponse>((IReadOnlyList<TrabajadorAsignadoDto>)[new TrabajadorAsignadoDto(Trabajador, "x", "y", Hoy)]);

                case ObtenerVisitaPorIdQuery edicion:
                    return Respuesta<TResponse>(new VisitaDetalleDto(
                        edicion.Id, Centro, "Planta Norte", "Cliente A", "Empresa A", Hoy, Hoy.AddDays(2),
                        TrabajadorIds: [Trabajador], NotificadoCliente: false, Notas: null, HoraEstimadaAcceso: null, Version: Guid.NewGuid()));

                case ObtenerPlanAltaVisitaQuery plan:
                    ConsultasPlan.Add(plan);
                    return Respuesta<TResponse>(Previa);

                case CrearVisitaCommand crear:
                    Creaciones.Add(crear);
                    return Respuesta<TResponse>(Result.Exito(Guid.NewGuid()));

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
        .FindAll(".drawer-panel .alerta-info[role=status]")
        .Where(b => b.TextContent.Contains("Se asignará") || b.TextContent.Contains("No tienes permiso") || b.TextContent.Contains("no se puede asignar hoy"))
        .ToArray();

    /// <summary>La fecha de fin es el segundo campo de fecha del formulario (el primero es el de inicio).</summary>
    private static Task CambiarFechaFinAsync(IRenderedComponent<Visitas> cut, DateOnly fecha)
    {
        var campo = cut.FindComponents<CampoTexto>().Where(c => c.Instance.Type == "date").ElementAt(1);
        return cut.InvokeAsync(() => campo.Instance.ValorChanged.InvokeAsync(fecha.ToString("yyyy-MM-dd")));
    }

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
    }

    [Fact]
    public async Task Con_plan_y_autoridad_crear_la_visita_pide_asignar_y_crear_las_gestiones()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: true) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle().Which.AsignarYCrearGestiones.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_autoridad_para_asignar_avisa_y_crea_la_visita_como_antes()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConAsignacionYGestiones(), PuedeAsignar: false) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        var texto = BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent;
        texto.Should().Contain("No tienes permiso para asignar").And.Contain("sin asignarlos y sin abrir gestiones");
        texto.Should().NotContain("Se abrirán", "sin permiso no se promete nada");

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle().Which.AsignarYCrearGestiones.Should().BeFalse();
    }

    [Fact]
    public async Task Con_una_asignacion_solapada_el_aviso_nombra_a_la_persona_y_no_promete_nada()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConUnaSolapada(), PuedeAsignar: true) };

        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        var texto = BloquesDeVistaPrevia(cut).Should().ContainSingle().Which.TextContent;
        texto.Should().Contain("Bruno Pérez no se puede asignar hoy a este centro: tuvo una asignación anterior cuyo periodo coincide con hoy.");
        texto.Should().Contain("Quita de la visita a quien no se pueda asignar o revisa su asignación")
            .And.Contain("la visita se creará sin asignar a nadie y sin abrir gestiones");
        texto.Should().NotContainAny("Se asignará", "Se abrirá", "Se abrirán", "Tenant", "Inbound", "Outbound", "Gestor");
    }

    [Fact]
    public async Task Con_una_asignacion_solapada_se_crea_la_visita_sin_pedir_asignar_ni_crear_gestiones()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanConUnaSolapada(), PuedeAsignar: true) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle().Which.AsignarYCrearGestiones.Should().BeFalse(
            "pedir asignar con una solapada haría que el servidor rechazase la Visita entera");
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

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle().Which.AsignarYCrearGestiones.Should().BeFalse();
    }

    [Fact]
    public async Task Con_el_plan_vacio_no_hay_bloque_y_la_visita_se_crea_como_antes()
    {
        var mediator = new MediatorAlta { Previa = new PlanAltaVisitaPreviaDto(PlanAltaVisitaDto.Vacio, PuedeAsignar: true) };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        mediator.ConsultasPlan.Should().ContainSingle("se pidió, pero no había nada que decir");
        BloquesDeVistaPrevia(cut).Should().BeEmpty();

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle().Which.AsignarYCrearGestiones.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_vista_previa_fuera_de_alcance_el_formulario_sigue_y_crea_como_antes()
    {
        var mediator = new MediatorAlta { Previa = null };
        var cut = await NuevaVisitaConTrabajadoraAsync(mediator);

        BloquesDeVistaPrevia(cut).Should().BeEmpty();

        await GuardarAsync(cut);

        mediator.Creaciones.Should().ContainSingle().Which.AsignarYCrearGestiones.Should().BeFalse();
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
    }
}
