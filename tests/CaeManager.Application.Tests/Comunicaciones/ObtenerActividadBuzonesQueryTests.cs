using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerActividadBuzones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Gestiones;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Las columnas «Último correo» y «Abiertas» de la tabla de Buzones (PR-B1): una sola petición para todos los buzones, con
/// la regla de visibilidad de la bandeja y la misma definición de «abierta» que su filtro de estado.
/// </summary>
public class ObtenerActividadBuzonesQueryTests
{
    private static readonly DateTime Base = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task El_ultimo_correo_es_el_del_ultimo_mensaje_entrante_y_no_cuenta_los_salientes()
    {
        var escenario = new Escenario();
        var buzon = escenario.Buzon();
        var hilo = escenario.Hilo(buzon, "Hilo 1");
        escenario.Mensaje(hilo, DireccionMensaje.Entrante, Base);
        escenario.Mensaje(hilo, DireccionMensaje.Entrante, Base.AddHours(2));
        escenario.Mensaje(hilo, DireccionMensaje.Saliente, Base.AddHours(5));

        var actividad = await escenario.Actividad(buzon);

        actividad[buzon].UltimoCorreoUtc.Should().Be(Base.AddHours(2));
    }

    [Fact]
    public async Task El_ultimo_correo_se_agrupa_por_buzon_y_no_mezcla_los_de_otro()
    {
        var escenario = new Escenario();
        var uno = escenario.Buzon();
        var dos = escenario.Buzon();
        escenario.Mensaje(escenario.Hilo(uno, "Hilo uno"), DireccionMensaje.Entrante, Base.AddHours(1));
        escenario.Mensaje(escenario.Hilo(dos, "Hilo dos"), DireccionMensaje.Entrante, Base.AddHours(7));

        var actividad = await escenario.Actividad(uno, dos);

        actividad[uno].UltimoCorreoUtc.Should().Be(Base.AddHours(1));
        actividad[dos].UltimoCorreoUtc.Should().Be(Base.AddHours(7));
    }

