using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Gestiones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Estados agrupados de las vistas «Abiertas» y «Archivadas» de la bandeja (DN-O, Chris, 2026-11-20): «Abiertas» reúne Abierta,
/// Esperando respuesta del Interlocutor y Pendiente de tercero (y el estado histórico Pendiente, que la máquina de estados cuenta
/// como vivo); «Archivadas» reúne Resuelta y Cerrada. El filtro por un estado concreto sigue siendo el de siempre, y los dos se
/// suman con AND a la visibilidad (R-3a): ninguno amplía lo que se ve. Los recuentos de la pantalla son esta misma consulta con una
/// sola fila, así que el total que devuelve con <c>TamanoPagina: 1</c> es la cifra de la vista.
/// </summary>
public class ObtenerConversacionesFiltroEstadoTests
{
    private static readonly DateTime Fecha = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

    private static readonly EstadoConversacion[] TodosLosEstados = Enum.GetValues<EstadoConversacion>();

    private sealed class Escenario
    {
        private readonly Guid _empresaVisible = Guid.NewGuid();
        public ComunicacionesQueryContextFalso Comunicaciones { get; } = new();
        public Dictionary<EstadoConversacion, Guid> PorEstado { get; } = [];
        public Guid DeOtraCartera { get; }

        public Escenario()
        {
            // Una conversación por cada estado del enum, todas dentro de la cartera del usuario.
            foreach (var estado in TodosLosEstados)
                PorEstado[estado] = Hilo($"Hilo {estado}", _empresaVisible, estado).Id;
            // Una abierta y una archivada fuera de la cartera: R-3a las excluye, también con el filtro de estado.
            DeOtraCartera = Hilo("Abierta de otra cartera", Guid.NewGuid(), EstadoConversacion.Abierta).Id;
            Hilo("Cerrada de otra cartera", Guid.NewGuid(), EstadoConversacion.Cerrada);
        }

