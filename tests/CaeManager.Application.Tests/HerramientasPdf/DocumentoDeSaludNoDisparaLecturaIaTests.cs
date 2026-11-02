using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Verificacion;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Trabajadores.Deteccion;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.HerramientasPdf;

/// <summary>
/// Dividir delega en el alta normal, así que un Documento creado con tipo de salud sigue la misma guarda: aunque el
/// alta encole el análisis porque el tipo lo tiene marcado, el servicio que lo procesa corta en
/// <c>LecturaIaActiva</c> antes de abrir el archivo o tocar un proveedor. La prueba atraviesa las dos piezas reales: el
/// trabajo que encola <see cref="CrearDocumentoCommandHandler"/> se entrega al servicio de verdad, cuyos puertos de IA
/// y de archivo fallan si alguien los llama.
/// </summary>
public class DocumentoDeSaludNoDisparaLecturaIaTests
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly MundoAcreditaciones _mundo = new();
    private readonly DocumentoRepositorioFalso _documentos = new();
    private readonly ColaEnMemoria _cola = new();
    private readonly List<string> _llamadasProhibidas = [];

    private TipoDocumento TipoDeSalud(AmbitoAplicacion ambito)
    {
        var tipo = new TipoDocumento("Certificado de aptitud médica", 12, true, 1, ambito,
            sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud);
        tipo.EstablecerVerificacionIaActiva(true);
        tipo.EstablecerDeteccionTrabajadoresActiva(true);
        _mundo.TiposDocumentoContexto.ListaTiposDocumento.Add(tipo);
        return tipo;
    }

    private async Task<Documento> CrearAsync(CrearDocumentoCommand comando, EmpresasQueryContextFalso empresas)
    {
        var handler = new CrearDocumentoCommandHandler(
            _documentos, _mundo.TiposDocumentoContexto, _mundo.TrabajadoresContexto, new SinVehiculos(),
            new ProyectosQueryContextFalso(), empresas, new UnitOfWorkFalso(),
            _cola, new CurrentUserServiceFalso(), _mundo.Servicio(), new PublisherFalso(),
            new AlcanceDatosServiceFalso(), new VerificadorArchivoAlmacenadoPermisivo());

        var creado = await handler.Handle(comando, CancellationToken.None);
        creado.EsExitoso.Should().BeTrue(creado.Error.Mensaje);

        var documento = _documentos.Documentos.Single();
        _mundo.DocumentosContexto.ListaDocumentos.Add(documento);
        return documento;
    }

    private T Prohibido<T>() where T : class => Prohibicion<T>.Crear(_llamadasProhibidas);

    [Fact]
    public async Task Un_tipo_de_salud_de_Trabajador_encola_la_verificacion_pero_no_llega_a_la_IA()
    {
        var tipo = TipoDeSalud(AmbitoAplicacion.Trabajador);
        tipo.LecturaIaActiva.Should().BeFalse();
        var trabajador = _mundo.Trabajador();

        var documento = await CrearAsync(
            new CrearDocumentoCommand(trabajador.Id, null, null, null, null, tipo.Id, Hoy, null, "tramo.pdf", null),
            new EmpresasQueryContextFalso());

        _cola.Trabajos.Should().ContainSingle(t => t.Tipo == TipoAnalisisDocumento.VerificacionIa,
            "si no se encolara, esta prueba no demostraría nada de la guarda del procesador");

        var verificacion = new VerificacionIaDocumentoService(
            _mundo.DocumentosContexto, _mundo.TiposDocumentoContexto,
            Prohibido<IFileStorageService>(), Prohibido<IExtraccionMetadatosDocumentoIaService>(),
            Prohibido<IRevisionIaDocumentoRepository>(), Prohibido<IAuditoriaExtraccionIaRepository>(),
            new InstruccionHabilitada(), new TenantFijo(_tenantId), Prohibido<ITransaccionDocumentoBloqueado>(),
            new UnitOfWorkFalso(), Prohibido<IPresupuestoMemoriaDocumentos>());

        await verificacion.ProcesarDocumentoAsync(EncargoVerificacionIa.De(_cola.Trabajos.Single()));

        _llamadasProhibidas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_tipo_de_salud_de_Empresa_no_llega_a_la_deteccion_de_Trabajadores()
    {
        var tipo = TipoDeSalud(AmbitoAplicacion.Empresa);
        var empresas = new EmpresasQueryContextFalso();
        var empresa = new Domain.Empresas.Empresa("Montajes Norte S.L.");
        empresas.ListaEmpresas.Add(empresa);

        var documento = await CrearAsync(
            new CrearDocumentoCommand(null, null, empresa.Id, null, null, tipo.Id, Hoy, null, "tramo.pdf", null), empresas);

        _cola.Trabajos.Should().ContainSingle(t => t.Tipo == TipoAnalisisDocumento.DeteccionTrabajadores);

        var deteccion = new DeteccionTrabajadoresService(
            _mundo.DocumentosContexto, empresas, _mundo.TiposDocumentoContexto, _mundo.TrabajadoresContexto,
            Prohibido<IFileStorageService>(), Prohibido<IExtraccionTrabajadoresIaService>(),
            Prohibido<IDeteccionTrabajadorRepository>(), Prohibido<INotificacionUsuarioRepository>(),
            new InstruccionHabilitada(), new TenantFijo(_tenantId), new UnitOfWorkFalso(), Prohibido<IPresupuestoMemoriaDocumentos>());

        await deteccion.ProcesarDocumentoAsync(documento.Id);

        _llamadasProhibidas.Should().BeEmpty();
    }

    /// <summary>Puerto que deja constancia de cualquier llamada; los servicios atrapan fallos, así que un throw no bastaría.</summary>
    public class Prohibicion<T> : DispatchProxy where T : class
    {
        private List<string> _llamadas = [];

        public static T Crear(List<string> llamadas)
        {
            var proxy = Create<T, Prohibicion<T>>();
            ((Prohibicion<T>)(object)proxy)._llamadas = llamadas;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _llamadas.Add($"{typeof(T).Name}.{targetMethod?.Name}");
            throw new InvalidOperationException($"{typeof(T).Name}.{targetMethod?.Name} no debía llamarse.");
        }
    }

    private sealed class ColaEnMemoria : ITrabajoAnalisisDocumentoRepository
    {
        public List<TrabajoAnalisisDocumento> Trabajos { get; } = [];

        public void Agregar(TrabajoAnalisisDocumento trabajo) => Trabajos.Add(trabajo);

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(TimeSpan umbral, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SinVehiculos : IVehiculosQueryContext
    {
        public IQueryable<Vehiculo> Vehiculos => new TestAsyncQueryable<Vehiculo>(Array.Empty<Vehiculo>().AsQueryable());
    }

    private sealed class TenantFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class InstruccionHabilitada : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