    [Fact]
    public void La_peticion_es_de_Outbound_y_queda_acotada_al_Tenant_solo_Inbound_como_la_bandeja()
    {
        // La página la pide por el puerto del Núcleo, y el puerto envía esta petición por el mediador: la guarda la clasifica por
        // su tipo. Si dejara de ser de Comunicaciones, un Tenant solo Inbound contaría hilos que la bandeja le oculta.
        PeticionDeOutbound.Es(typeof(ObtenerActividadBuzonesQuery)).Should().BeTrue();
        PeticionDeOutbound.AcotadaAlSoloInbound(typeof(ObtenerActividadBuzonesQuery)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    public async Task Un_Tenant_solo_Inbound_solo_cuenta_los_hilos_anclados_a_una_Empresa(bool soloInbound, int abiertasEsperadas)
    {
        var escenario = new Escenario();
        var buzon = escenario.Buzon();
        var deEmpresa = escenario.Hilo(buzon, "De Empresa", empresaId: Guid.NewGuid());
        var deCliente = escenario.Hilo(buzon, "De Cliente", clienteId: Guid.NewGuid());
        var sinAncla = escenario.Hilo(buzon, "Sin ancla");
        escenario.Mensaje(deEmpresa, DireccionMensaje.Entrante, Base);
        escenario.Mensaje(deCliente, DireccionMensaje.Entrante, Base.AddHours(3));
        escenario.Mensaje(sinAncla, DireccionMensaje.Entrante, Base.AddHours(6));

        IReadOnlyDictionary<Guid, ActividadBuzonDto> actividad;
        using (AmbitoComunicacionesSoloInbound.Fijar(acotada: soloInbound))
            actividad = await escenario.Actividad(buzon);

        actividad[buzon].ConversacionesAbiertas.Should().Be(abiertasEsperadas);
        actividad[buzon].UltimoCorreoUtc.Should().Be(soloInbound ? Base : Base.AddHours(6));
    }

    [Fact]
    public async Task El_servicio_del_puerto_envia_la_peticion_por_el_mediador_con_los_mismos_buzones()
    {
        // Enviarla por el mediador es lo que la hace pasar por la guarda y el ámbito de Outbound (Tenant solo Inbound): si el
        // servicio llamara al handler directamente, ese ámbito no se fijaría y la tabla contaría hilos que la bandeja oculta.
        var uno = Guid.NewGuid();
        var dos = Guid.NewGuid();
        var respuesta = new Dictionary<Guid, ActividadBuzonDto> { [uno] = new(Base, 4) };
        var mediador = new EmisorFalso(respuesta);

        var resultado = await new ActividadBuzonesService(mediador).ObtenerAsync([uno, dos]);

        resultado.Should().BeSameAs(respuesta);
        mediador.Peticiones.Should().ContainSingle().Which.Should().BeOfType<ObtenerActividadBuzonesQuery>()
            .Which.ConexionIds.Should().BeEquivalentTo([uno, dos]);
    }

    [Fact]
    public void El_puerto_IActividadBuzones_esta_registrado_con_su_implementacion_de_Comunicaciones()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();

        servicios.Should().ContainSingle(d => d.ServiceType == typeof(IActividadBuzones))
            .Which.Should().Match<ServiceDescriptor>(d =>
                d.ImplementationType == typeof(ActividadBuzonesService) && d.Lifetime == ServiceLifetime.Scoped);
    }

    private sealed class EmisorFalso(IReadOnlyDictionary<Guid, ActividadBuzonDto> respuesta) : ISender
    {
        public List<object> Peticiones { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Peticiones.Add(request);
            return Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Un_buzon_sin_ningun_correo_devuelve_fecha_nula_y_cero_abiertas()
    {
        var escenario = new Escenario();
        var buzon = escenario.Buzon();

        var actividad = await escenario.Actividad(buzon);

        actividad.Should().ContainKey(buzon);
        actividad[buzon].Should().Be(new ActividadBuzonDto(null, 0));
    }

    [Fact]
    public async Task Abiertas_cuenta_solo_las_conversaciones_en_estado_Abierta_de_ese_buzon()
    {
        var escenario = new Escenario();
        var buzon = escenario.Buzon();
        var otro = escenario.Buzon();
        escenario.Hilo(buzon, "Abierta 1");
        escenario.Hilo(buzon, "Abierta 2");
        escenario.Hilo(buzon, "Esperando", EstadoConversacion.EsperandoRespuestaExterna);
        escenario.Hilo(buzon, "Resuelta", EstadoConversacion.Resuelta);
        escenario.Hilo(otro, "De otro buzon");

        var actividad = await escenario.Actividad(buzon, otro);

        actividad[buzon].ConversacionesAbiertas.Should().Be(2);
        actividad[otro].ConversacionesAbiertas.Should().Be(1);
    }

    [Fact]
    public async Task Con_cartera_acotada_ni_las_abiertas_ni_el_ultimo_correo_incluyen_hilos_fuera_de_la_cartera()
    {
        var empresaVisible = Guid.NewGuid();
        var escenario = new Escenario(new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [], empresaIdsVisibles: [empresaVisible]));
        var buzon = escenario.Buzon();
        var propio = escenario.Hilo(buzon, "En cartera", empresaId: empresaVisible);
        var ajeno = escenario.Hilo(buzon, "Fuera de cartera", empresaId: Guid.NewGuid());
        escenario.Mensaje(propio, DireccionMensaje.Entrante, Base);
        escenario.Mensaje(ajeno, DireccionMensaje.Entrante, Base.AddDays(1));

        var actividad = await escenario.Actividad(buzon);

        actividad[buzon].ConversacionesAbiertas.Should().Be(1);
        actividad[buzon].UltimoCorreoUtc.Should().Be(Base);
    }

    [Fact]
    public async Task El_buzon_personal_de_otro_Gestor_CAE_no_sale_en_la_respuesta_y_el_propio_si()
    {
        var yo = Guid.NewGuid();
        var escenario = new Escenario(usuarioId: yo);
        var propio = escenario.BuzonPersonal(yo);
        var ajeno = escenario.BuzonPersonal(Guid.NewGuid());
        escenario.Hilo(propio, "Mio");
        escenario.Hilo(ajeno, "Ajeno");

        var actividad = await escenario.Actividad(propio, ajeno);

        actividad.Keys.Should().BeEquivalentTo([propio]);
        actividad[propio].ConversacionesAbiertas.Should().Be(1);
    }

    [Fact]
    public async Task Un_id_que_no_es_un_buzon_del_Tenant_no_tiene_clave_en_la_respuesta()
    {
        // Un id que no existe o que es de otro Tenant no vuelve de la lectura de conexiones (en PostgreSQL, por el filtro de
        // Tenant y la RLS; aquí, por no estar en el contexto): no es un buzón medible y falta del diccionario, así que la
        // tabla enseña «—» y no «Sin correos visibles» con 0 abiertas, que afirmarían haberlo medido.
        var escenario = new Escenario();
        var buzon = escenario.Buzon();
        var inexistente = Guid.NewGuid();

        var actividad = await escenario.Actividad(buzon, inexistente);

        actividad.Keys.Should().BeEquivalentTo([buzon]);
    }

    [Fact]
    public async Task Sin_buzones_no_consulta_nada_y_devuelve_un_diccionario_vacio()
    {
        var escenario = new Escenario();

        (await escenario.Actividad()).Should().BeEmpty();
    }

    [Fact]
    public async Task El_recuento_de_Abiertas_coincide_con_el_de_la_bandeja_filtrada_por_Abierta_para_el_mismo_buzon()
    {
        // Mismo alcance acotado en la bandeja y aquí: hay hilos en cartera y fuera de ella, de varios estados y en dos
        // buzones. El ConversacionListaDto no trae el buzón: se distingue por el prefijo del asunto.
        var empresaVisible = Guid.NewGuid();
        var escenario = new Escenario(new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [], empresaIdsVisibles: [empresaVisible]));
        var a = escenario.Buzon();
        var b = escenario.Buzon();
        escenario.Hilo(a, "A-1", empresaId: empresaVisible);
        escenario.Hilo(a, "A-2", empresaId: empresaVisible);
        escenario.Hilo(a, "A-3", EstadoConversacion.Resuelta, empresaVisible);
        escenario.Hilo(a, "A-4", EstadoConversacion.EsperandoRespuestaExterna, empresaVisible);
        escenario.Hilo(a, "A-5", empresaId: Guid.NewGuid());
        escenario.Hilo(b, "B-1", empresaId: empresaVisible);
        escenario.Hilo(b, "B-2", EstadoConversacion.EnEsperaDeTercero, empresaVisible);

        var bandeja = await escenario.Bandeja().Handle(
            new ObtenerConversacionesQuery(Estado: EstadoConversacion.Abierta, TamanoPagina: 100), CancellationToken.None);
        var actividad = await escenario.Actividad(a, b);

        actividad[a].ConversacionesAbiertas.Should().Be(bandeja.Elementos.Count(c => c.Asunto.StartsWith("A-")))
            .And.Be(2);
        actividad[b].ConversacionesAbiertas.Should().Be(bandeja.Elementos.Count(c => c.Asunto.StartsWith("B-")))
            .And.Be(1);
    }

    private sealed class Escenario(AlcanceDatosServiceFalso? alcance = null, Guid? usuarioId = null)
    {
        private readonly AlcanceDatosServiceFalso _alcance = alcance ?? new AlcanceDatosServiceFalso();
        private readonly Guid _usuarioId = usuarioId ?? Guid.NewGuid();
        private readonly ComunicacionesQueryContextFalso _comunicaciones = new();
        private readonly IntegracionesQueryContextFalso _integraciones = new();
        private int _numero;

        public Guid Buzon() => Nuevo(null);

        public Guid BuzonPersonal(Guid gestorId) => Nuevo(gestorId);

        private Guid Nuevo(Guid? gestorId)
        {
            var n = ++_numero;
            var conexion = new ConexionIntegracion($"buzon{n}@operador.test", $"Buzón {n}", gestorPropietarioId: gestorId);
            _integraciones.ConexionesLista.Add(conexion);
            return conexion.Id;
        }

        public Conversacion Hilo(
            Guid buzon, string asunto, EstadoConversacion estado = EstadoConversacion.Abierta, Guid? empresaId = null, Guid? clienteId = null)
        {
            var hilo = new Conversacion(asunto, clienteId: clienteId, empresaId: empresaId);
            hilo.AsociarConexion(buzon, $"hilo-externo-{asunto}");
            if (estado != EstadoConversacion.Abierta) hilo.CambiarEstado(estado).EsExitoso.Should().BeTrue();
            _comunicaciones.ListaConversaciones.Add(hilo);
            return hilo;
        }

        public void Mensaje(Conversacion hilo, DireccionMensaje direccion, DateTime fechaUtc) =>
            _comunicaciones.ListaMensajes.Add(new Mensaje(
                hilo.Id, direccion, CanalConversacion.Correo, "remitente@ejemplo.test", "<p>Texto</p>", fechaUtc));

        public Task<IReadOnlyDictionary<Guid, ActividadBuzonDto>> Actividad(params Guid[] buzones) =>
            new ObtenerActividadBuzonesQueryHandler(
                _comunicaciones, _integraciones, _alcance, new CurrentUserServiceFalso(_usuarioId, "Administrador"), new SinTenant())
                .Handle(new ObtenerActividadBuzonesQuery(buzones), CancellationToken.None);

        public ObtenerConversacionesQueryHandler Bandeja() => new(
            new EmpresasQueryContextFalso(), _comunicaciones, _integraciones, new CentrosQueryContextFalso(),
            new GestionesVacias(), new VisitasVacias(), _alcance, new CurrentUserServiceFalso(_usuarioId, "Administrador"),
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

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }
}
