using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Documentos.Verificacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Una verificación IA que llega tarde (p. ej. tras días con los servicios de
/// fondo parados) nunca pisa una decisión manual ni se aplica sobre un
/// Documento que ya no es el que se encoló. Con el cableado de producción
/// (<see cref="ArnesDeArranqueRuntime"/>: <c>cae_app_runtime</c>, RLS y los
/// cuatro interceptores), porque la comprobación decisiva vive en una
/// transacción con <c>FOR UPDATE</c> en SQL crudo, que solo PostgreSQL bajo
/// la política del Tenant propietario puede demostrar.
///
/// Dos momentos distintos, a propósito: la decisión manual o la renovación
/// ANTES de procesar (la detecta ya la comprobación previa, sin pagar la
/// llamada a la IA) y DURANTE la extracción (solo la detecta la comprobación
/// dentro de la transacción, que es la que cuenta).
/// </summary>
public class VerificacionIaTardiaBajoRuntimeTests
{
    private static readonly DateOnly Emision = new(2026, 9, 1);

    private readonly Guid _tenantPropietario = Guid.NewGuid();
    private readonly TenantActualAmbiental _tenant = new();

    [Fact]
    public async Task Sin_cambios_desde_el_encolado_aprueba_automaticamente()
    {
        await using var arnes = await CrearArnesAsync();
        var (documentoId, encargo) = await SembrarDocumentoEncoladoAsync(arnes);

        var extraccion = new ExtraccionQueCoincide();
        var descarte = await ProcesarAsync(arnes, encargo, extraccion);

        descarte.Should().BeNull();
        extraccion.Llamadas.Should().Be(1);
        (await LeerAprobacionesComoPropietarioAsync(arnes, documentoId))
            .Should().ContainSingle().Which.Should().Be((TipoAprobacionDocumento.Automatica, _tenantPropietario));
        (await ContarRevisionesComoPropietarioAsync(arnes, documentoId)).Should().Be(0);
    }

    [Fact]
    public async Task Una_decision_manual_antes_de_procesar_impide_la_aprobacion_automatica()
    {
        await using var arnes = await CrearArnesAsync();
        var (documentoId, encargo) = await SembrarDocumentoEncoladoAsync(arnes);
        await DecidirAManoAsync(arnes, documentoId);

        var extraccion = new ExtraccionQueCoincide();
        var descarte = await ProcesarAsync(arnes, encargo, extraccion);

        descarte.Should().Contain("a mano");
        extraccion.Llamadas.Should().Be(0, "descartable antes de leer: no se paga la llamada al proveedor de IA");
        (await LeerAprobacionesComoPropietarioAsync(arnes, documentoId))
            .Should().ContainSingle().Which.Tipo.Should().Be(TipoAprobacionDocumento.Manual,
                "la decisión manual prevalece y la verificación no añade nada");
        (await ContarRevisionesComoPropietarioAsync(arnes, documentoId)).Should().Be(0);
    }

    [Fact]
    public async Task Un_Documento_renovado_antes_de_procesar_descarta_la_verificacion()
    {
        await using var arnes = await CrearArnesAsync();
        var (documentoId, encargo) = await SembrarDocumentoEncoladoAsync(arnes);
        await RenovarAsync(arnes, documentoId);

        var extraccion = new ExtraccionQueCoincide();
        var descarte = await ProcesarAsync(arnes, encargo, extraccion);

        descarte.Should().Contain("cambió");
        extraccion.Llamadas.Should().Be(0);
        (await LeerAprobacionesComoPropietarioAsync(arnes, documentoId)).Should().BeEmpty(
            "la renovación conserva la fecha de emisión y cambia el archivo: sin el descarte, la verificación del archivo viejo aprobaría el nuevo");
        (await ContarRevisionesComoPropietarioAsync(arnes, documentoId)).Should().Be(0);
    }

    [Fact]
    public async Task Una_decision_manual_durante_la_extraccion_la_ve_la_comprobacion_de_la_transaccion()
    {
        await using var arnes = await CrearArnesAsync();
        var (documentoId, encargo) = await SembrarDocumentoEncoladoAsync(arnes);

        var extraccion = new ExtraccionQueCoincide(() => DecidirAManoAsync(arnes, documentoId));
        var descarte = await ProcesarAsync(arnes, encargo, extraccion);

        descarte.Should().Contain("a mano");
        extraccion.Llamadas.Should().Be(1, "la decisión llega después de la comprobación previa");
        (await LeerAprobacionesComoPropietarioAsync(arnes, documentoId))
            .Should().ContainSingle().Which.Tipo.Should().Be(TipoAprobacionDocumento.Manual);
    }

    [Fact]
    public async Task Una_renovacion_durante_la_extraccion_la_ve_la_comprobacion_de_la_transaccion()
    {
        await using var arnes = await CrearArnesAsync();
        var (documentoId, encargo) = await SembrarDocumentoEncoladoAsync(arnes);

        var extraccion = new ExtraccionQueCoincide(() => RenovarAsync(arnes, documentoId));
        var descarte = await ProcesarAsync(arnes, encargo, extraccion);

        descarte.Should().Contain("cambió");
        extraccion.Llamadas.Should().Be(1);
        (await LeerAprobacionesComoPropietarioAsync(arnes, documentoId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Una_revision_pendiente_aparecida_durante_la_extraccion_descarta_la_aprobacion()
    {
        await using var arnes = await CrearArnesAsync();
        var (documentoId, encargo) = await SembrarDocumentoEncoladoAsync(arnes);

        var extraccion = new ExtraccionQueCoincide(async () => await CrearRevisionPendienteAsync(arnes, documentoId));
        var descarte = await ProcesarAsync(arnes, encargo, extraccion);

        descarte.Should().Contain("pendiente de decisión manual");
        (await LeerAprobacionesComoPropietarioAsync(arnes, documentoId)).Should().BeEmpty();
    }

    /// <summary>
    /// La carrera que señaló la revisión de Codex: una decisión manual en
    /// curso, sin confirmar, mientras la verificación escribe. La aprobación
    /// manual no bloquea nada por sí sola; lo que serializa es que resuelve la
    /// revisión en la misma transacción. La verificación debe esperar a ese
    /// commit y entonces ver la decisión — no adelantarse con la revisión
    /// todavía pendiente ni coexistir con la aprobación manual.
    /// </summary>
    [Fact]
    public async Task Una_decision_manual_sin_confirmar_bloquea_la_verificacion_y_prevalece()
    {
        await using var arnes = await CrearArnesAsync();
        var (documentoId, encargo) = await SembrarDocumentoEncoladoAsync(arnes);
        var retardoCommit = TimeSpan.FromSeconds(2);
        Task? decisionEnCurso = null;

        var extraccion = new ExtraccionQueCoincide(async () =>
        {
            var revisionId = await CrearRevisionPendienteAsync(arnes, documentoId);
            decisionEnCurso = await EmpezarDecisionManualSinConfirmarAsync(arnes, revisionId, retardoCommit);
        });

        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var descarte = await ProcesarAsync(arnes, encargo, extraccion);
        cronometro.Stop();
        await decisionEnCurso!;

        descarte.Should().Contain("a mano", "esperó al commit de la decisión manual y la vio");
        cronometro.Elapsed.Should().BeGreaterThan(retardoCommit - TimeSpan.FromMilliseconds(300),
            "el bloqueo de la revisión obliga a esperar a la transacción manual");
        (await LeerAprobacionesComoPropietarioAsync(arnes, documentoId))
            .Should().ContainSingle().Which.Tipo.Should().Be(TipoAprobacionDocumento.Manual);
    }

    private async Task<Guid> CrearRevisionPendienteAsync(ArnesDeArranqueRuntime arnes, Guid documentoId)
    {
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var revision = RevisionIaDocumento.Crear(documentoId, 60, "Apto médico", Emision, null, true, "Confianza baja (60%)");
        contexto.RevisionesIaDocumento.Add(revision);
        await contexto.SaveChangesAsync();
        return revision.Id;
    }

    /// <summary>
    /// Como <c>ResolverRevisionIaDocumentoCommand</c>, pero con la transacción
    /// abierta: resuelve la revisión y añade la aprobación manual, y confirma
    /// pasado <paramref name="retardo"/> en segundo plano.
    /// </summary>
    private static async Task<Task> EmpezarDecisionManualSinConfirmarAsync(
        ArnesDeArranqueRuntime arnes, Guid revisionId, TimeSpan retardo)
    {
        var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var transaccion = await contexto.Database.BeginTransactionAsync();
        var revision = await contexto.RevisionesIaDocumento.SingleAsync(r => r.Id == revisionId);
        revision.Resolver();
        contexto.AprobacionesDocumento.Add(AprobacionDocumento.CrearManual(revision.DocumentoId, revision.ConfianzaGeneral, Guid.NewGuid()));
        await contexto.SaveChangesAsync();

        return Task.Run(async () =>
        {
            await Task.Delay(retardo);
            await transaccion.CommitAsync();
            await transaccion.DisposeAsync();
            await scope.DisposeAsync();
        });
    }

    private Task<ArnesDeArranqueRuntime> CrearArnesAsync() =>
        ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false, tenantActualPersonalizado: _tenant);

    /// <summary>
    /// Como <c>CrearDocumentoCommand</c>: el Documento de Trabajador con su
    /// archivo, y el encargo con la hora y la versión del encolado.
    /// </summary>
    private async Task<(Guid DocumentoId, EncargoVerificacionIa Encargo)> SembrarDocumentoEncoladoAsync(ArnesDeArranqueRuntime arnes)
    {
        _tenant.TenantId = _tenantPropietario;
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var empresa = new Empresa("Empresa Runtime S.L.", "B10380186");
        contexto.Empresas.Add(empresa);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "Gómez", "12345678Z");
        contexto.Trabajadores.Add(trabajador);
        var tipo = new TipoDocumento("Apto médico", 12, aplicaVencimientoAutomatico: true, orden: 1,
            AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        tipo.EstablecerLecturaIaActiva(true);
        tipo.EstablecerVerificacionIaActiva(true);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, Emision, VigenciaDocumento.NoCaduca, "original.pdf");
        contexto.Documentos.Add(documento);
        var encargo = new EncargoVerificacionIa(documento.Id, DateTime.UtcNow, documento.Version);
        await contexto.SaveChangesAsync();

        return (documento.Id, encargo);
    }

    private async Task<string?> ProcesarAsync(
        ArnesDeArranqueRuntime arnes, EncargoVerificacionIa encargo, IExtraccionMetadatosDocumentoIaService extraccion)
    {
        _tenant.TenantId = _tenantPropietario;
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        (await contexto.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync())
            .Should().Be("cae_app_runtime");

        var servicio = ActivatorUtilities.CreateInstance<VerificacionIaDocumentoService>(
            scope.ServiceProvider, new AlmacenamientoFalso(), extraccion, new InstruccionSiempreHabilitada());
        return await servicio.ProcesarDocumentoAsync(encargo);
    }

    /// <summary>Como <c>ResolverRevisionIaDocumentoCommand</c>, en su propio ámbito y conexión: la AprobacionDocumento manual.</summary>
    private async Task DecidirAManoAsync(ArnesDeArranqueRuntime arnes, Guid documentoId)
    {
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        contexto.AprobacionesDocumento.Add(AprobacionDocumento.CrearManual(documentoId, 80, Guid.NewGuid()));
        await contexto.SaveChangesAsync();
    }

    /// <summary>Como <c>RenovarDocumentoCommand</c>: misma fecha de emisión, archivo nuevo.</summary>
    private async Task RenovarAsync(ArnesDeArranqueRuntime arnes, Guid documentoId)
    {
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var documento = await contexto.Documentos.SingleAsync(d => d.Id == documentoId);
        documento.Renovar(Emision, VigenciaDocumento.NoCaduca);
        documento.AdjuntarArchivo("renovado.pdf");
        await contexto.SaveChangesAsync();
    }

    private static async Task<List<(TipoAprobacionDocumento Tipo, Guid TenantId)>> LeerAprobacionesComoPropietarioAsync(
        ArnesDeArranqueRuntime arnes, Guid documentoId)
    {
        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = """
            SELECT "Tipo", "TenantId" FROM "AprobacionesDocumento" WHERE "DocumentoId" = @documento;
            """;
        consulta.Parameters.AddWithValue("documento", documentoId);

        var filas = new List<(TipoAprobacionDocumento, Guid)>();
        await using var lector = await consulta.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            filas.Add((Enum.Parse<TipoAprobacionDocumento>(lector.GetValue(0).ToString()!), lector.GetGuid(1)));
        return filas;
    }

    private static async Task<long> ContarRevisionesComoPropietarioAsync(ArnesDeArranqueRuntime arnes, Guid documentoId)
    {
        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = """SELECT count(*) FROM "RevisionesIaDocumento" WHERE "DocumentoId" = @documento;""";
        consulta.Parameters.AddWithValue("documento", documentoId);
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Lo que devolvería el proveedor de IA para el Documento sembrado: todo
    /// coincide y con evidencia, así que sin descarte el resultado es una
    /// aprobación automática. <paramref name="durante"/> simula lo que un
    /// usuario hace mientras el proveedor responde.
    /// </summary>
    private sealed class ExtraccionQueCoincide(Func<Task>? durante = null) : IExtraccionMetadatosDocumentoIaService
    {
        public int Llamadas { get; private set; }

        public async Task<Result<MetadatosDocumentoExtraidosDto>> ExtraerAsync(
            byte[] contenidoPdf, string nombreTipoDocumento, Guid? documentoId = null, CancellationToken cancellationToken = default)
        {
            Llamadas++;
            if (durante is not null)
                await durante();
            return Result.Exito(new MetadatosDocumentoExtraidosDto("Apto médico", Emision, null, true, 99, null));
        }
    }

    private sealed class InstruccionSiempreHabilitada : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class AlmacenamientoFalso : IFileStorageService
    {
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            Task.FromResult("falso.pdf");

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
    }
}
