using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using CaeManager.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Mismo escenario que <c>EditarClienteConcurrenciaTests</c>, pero para
/// <see cref="RenovarDocumentoCommand"/>: por su propio comentario XML es la
/// edición real de Documento, y el agregado más disputado de todo el sistema
/// — varios gestores CAE pueden tener la misma ficha abierta a la vez.
///
/// Test de integración y no de Application con fakes, a diferencia de
/// EditarClienteConcurrenciaTests: el handler depende de
/// <c>ITiposDocumentoQueryContext</c>/<c>IProyectosQueryContext</c>, que ya
/// implementa el propio <c>CaeManagerDbContext</c> — replicarlos a mano en
/// fakes solo para este test no probaría nada que este patrón (ya usado en
/// AcreditacionDocumentoPlataformaSincronizacionTests) no cubra mejor.
/// </summary>
public class RenovarDocumentoConcurrenciaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };
    private Guid _documentoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        // Ámbito Empresa a propósito: es el que menos montaje necesita (sin
        // Centro/Asignación) y no aporta nada al caso de concurrencia, que
        // vive por completo en el Command/handler.
        var empresa = new Empresa("Empresa Renovación Concurrencia S.L.", "B10380186");
        contexto.Empresas.Add(empresa);
        var tipoDocumento = new TipoDocumento("Certificado RC", 12, true, 1, AmbitoAplicacion.Empresa);
        contexto.TiposDocumento.Add(tipoDocumento);
        await contexto.SaveChangesAsync();

        var documento = Documento.DeEmpresa(empresa.Id, tipoDocumento.Id, new DateOnly(2026, 1, 1), null);
        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();

        _documentoId = documento.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Renovar_con_una_version_vieja_no_pisa_al_que_guardo_antes()
    {
        // 1. Las dos personas abren la misma ficha: cada una se queda con la
        // Version que vio en pantalla.
        Guid versionQueVeA;
        Guid versionQueVeB;
        await using (var contexto = CrearContexto())
        {
            var detalle = await ConstruirHandlerConsulta(contexto).Handle(new ObtenerDocumentoPorIdQuery(_documentoId), CancellationToken.None);
            versionQueVeA = detalle!.Version;
            versionQueVeB = detalle.Version;
        }

        // 2. A renueva primero.
        await using (var contexto = CrearContexto())
        {
            var resultado = await ConstruirHandlerRenovar(contexto).Handle(
                new RenovarDocumentoCommand(_documentoId, new DateOnly(2026, 2, 1), null, null, "Renovado por A", versionQueVeA),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        // 3. B renueva con la versión que tenía en pantalla, ya caducada.
        await using (var contexto = CrearContexto())
        {
            var resultado = await ConstruirHandlerRenovar(contexto).Handle(
                new RenovarDocumentoCommand(_documentoId, new DateOnly(2026, 3, 1), null, null, "Renovado por B", versionQueVeB),
                CancellationToken.None);

            resultado.EsFallido.Should().BeTrue("B trae una versión que ya no es la vigente");
            resultado.Error.Codigo.Should().Be("Concurrencia.Conflicto");
        }

        // 4. Lo que quedó guardado es lo de A, no lo de B.
        await using var verificacion = CrearContexto();
        var almacenado = await verificacion.Documentos.FirstAsync(d => d.Id == _documentoId);
        almacenado.Comentarios.Should().Be("Renovado por A");
        almacenado.FechaEmision.Should().Be(new DateOnly(2026, 2, 1));
    }

    [Fact]
    public async Task Renovar_con_la_version_vigente_se_aplica()
    {
        await using var contexto = CrearContexto();
        var detalle = await ConstruirHandlerConsulta(contexto).Handle(new ObtenerDocumentoPorIdQuery(_documentoId), CancellationToken.None);

        var resultado = await ConstruirHandlerRenovar(contexto).Handle(
            new RenovarDocumentoCommand(_documentoId, new DateOnly(2026, 2, 1), null, null, "Al día", detalle!.Version),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Un_llamador_que_no_propaga_version_sigue_funcionando()
    {
        // Guid.Empty = "sin comprobación" — el caso de
        // ActualizarDocumentoDesdeAdjuntoCommand, que decide crear-o-renovar
        // sin que haya habido una pantalla abierta de antemano.
        await using var contexto = CrearContexto();

        var resultado = await ConstruirHandlerRenovar(contexto).Handle(
            new RenovarDocumentoCommand(_documentoId, new DateOnly(2026, 2, 1), null, null, "Sin version"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    private static ObtenerDocumentoPorIdQueryHandler ConstruirHandlerConsulta(CaeManagerDbContext contexto) =>
        new(contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso());

    private static RenovarDocumentoCommandHandler ConstruirHandlerRenovar(
        CaeManagerDbContext contexto, IFileStorageService? almacenamiento = null) =>
        new(
            new DocumentoRepository(contexto), contexto, new AlcanceDatosServiceFalso(), contexto,
            new ColaAnalisisDocumentoFalsa(), new CurrentUserServiceFalso(),
            new AcreditacionDocumentoPlataformaRepository(contexto), new PublisherFalso(), contexto,
            almacenamiento ?? new AlmacenamientoFalso(),
            NullLogger<RenovarDocumentoCommandHandler>.Instance);

    /// <summary>Documento con archivo adjunto ya guardado en el almacén falso.</summary>
    private async Task<(Guid DocumentoId, string ArchivoUrl)> SembrarDocumentoConArchivoAsync(AlmacenamientoFalso almacen)
    {
        await using var contexto = CrearContexto();

        var empresaId = await contexto.Empresas.Select(e => e.Id).FirstAsync();
        var tipoId = await contexto.TiposDocumento.Select(t => t.Id).FirstAsync();

        using var contenido = new MemoryStream("pdf original"u8.ToArray());
        var archivoUrl = await almacen.GuardarAsync(contenido, "original.pdf");

        var documento = Documento.DeEmpresa(empresaId, tipoId, new DateOnly(2026, 1, 1), null, archivoUrl);
        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();

        return (documento.Id, archivoUrl);
    }

    [Fact]
    public async Task Renovar_con_un_archivo_nuevo_borra_el_anterior()
    {
        // AdjuntarArchivo sobreescribe ArchivoUrl, así que sin capturar la
        // clave anterior antes se perdía para siempre: el PDF de la versión
        // vieja —datos médicos— se quedaba en almacenamiento sin ninguna fila
        // que lo nombrara, fuera del alcance de la retención y de cualquier
        // purga. Cada renovación dejaba una copia irrecuperable.
        var almacen = new AlmacenamientoFalso();
        var (documentoId, archivoAnterior) = await SembrarDocumentoConArchivoAsync(almacen);

        using var nuevo = new MemoryStream("pdf renovado"u8.ToArray());
        var archivoNuevo = await almacen.GuardarAsync(nuevo, "renovado.pdf");

        await using (var contexto = CrearContexto())
        {
            var resultado = await ConstruirHandlerRenovar(contexto, almacen).Handle(
                new RenovarDocumentoCommand(documentoId, new DateOnly(2026, 2, 1), null, archivoNuevo, null),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        almacen.Existe(archivoAnterior).Should().BeFalse("el archivo de la versión anterior ya no lo referencia nadie");
        almacen.Existe(archivoNuevo).Should().BeTrue("el archivo vigente no se puede tocar");
    }

    [Fact]
    public async Task Renovar_sin_archivo_nuevo_no_borra_el_que_ya_tenia()
    {
        // Control negativo, y el que de verdad importa: renovar solo las
        // fechas deja el mismo archivo adjunto. Borrarlo aquí no sería una
        // limpieza, sería destruir el documento vigente.
        var almacen = new AlmacenamientoFalso();
        var (documentoId, archivoUrl) = await SembrarDocumentoConArchivoAsync(almacen);

        await using (var contexto = CrearContexto())
        {
            var resultado = await ConstruirHandlerRenovar(contexto, almacen).Handle(
                new RenovarDocumentoCommand(documentoId, new DateOnly(2026, 2, 1), null, null, "Solo fechas"),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        almacen.Existe(archivoUrl).Should().BeTrue();
    }

    private CaeManagerDbContext CrearContexto()
    {
        // ConcurrenciaOptimistaInterceptor incluido (a diferencia de
        // AcreditacionDocumentoPlataformaSincronizacionTests, que no lo
        // necesita): sin él, Version nunca se renueva al guardar y la
        // primera renovación de A no dejaría nada que detectar cuando B
        // llega con la versión vieja.
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    private sealed class AlmacenamientoFalso : IFileStorageService
    {
        private readonly Dictionary<string, byte[]> _archivos = [];
        private int _contador;

        public bool Existe(string identificador) => _archivos.ContainsKey(identificador);

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            using var memoria = new MemoryStream();
            contenido.CopyTo(memoria);
            var identificador = $"falso-{++_contador}-{nombreArchivoOriginal}";
            _archivos[identificador] = memoria.ToArray();
            return Task.FromResult(identificador);
        }

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            _archivos.TryGetValue(identificador, out var contenido)
                ? Task.FromResult<Stream>(new MemoryStream(contenido))
                : throw new FileNotFoundException("No encontramos el archivo solicitado.", identificador);

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
        {
            _archivos.Remove(identificador);
            return Task.CompletedTask;
        }
    }

    private sealed class ColaAnalisisDocumentoFalsa : ITrabajoAnalisisDocumentoRepository
    {
        public void Agregar(TrabajoAnalisisDocumento trabajo)
        {
        }

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TrabajoAnalisisDocumento?>(null);

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(
            TimeSpan umbral, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrabajoAnalisisDocumento>>([]);

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
