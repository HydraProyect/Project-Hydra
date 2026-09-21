using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Nivel 0 (DEC-33, REC-035) en las tres detecciones que <c>IngestaWebhookService</c>
/// lanza sobre cada correo entrante. Hasta este incremento eran las únicas consumidoras
/// de un proveedor de IA que no consultaban la instrucción de tratamiento del Tenant
/// propietario: el cuerpo del correo —y, en la detección de gestión, los Trabajadores
/// candidatos con su DNI— salían hacia el proveedor sin comprobar nada.
///
/// Cada detección se prueba por partida doble: sin instrucción vigente no se toca
/// ninguna dependencia (los dobles lanzan si se invocan, así que un gate que no corte
/// revienta el test en vez de pasar en silencio), y con instrucción vigente la misma
/// llamada sí sigue su curso. Sin ese segundo test, un servicio que no hiciera nada
/// nunca daría rojo.
/// </summary>
public class GateNivel0EnDeteccionesDeIngestaTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    // ─────────────────────────── Relevancia CAE ───────────────────────────

    [Fact]
    public async Task Sin_instruccion_vigente_la_relevancia_cae_no_llega_al_proveedor()
    {
        var clasificaciones = new ClasificacionRelevanciaCaeRepositorioEspia();
        var registro = new LoggerRecolector<RelevanciaCaeService>();
        var servicio = new RelevanciaCaeService(
            new DeteccionRelevanciaQueLanzaSiSeInvoca(), clasificaciones,
            new InstruccionTratamientoIaFalsa(habilitada: false), new TenantActualFalso(TenantId), registro);

        await servicio.ProcesarAsync(ConversacionConUnMensaje(), CancellationToken.None);

        clasificaciones.Consultas.Should().Be(0, "el gate va por delante de tocar nada");
        registro.Mensajes.Should().ContainSingle().Which.Should().Contain("Nivel 0");
    }

    [Fact]
    public async Task Con_instruccion_vigente_la_relevancia_cae_sigue_su_curso()
    {
        var clasificaciones = new ClasificacionRelevanciaCaeRepositorioEspia();
        var deteccion = new DeteccionRelevanciaEspia();
        var servicio = new RelevanciaCaeService(
            deteccion, clasificaciones,
            new InstruccionTratamientoIaFalsa(habilitada: true), new TenantActualFalso(TenantId),
            new LoggerRecolector<RelevanciaCaeService>());

        await servicio.ProcesarAsync(ConversacionConUnMensaje(), CancellationToken.None);

        clasificaciones.Consultas.Should().Be(1);
        deteccion.Llamadas.Should().Be(1);
    }

    [Fact]
    public async Task Sin_tenant_resuelto_la_relevancia_cae_falla_cerrada()
    {
        var clasificaciones = new ClasificacionRelevanciaCaeRepositorioEspia();
        var servicio = new RelevanciaCaeService(
            new DeteccionRelevanciaQueLanzaSiSeInvoca(), clasificaciones,
            new InstruccionTratamientoIaQueLanzaSiSeInvoca(), new TenantActualFalso(null),
            new LoggerRecolector<RelevanciaCaeService>());

        await servicio.ProcesarAsync(ConversacionConUnMensaje(), CancellationToken.None);

        clasificaciones.Consultas.Should().Be(0);
    }

    // ─────────────────────────── Sugerencia de visita ───────────────────────────

    [Fact]
    public async Task Sin_instruccion_vigente_la_sugerencia_de_visita_no_llega_al_proveedor()
    {
        var centros = new CentrosQueryContextEspia();
        var registro = new LoggerRecolector<SugerenciaVisitaCorreoService>();
        var servicio = new SugerenciaVisitaCorreoService(
            centros, new DeteccionVisitaQueLanzaSiSeInvoca(), new SugerenciaVisitaRepositorioQueLanzaSiSeInvoca(),
            new InstruccionTratamientoIaFalsa(habilitada: false), new TenantActualFalso(TenantId), registro);

        await servicio.ProcesarAsync(MensajeEntrante(), Guid.NewGuid(), CancellationToken.None);

        centros.Consultas.Should().Be(0, "el gate va por delante de cargar los Centros candidatos");
        registro.Mensajes.Should().ContainSingle().Which.Should().Contain("Nivel 0");
    }

    [Fact]
    public async Task Con_instruccion_vigente_la_sugerencia_de_visita_sigue_su_curso()
    {
        var centros = new CentrosQueryContextEspia();
        var servicio = new SugerenciaVisitaCorreoService(
            centros, new DeteccionVisitaQueLanzaSiSeInvoca(), new SugerenciaVisitaRepositorioQueLanzaSiSeInvoca(),
            new InstruccionTratamientoIaFalsa(habilitada: true), new TenantActualFalso(TenantId),
            new LoggerRecolector<SugerenciaVisitaCorreoService>());

        await servicio.ProcesarAsync(MensajeEntrante(), Guid.NewGuid(), CancellationToken.None);

        // Sin Centros el servicio se para ahí por su propia regla (v1 no sugiere crear
        // el Centro): lo que demuestra el paso del gate es que llegó a consultarlos.
        centros.Consultas.Should().Be(1);
    }

    // ─────────────────────────── Sugerencia de gestión ───────────────────────────

    [Fact]
    public async Task Sin_instruccion_vigente_la_sugerencia_de_gestion_no_llega_al_proveedor()
    {
        var centros = new CentrosQueryContextEspia();
        var registro = new LoggerRecolector<SugerenciaGestionCorreoService>();
        var servicio = ServicioDeGestion(centros, habilitada: false, registro);

        var resultado = await servicio.ProcesarAsync(MensajeEntrante(), Guid.NewGuid(), CancellationToken.None);

        centros.Consultas.Should().Be(0, "el gate va por delante de cargar Centros, Asignaciones y Trabajadores");
        resultado.Should().Be(ResultadoDeteccionGestionDto.Vacio);
        registro.Mensajes.Should().ContainSingle().Which.Should().Contain("Nivel 0");
    }

    [Fact]
    public async Task Con_instruccion_vigente_la_sugerencia_de_gestion_sigue_su_curso()
    {
        var centros = new CentrosQueryContextEspia();
        var servicio = ServicioDeGestion(centros, habilitada: true, new LoggerRecolector<SugerenciaGestionCorreoService>());

        await servicio.ProcesarAsync(MensajeEntrante(), Guid.NewGuid(), CancellationToken.None);

        centros.Consultas.Should().Be(1);
    }

    private static SugerenciaGestionCorreoService ServicioDeGestion(
        ICentrosQueryContext centros, bool habilitada, ILogger<SugerenciaGestionCorreoService> registro) =>
        new(centros,
            new AsignacionesQueryContextQueLanzaSiSeInvoca(),
            new TrabajadoresQueryContextQueLanzaSiSeInvoca(),
            new TiposDocumentoQueryContextQueLanzaSiSeInvoca(),
            new DeteccionGestionQueLanzaSiSeInvoca(),
            new SugerenciaGestionRepositorioQueLanzaSiSeInvoca(),
            new InstruccionTratamientoIaFalsa(habilitada),
            new TenantActualFalso(TenantId),
            registro);

    // ─────────────────────────── Material de prueba ───────────────────────────

    private static Conversacion ConversacionConUnMensaje()
    {
        var conversacion = new Conversacion("Documentación pendiente", Guid.NewGuid());
        conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, "obra@ejemplo.test", "<p>Nos falta el reconocimiento médico.</p>");
        return conversacion;
    }

    private static Mensaje MensajeEntrante() => ConversacionConUnMensaje().Mensajes.Single();

    private sealed class InstruccionTratamientoIaFalsa(bool habilitada) : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(habilitada);
    }

    /// <summary>Sin tenant resuelto no debe llegarse siquiera a preguntar por la instrucción.</summary>
    private sealed class InstruccionTratamientoIaQueLanzaSiSeInvoca : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Sin tenant resuelto no se consulta la instrucción de tratamiento.");
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class LoggerRecolector<T> : ILogger<T>
    {
        public List<string> Mensajes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Mensajes.Add(formatter(state, exception));
    }

    private sealed class ClasificacionRelevanciaCaeRepositorioEspia : IClasificacionRelevanciaCaeRepository
    {
        public int Consultas { get; private set; }

        public void Agregar(ClasificacionRelevanciaCae clasificacion) { }

        public Task<ClasificacionRelevanciaCae?> ObtenerPorConversacionIdAsync(Guid conversacionId, CancellationToken cancellationToken = default)
        {
            Consultas++;
            return Task.FromResult<ClasificacionRelevanciaCae?>(null);
        }
    }

    private sealed class DeteccionRelevanciaQueLanzaSiSeInvoca : IDeteccionRelevanciaCaeService
    {
        public Task<Result<DeteccionRelevanciaCaeDto>> DetectarAsync(string cuerpoConversacion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Sin instrucción de tratamiento vigente no puede salir nada hacia el proveedor de IA.");
    }

    private sealed class DeteccionRelevanciaEspia : IDeteccionRelevanciaCaeService
    {
        public int Llamadas { get; private set; }

        public Task<Result<DeteccionRelevanciaCaeDto>> DetectarAsync(string cuerpoConversacion, CancellationToken cancellationToken = default)
        {
            Llamadas++;
            return Task.FromResult(Result.Exito(new DeteccionRelevanciaCaeDto(false, "Solo negociación comercial.", 80)));
        }
    }

    private sealed class DeteccionVisitaQueLanzaSiSeInvoca : IDeteccionVisitaCorreoService
    {
        public Task<Result<DeteccionVisitaCorreoDto>> DetectarAsync(
            string cuerpoMensaje, IReadOnlyList<CentroCandidatoVisitaDto> centrosDisponibles, DateOnly fechaReferencia,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Sin instrucción de tratamiento vigente no puede salir nada hacia el proveedor de IA.");
    }

    private sealed class DeteccionGestionQueLanzaSiSeInvoca : IDeteccionGestionCorreoService
    {
        public Task<Result<DeteccionGestionCorreoDto>> DetectarAsync(
            string cuerpoMensaje, IReadOnlyList<TrabajadorCandidatoGestionDto> trabajadoresDisponibles,
            IReadOnlyList<TipoDocumentoCandidatoGestionDto> tiposDocumentoDisponibles, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Sin instrucción de tratamiento vigente no puede salir nada hacia el proveedor de IA.");
    }

    private sealed class SugerenciaVisitaRepositorioQueLanzaSiSeInvoca : ISugerenciaVisitaCorreoRepository
    {
        public Task<SugerenciaVisitaCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No hay sugerencia que leer en este flujo.");

        public void Agregar(SugerenciaVisitaCorreo sugerencia) =>
            throw new InvalidOperationException("Sin detección no puede crearse ninguna sugerencia.");
    }

    private sealed class SugerenciaGestionRepositorioQueLanzaSiSeInvoca : ISugerenciaGestionCorreoRepository
    {
        public Task<SugerenciaGestionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No hay sugerencia que leer en este flujo.");

        public void Agregar(SugerenciaGestionCorreo sugerencia) =>
            throw new InvalidOperationException("Sin detección no puede crearse ninguna sugerencia.");
    }

    /// <summary>Cuenta accesos y devuelve vacío: el servicio se para solo después, por su propia regla.</summary>
    private sealed class CentrosQueryContextEspia : ICentrosQueryContext
    {
        public int Consultas { get; private set; }

        public IQueryable<Centro> Centros
        {
            get
            {
                Consultas++;
                return new TestAsyncQueryable<Centro>(new List<Centro>().AsQueryable());
            }
        }

        public IQueryable<CanalGestionDocumental> CanalesGestionDocumental =>
            new TestAsyncQueryable<CanalGestionDocumental>(new List<CanalGestionDocumental>().AsQueryable());
    }

    private sealed class AsignacionesQueryContextQueLanzaSiSeInvoca : IAsignacionesQueryContext
    {
        public IQueryable<Asignacion> Asignaciones =>
            throw new InvalidOperationException("Sin Centros no se cargan Asignaciones.");
    }

    private sealed class TrabajadoresQueryContextQueLanzaSiSeInvoca : ITrabajadoresQueryContext
    {
        public IQueryable<Trabajador> Trabajadores =>
            throw new InvalidOperationException("Sin Asignaciones no se cargan Trabajadores.");

        public IQueryable<DeteccionTrabajador> DeteccionesTrabajador =>
            throw new InvalidOperationException("Este flujo no lee detecciones de trabajador.");
    }

    private sealed class TiposDocumentoQueryContextQueLanzaSiSeInvoca : ITiposDocumentoQueryContext
    {
        public IQueryable<TipoDocumento> TiposDocumento =>
            throw new InvalidOperationException("Sin Trabajadores candidatos no se cargan TiposDocumento.");

        public IQueryable<TipoDocumentoCentro> TiposDocumentoCentros => throw new InvalidOperationException("No se usa en este flujo.");

        public IQueryable<TipoDocumentoAlias> TiposDocumentoAlias => throw new InvalidOperationException("No se usa en este flujo.");

        public IQueryable<ConfiguracionIaDocumentoCliente> ConfiguracionesIaDocumentoCliente => throw new InvalidOperationException("No se usa en este flujo.");
    }
}