        private Conversacion Hilo(string asunto, Guid empresaId, EstadoConversacion estado)
        {
            var conversacion = new Conversacion(asunto, empresaId: empresaId);
            if (estado != EstadoConversacion.Abierta)
                conversacion.CambiarEstado(estado).EsExitoso.Should().BeTrue($"el estado {estado} se alcanza desde Abierta");
            Comunicaciones.ListaConversaciones.Add(conversacion);
            Comunicaciones.ListaMensajes.Add(new Mensaje(
                conversacion.Id, DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@ejemplo.test", "<p>Texto.</p>", Fecha));
            return conversacion;
        }

        public Task<ResultadoPaginado<ConversacionListaDto>> Lista(ObtenerConversacionesQuery consulta) =>
            new ObtenerConversacionesQueryHandler(
                new EmpresasQueryContextFalso(), Comunicaciones, new IntegracionesQueryContextFalso(), new CentrosQueryContextFalso(),
                new GestionesVacias(), new VisitasVacias(),
                new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [], empresaIdsVisibles: [_empresaVisible]),
                new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae"), new SinTenant())
                .Handle(consulta, CancellationToken.None);
    }

    private static Guid[] Ids(Escenario escenario, params EstadoConversacion[] estados) => estados.Select(e => escenario.PorEstado[e]).ToArray();

    [Fact]
    public async Task Abiertas_reune_Abierta_Esperando_Pendiente_de_tercero_y_el_Pendiente_historico()
    {
        var escenario = new Escenario();

        var resultado = await escenario.Lista(new ObtenerConversacionesQuery(GrupoEstado: GrupoEstadoConversacion.Abiertas));

        resultado.Elementos.Select(c => c.Id).Should().BeEquivalentTo(Ids(escenario,
            EstadoConversacion.Abierta, EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.EnEsperaDeTercero, EstadoConversacion.Pendiente));
        resultado.TotalElementos.Should().Be(4);
    }

    [Fact]
    public async Task Archivadas_reune_Resuelta_y_Cerrada()
    {
        var escenario = new Escenario();

        var resultado = await escenario.Lista(new ObtenerConversacionesQuery(GrupoEstado: GrupoEstadoConversacion.Archivadas));

        resultado.Elementos.Select(c => c.Id).Should().BeEquivalentTo(Ids(escenario, EstadoConversacion.Resuelta, EstadoConversacion.Cerrada));
        resultado.TotalElementos.Should().Be(2);
    }

    [Fact]
    public async Task Sin_grupo_ni_estado_devuelve_todas_las_de_la_cartera_y_los_dos_grupos_las_reparten_sin_solaparse()
    {
        var escenario = new Escenario();

        var todas = await escenario.Lista(new ObtenerConversacionesQuery());
        var abiertas = await escenario.Lista(new ObtenerConversacionesQuery(GrupoEstado: GrupoEstadoConversacion.Abiertas));
        var archivadas = await escenario.Lista(new ObtenerConversacionesQuery(GrupoEstado: GrupoEstadoConversacion.Archivadas));

        // Una por cada estado del enum; la abierta y la cerrada de otra cartera las deja fuera R-3a.
        todas.TotalElementos.Should().Be(TodosLosEstados.Length);
        abiertas.Elementos.Select(c => c.Id).Intersect(archivadas.Elementos.Select(c => c.Id)).Should().BeEmpty();
        abiertas.Elementos.Concat(archivadas.Elementos).Select(c => c.Id).Should().BeEquivalentTo(todas.Elementos.Select(c => c.Id),
            "ninguna conversación de la cartera se queda sin vista");
    }

    [Theory]
    [MemberData(nameof(Estados))]
    public async Task El_filtro_por_un_estado_concreto_sigue_devolviendo_solo_ese_estado(EstadoConversacion estado)
    {
        var escenario = new Escenario();

        var resultado = await escenario.Lista(new ObtenerConversacionesQuery(Estado: estado));

        resultado.Elementos.Should().ContainSingle().Which.Id.Should().Be(escenario.PorEstado[estado]);
        resultado.Elementos.Single().Estado.Should().Be(estado);
    }

    public static TheoryData<EstadoConversacion> Estados() => [.. TodosLosEstados];

    [Fact]
    public async Task El_estado_concreto_y_el_grupo_se_suman_con_AND()
    {
        var escenario = new Escenario();

        var cerradaEnArchivadas = await escenario.Lista(
            new ObtenerConversacionesQuery(Estado: EstadoConversacion.Cerrada, GrupoEstado: GrupoEstadoConversacion.Archivadas));
        var cerradaEnAbiertas = await escenario.Lista(
            new ObtenerConversacionesQuery(Estado: EstadoConversacion.Cerrada, GrupoEstado: GrupoEstadoConversacion.Abiertas));
        var esperandoEnAbiertas = await escenario.Lista(
            new ObtenerConversacionesQuery(Estado: EstadoConversacion.EsperandoRespuestaExterna, GrupoEstado: GrupoEstadoConversacion.Abiertas));

        cerradaEnArchivadas.Elementos.Should().ContainSingle().Which.Id.Should().Be(escenario.PorEstado[EstadoConversacion.Cerrada]);
        cerradaEnAbiertas.TotalElementos.Should().Be(0, "una Cerrada no es abierta: el grupo no ensancha el estado concreto");
        esperandoEnAbiertas.Elementos.Should().ContainSingle().Which.Id.Should().Be(escenario.PorEstado[EstadoConversacion.EsperandoRespuestaExterna]);
    }

    [Fact]
    public async Task El_grupo_se_suma_con_AND_a_los_demas_filtros_y_a_la_visibilidad_y_nunca_amplia_lo_que_se_ve()
    {
        var escenario = new Escenario();

        var abiertasSinAsignar = await escenario.Lista(
            new ObtenerConversacionesQuery(GrupoEstado: GrupoEstadoConversacion.Abiertas, SoloSinAsignar: true));
        var abiertasDeOtroCanal = await escenario.Lista(
            new ObtenerConversacionesQuery(GrupoEstado: GrupoEstadoConversacion.Abiertas, Canal: CanalConversacion.WhatsApp));
        var abiertasPorAsunto = await escenario.Lista(
            new ObtenerConversacionesQuery(GrupoEstado: GrupoEstadoConversacion.Abiertas, Busqueda: "otra cartera"));
        var todasLasAbiertas = await escenario.Lista(new ObtenerConversacionesQuery(GrupoEstado: GrupoEstadoConversacion.Abiertas));

        abiertasSinAsignar.TotalElementos.Should().Be(4, "ninguna está asignada, así que SoloSinAsignar no quita ninguna abierta");
        abiertasDeOtroCanal.TotalElementos.Should().Be(0, "las del escenario son de correo");
        abiertasPorAsunto.TotalElementos.Should().Be(0, "la abierta de otra cartera coincide con la búsqueda pero R-3a la deja fuera");
        todasLasAbiertas.Elementos.Select(c => c.Id).Should().NotContain(escenario.DeOtraCartera);
    }

    [Fact]
    public async Task El_recuento_de_una_vista_es_la_misma_consulta_con_una_fila()
    {
        var escenario = new Escenario();

        var recuento = await escenario.Lista(new ObtenerConversacionesQuery(GrupoEstado: GrupoEstadoConversacion.Abiertas, TamanoPagina: 1));

        recuento.TotalElementos.Should().Be(4);
        recuento.Elementos.Should().HaveCount(1);
    }

    [Fact]
    public void Cada_estado_del_enum_pertenece_a_un_solo_grupo()
    {
        // Si alguien añade un estado nuevo, tiene que decidir en qué vista cae: sin esto desaparecería de las dos.
        foreach (var estado in TodosLosEstados)
        {
            var enAbiertas = GruposEstadoConversacion.Abiertas.Contains(estado);
            var enArchivadas = GruposEstadoConversacion.Archivadas.Contains(estado);
            (enAbiertas ^ enArchivadas).Should().BeTrue($"{estado} debe estar en exactamente uno de los dos grupos");
            GruposEstadoConversacion.EsAbierta(estado).Should().Be(enAbiertas);
        }

        // Contenido fijado a mano, no contra la propia propiedad: si la máquina de estados añade un estado vivo, este test obliga a
        // decidir a conciencia si entra en «Abiertas» (y, con él, en el indicador de acción por aprobar de DN-L).
        GruposEstadoConversacion.Estados(GrupoEstadoConversacion.Abiertas).Should().BeEquivalentTo(
            [EstadoConversacion.Abierta, EstadoConversacion.Pendiente, EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.EnEsperaDeTercero]);
        GruposEstadoConversacion.Estados(GrupoEstadoConversacion.Archivadas).Should().BeEquivalentTo(
            [EstadoConversacion.Resuelta, EstadoConversacion.Cerrada]);
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

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }
}
