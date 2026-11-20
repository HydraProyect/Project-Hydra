using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ConfirmarClasificacionRuidoMensaje;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Gestiones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reclamaciones;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// D6=a (2026-11-19): al Tenant solo Inbound con el papel de Tenant de origen, Comunicaciones le queda abierta solo para
/// los hilos anclados a una Empresa contratista (<see cref="AmbitoComunicacionesSoloInbound"/>). Dos lecturas del hilo
/// enseñaban, aun así, datos de Outbound (hallazgo A1 del revisor-puente sobre la PR #2379):
/// <list type="bullet">
/// <item>el detalle (<see cref="ObtenerConversacionPorIdQueryHandler"/>) proyectaba las sugerencias de Visita pendientes
/// (Centro, fechas, resumen) y las de Gestión (Trabajador, tipo de documento);</item>
/// <item>la bandeja (<see cref="ObtenerConversacionesQueryHandler"/>) pedía la cartera de Centros directamente a
/// <see cref="IAlcanceDatosService"/>, sin el ámbito, y la búsqueda por Centro ligaba hilos por sus Visitas, Gestiones o
/// participantes.</item>
/// </list>
/// Cada caso se prueba con el ámbito activo (vacío) y con el ámbito inactivo (los datos), para que el test no pase por un
/// fixture que no tenga nada que proyectar.
/// </summary>
public class ComunicacionesSoloInboundSinCentrosNiSugerenciasTests
{
    [Fact]
    public async Task CentrosVisiblesAsync_con_el_ambito_solo_Inbound_devuelve_una_cartera_vacia()
    {
        var centro = Guid.NewGuid();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centro]);

        using (AmbitoComunicacionesSoloInbound.Fijar(acotada: true))
            (await alcance.CentrosVisiblesAsync()).Should().NotBeNull().And.BeEmpty();

        (await alcance.CentrosVisiblesAsync()).Should().Equal(centro);
    }

    [Fact]
    public async Task CentrosVisiblesAsync_con_el_marco_no_acotado_devuelve_la_cartera()
    {
        var centro = Guid.NewGuid();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centro]);

        using (AmbitoComunicacionesSoloInbound.Fijar(acotada: false))
            (await alcance.CentrosVisiblesAsync()).Should().Equal(centro);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CarteraEnMemoria_decide_los_Centros_con_el_mismo_recorte_que_CentroVisibleAsync(bool soloInbound, bool visible)
    {
        // CarteraEnMemoria la construye DocumentosLegiblesAsync, que alcanza ObtenerRepartoAdjuntosQuery (acotada). Su
        // cartera de Centros tiene que ser la misma que la de la extensión, también dentro del marco.
        var centro = Guid.NewGuid();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centro]);

        foreach (var comoConjunto in new[] { true, false })
        {
            using (AmbitoComunicacionesSoloInbound.Fijar(acotada: soloInbound))
                (await new CarteraEnMemoria(alcance, comoConjunto).CentroVisibleAsync(centro)).Should().Be(visible);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_detalle_del_hilo_no_proyecta_sugerencias_de_Visita_ni_de_Gestion_al_Tenant_solo_Inbound(bool soloInbound)
    {
        var escenario = new Escenario();
        var mensaje = escenario.HiloConEmpresaYMensaje();

        var centro = escenario.Centro("Fábrica Norte");
        escenario.Comunicaciones.ListaSugerenciasVisitaCorreo.Add(new SugerenciaVisitaCorreo(
            mensaje.Id, centro.Id, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 2), "Visita de mantenimiento", 90, 90, 90));
        var sugerenciaGestion = new SugerenciaGestionCorreo(mensaje.Id, "Renovar el reconocimiento médico", 80);
        var detalle = sugerenciaGestion.AgregarDetalle(Guid.NewGuid(), Guid.NewGuid(), 80, 80);
        escenario.Comunicaciones.ListaSugerenciasGestionCorreo.Add(sugerenciaGestion);
        escenario.Comunicaciones.ListaDetallesSugerenciaGestionCorreo.Add(detalle);

        ConversacionDetalleDto? resultado;
        using (AmbitoComunicacionesSoloInbound.Fijar(acotada: soloInbound))
            resultado = await escenario.Detalle().Handle(new ObtenerConversacionPorIdQuery(mensaje.ConversacionId), CancellationToken.None);

        // El hilo anclado a una Empresa sigue visible en los dos casos: lo que cambia es solo lo que se proyecta de Outbound.
        var dto = resultado.Should().NotBeNull().And.Subject.As<ConversacionDetalleDto>().Mensajes.Should().ContainSingle().Subject;
        if (soloInbound)
        {
            dto.SugerenciaVisita.Should().BeNull();
            dto.SugerenciasGestion.Should().BeEmpty();
        }
        else
        {
            dto.SugerenciaVisita.Should().NotBeNull();
            dto.SugerenciaVisita!.CentroId.Should().Be(centro.Id);
            dto.SugerenciaVisita.CentroNombre.Should().Be("Fábrica Norte");
            dto.SugerenciasGestion.Should().ContainSingle();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_busqueda_por_Centro_de_la_bandeja_no_liga_hilos_al_Tenant_solo_Inbound(bool soloInbound)
    {
        var escenario = new Escenario();
        var mensaje = escenario.HiloConEmpresaYMensaje();
        var centro = escenario.Centro("Fábrica Norte");
        // Vínculo 1 de la búsqueda: el Centro es participante del hilo. Ni el asunto ni el cuerpo contienen el término.
        escenario.Comunicaciones.ListaParticipantesConversacion.Add(new ParticipanteConversacion(
            mensaje.ConversacionId, "centro@ejemplo.test", RolParticipante.Cc, TipoParticipanteOrigen.Centro, centro.Id));

        ResultadoPaginado<ConversacionListaDto> resultado;
        using (AmbitoComunicacionesSoloInbound.Fijar(acotada: soloInbound))
            resultado = await escenario.Bandeja().Handle(new ObtenerConversacionesQuery(Busqueda: "norte"), CancellationToken.None);

        if (soloInbound)
            resultado.TotalElementos.Should().Be(0);
        else
            resultado.Elementos.Should().ContainSingle(c => c.Id == mensaje.ConversacionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_bandeja_no_cuenta_como_ruido_las_sugerencias_de_Gestion_al_Tenant_solo_Inbound(bool soloInbound)
    {
        // Ronda 11 del revisor-puente: el único test de la bandeja con el marco salía por «total == 0» antes de contar el
        // ruido. Aquí el hilo con Empresa sí se lista en los dos casos, y lo único que cambia es el contador.
        var escenario = new Escenario();
        var mensaje = escenario.HiloConEmpresaYMensaje();
        escenario.SugerenciaDeGestionSoloConRepeticiones(mensaje);

        ResultadoPaginado<ConversacionListaDto> resultado;
        using (AmbitoComunicacionesSoloInbound.Fijar(acotada: soloInbound))
            resultado = await escenario.Bandeja().Handle(new ObtenerConversacionesQuery(), CancellationToken.None);

        resultado.Elementos.Should().ContainSingle(c => c.Id == mensaje.ConversacionId)
            .Which.MensajesRuido.Should().Be(soloInbound ? 0 : 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmar_el_ruido_de_un_mensaje_no_toca_ni_revela_sus_sugerencias_de_Gestion_al_Tenant_solo_Inbound(bool soloInbound)
    {
        // Ronda 11 del revisor-puente: el comando está acotado, no cerrado. Con el marco, un mensaje que solo tiene una
        // sugerencia de Gestión responde igual que uno sin nada, y los detalles de esa Gestión no se confirman.
        var escenario = new Escenario();
        var mensaje = escenario.HiloConEmpresaYMensaje();
        var clasificacion = escenario.SugerenciaDeGestionSoloConRepeticiones(mensaje);
        var detalles = new ClasificacionesDeDetalleFalsas(clasificacion);
        var unidad = new UnitOfWorkFalso();
        var handler = new ConfirmarClasificacionRuidoMensajeCommandHandler(
            new ClasificacionRuidoMensajeRepositorioFalso(), detalles, escenario.Comunicaciones, escenario.AlcanceDelEscenario(), unidad);

        Result resultado;
        using (AmbitoComunicacionesSoloInbound.Fijar(acotada: soloInbound))
            resultado = await handler.Handle(new ConfirmarClasificacionRuidoMensajeCommand(mensaje.Id), CancellationToken.None);

        if (soloInbound)
        {
            resultado.EsExitoso.Should().BeFalse();
            resultado.Error.Codigo.Should().Be("ClasificacionRuidoMensaje.NoEncontrada");
            clasificacion.ConfirmadaManualmente.Should().BeFalse();
            detalles.Consultas.Should().Be(0);
            unidad.VecesGuardado.Should().Be(0);
        }
        else
        {
            resultado.EsExitoso.Should().BeTrue();
            clasificacion.ConfirmadaManualmente.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Confirmar_el_ruido_de_un_mensaje_con_clasificacion_propia_la_confirma_con_el_marco_solo_Inbound_sin_tocar_las_de_Gestion()
    {
        // Tercera pasada de Gemini (BAJA 7): el test anterior solo mide el mensaje sin clasificación propia. Aquí el
        // mensaje sí tiene una, así que el comando tiene éxito y la confirma, y el marco solo recorta la parte de Gestión:
        // el mutante «Activo && clasificacionMensaje is null ? null : …» dejaría pasar los detalles de la Gestión.
        var escenario = new Escenario();
        var mensaje = escenario.HiloConEmpresaYMensaje();
        var clasificacionDeGestion = escenario.SugerenciaDeGestionSoloConRepeticiones(mensaje);
        var clasificacionPropia = new ClasificacionRuidoMensaje(mensaje.Id, esNotificacionAutomatica: true, proveedorPlataformaCaeId: null);
        var clasificacionesDelMensaje = new ClasificacionRuidoMensajeRepositorioFalso();
        clasificacionesDelMensaje.Clasificaciones.Add(clasificacionPropia);
        var detalles = new ClasificacionesDeDetalleFalsas(clasificacionDeGestion);
        var unidad = new UnitOfWorkFalso();
        var handler = new ConfirmarClasificacionRuidoMensajeCommandHandler(
            clasificacionesDelMensaje, detalles, escenario.Comunicaciones, escenario.AlcanceDelEscenario(), unidad);

        Result resultado;
        using (AmbitoComunicacionesSoloInbound.Fijar(acotada: true))
            resultado = await handler.Handle(new ConfirmarClasificacionRuidoMensajeCommand(mensaje.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        clasificacionPropia.ConfirmadaManualmente.Should().BeTrue();
        clasificacionDeGestion.ConfirmadaManualmente.Should().BeFalse();
        detalles.Consultas.Should().Be(0);
        unidad.VecesGuardado.Should().Be(1);
    }

    private sealed class ClasificacionesDeDetalleFalsas(ClasificacionRuidoDetalleGestion clasificacion) : IClasificacionRuidoDetalleGestionRepository
    {
        public int Consultas { get; private set; }

        public void Agregar(ClasificacionRuidoDetalleGestion nueva) => throw new NotSupportedException();

        public Task<ClasificacionRuidoDetalleGestion?> ObtenerPorDetalleIdAsync(Guid detalleSugerenciaGestionCorreoId, CancellationToken cancellationToken = default)
        {
            Consultas++;
            return Task.FromResult(clasificacion.DetalleSugerenciaGestionCorreoId == detalleSugerenciaGestionCorreoId ? clasificacion : null);
        }
    }

    private sealed class Escenario
    {
        public ComunicacionesQueryContextFalso Comunicaciones { get; } = new();
        private readonly CentrosQueryContextFalso _centros = new();
        private readonly Guid _empresa = Guid.NewGuid();

        // Cartera acotada: la Empresa del hilo y el Centro sí están; acceso total no, para que el filtro de la cartera de
        // Centros (y no un null de «sin restricción») sea lo que decide.
        private AlcanceDatosServiceFalso Alcance() => new(
            tieneAccesoTotal: false, clienteIdsVisibles: [], empresaIdsVisibles: [_empresa],
            centroIdsVisibles: _centros.ListaCentros.Select(c => c.Id).ToList());

        public Mensaje HiloConEmpresaYMensaje()
        {
            var conversacion = new Conversacion("Documentación pendiente", empresaId: _empresa);
            var mensaje = new Mensaje(
                conversacion.Id, DireccionMensaje.Entrante, CanalConversacion.Correo, "contratista@ejemplo.test",
                "<p>Os enviamos lo que falta.</p>", new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc));
            Comunicaciones.ListaConversaciones.Add(conversacion);
            Comunicaciones.ListaMensajes.Add(mensaje);
            return mensaje;
        }

        public AlcanceDatosServiceFalso AlcanceDelEscenario() => Alcance();

        /// <summary>Una sugerencia de Gestión con un detalle pendiente que es repetición de algo ya reclamado: ruido.</summary>
        public ClasificacionRuidoDetalleGestion SugerenciaDeGestionSoloConRepeticiones(Mensaje mensaje)
        {
            var sugerencia = new SugerenciaGestionCorreo(mensaje.Id, "Renovar el reconocimiento médico", 80);
            var detalle = sugerencia.AgregarDetalle(Guid.NewGuid(), Guid.NewGuid(), 80, 80);
            var clasificacion = new ClasificacionRuidoDetalleGestion(detalle.Id, Guid.NewGuid());
            Comunicaciones.ListaSugerenciasGestionCorreo.Add(sugerencia);
            Comunicaciones.ListaDetallesSugerenciaGestionCorreo.Add(detalle);
            Comunicaciones.ListaClasificacionesRuidoDetalleGestion.Add(clasificacion);
            return clasificacion;
        }

        public Centro Centro(string nombre)
        {
            var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), nombre);
            _centros.ListaCentros.Add(centro);
            return centro;
        }

        public ObtenerConversacionPorIdQueryHandler Detalle() => new(
            _centros, Comunicaciones, new TrabajadoresQueryContextFalso(), new TiposDocumentoQueryContextFalso(),
            new VisitasVacias(), new DocumentosQueryContextFalso(), new EmpresasQueryContextFalso(),
            new ReclamacionesQueryContextFalso(), Alcance(), new SanitizadorIdentidad(), new MotorQueNoSeUsa(),
            PoliticaNotaInternaPruebas.Con("GestorCae"));

        public ObtenerConversacionesQueryHandler Bandeja() => new(
            new EmpresasQueryContextFalso(), Comunicaciones, new IntegracionesQueryContextFalso(), _centros,
            new GestionesVacias(), new VisitasVacias(), Alcance(), new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae"),
            new SinTenant());
    }

    private sealed class VisitasVacias : IVisitasQueryContext
    {
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(new List<Visita>().AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => throw new NotSupportedException();
    }

    private sealed class GestionesVacias : IGestionesQueryContext
    {
        public IQueryable<Gestion> Gestiones => new TestAsyncQueryable<Gestion>(new List<Gestion>().AsQueryable());
        public IQueryable<GestionAutomatica> GestionesAutomaticas => throw new NotSupportedException();
        public IQueryable<PasoGestionAutomatica> PasosGestionAutomatica => throw new NotSupportedException();
    }

    private sealed class SanitizadorIdentidad : ISanitizadorHtmlService
    {
        public string Sanear(string? html) => html ?? string.Empty;
    }

    /// <summary>El hilo está anclado a una Empresa, sin Cliente: el detalle no propone vinculación y no llega aquí.</summary>
    private sealed class MotorQueNoSeUsa : CaeManager.Application.Comunicaciones.Matching.IMotorCoincidenciaConversacionesService
    {
        public Task<CaeManager.Application.Comunicaciones.Matching.CandidatoCoincidenciaDto?> ProponerVinculacionAsync(
            Guid conversacionOrigenId, Guid clienteId, DateTime fechaUltimoMensajeOrigenUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }
}
