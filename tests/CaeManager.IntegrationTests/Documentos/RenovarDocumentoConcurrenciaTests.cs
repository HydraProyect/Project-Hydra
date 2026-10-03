using CaeManager.Application.Documentos;
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

        var documento = Documento.DeEmpresa(empresa.Id, tipoDocumento.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
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

    private static RenovarDocumentoCommandHandler ConstruirHandlerRenovar(CaeManagerDbContext contexto) =>
        new(
            new DocumentoRepository(contexto), contexto, new AlcanceDatosServiceFalso(), contexto,
            new ColaAnalisisDocumentoFalsa(), new CurrentUserServiceFalso(),
            new AcreditacionDocumentoPlataformaRepository(contexto), AltaAcreditacionesDePrueba.Con(contexto),
            new PublisherFalso(), contexto);

    /// <summary>Documento de la Empresa de la siembra con un archivo ya guardado (la clave vive en su fila).</summary>
    private async Task<(Guid DocumentoId, string ArchivoUrl)> SembrarDocumentoConArchivoAsync(string archivoUrl = "archivos/original.pdf")
    {
        await using var contexto = CrearContexto();

        var empresaId = await contexto.Empresas.Select(e => e.Id).FirstAsync();
        var tipoId = await contexto.TiposDocumento.Select(t => t.Id).FirstAsync();

        var documento = Documento.DeEmpresa(empresaId, tipoId, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca, archivoUrl);
        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();

        return (documento.Id, archivoUrl);
    }

    [Fact]
    public async Task Renovar_con_un_archivo_nuevo_crea_un_registro_nuevo_y_manda_el_anterior_al_historial_intacto()
    {
        var (anteriorId, archivoAnterior) = await SembrarDocumentoConArchivoAsync();

        Guid nuevoId;
        await using (var contexto = CrearContexto())
        {
            var resultado = await ConstruirHandlerRenovar(contexto).Handle(
                new RenovarDocumentoCommand(anteriorId, new DateOnly(2026, 2, 1), null, "archivos/renovado.pdf", "Renovado"),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            nuevoId = resultado.Valor;
        }

        nuevoId.Should().NotBe(anteriorId, "D8: renovar con archivo cambia el Id y no reutiliza el del anterior");

        await using var verificacion = CrearContexto();
        var anterior = await verificacion.Documentos.SingleAsync(d => d.Id == anteriorId);
        var nuevo = await verificacion.Documentos.SingleAsync(d => d.Id == nuevoId);

        anterior.EstaSustituido.Should().BeTrue();
        anterior.SustituidoPorDocumentoId.Should().Be(nuevoId);
        anterior.MotivoSustitucion.Should().Be(MotivoSustitucionDocumento.Renovacion);
        anterior.SustituidoEnUtc.Should().NotBeNull();

        // El historial es la evidencia de lo que estuvo en uso: ni el archivo ni las fechas ni los comentarios cambian.
        anterior.ArchivoUrl.Should().Be(archivoAnterior, "el archivo anterior se conserva, referenciado por su fila");
        anterior.FechaEmision.Should().Be(new DateOnly(2026, 1, 1));
        anterior.EstadoVigencia.Should().Be(EstadoVigenciaDocumento.NoCaduca);
        anterior.Comentarios.Should().BeNull();

        nuevo.EstaSustituido.Should().BeFalse();
        nuevo.ArchivoUrl.Should().Be("archivos/renovado.pdf");
        nuevo.FechaEmision.Should().Be(new DateOnly(2026, 2, 1));
        nuevo.Comentarios.Should().Be("Renovado");
        nuevo.EmpresaId.Should().Be(anterior.EmpresaId, "mismo titular");
        nuevo.TipoDocumentoId.Should().Be(anterior.TipoDocumentoId, "mismo Tipo");

        // En la unidad (Empresa, Tipo) el operativo es el nuevo y ya no el anterior.
        var operativos = await verificacion.Documentos.Operativos()
            .Where(d => d.EmpresaId == anterior.EmpresaId && d.TipoDocumentoId == anterior.TipoDocumentoId)
            .Select(d => d.Id).ToListAsync();
        operativos.Should().Contain(nuevoId).And.NotContain(anteriorId);
    }

    [Fact]
    public async Task Renovar_sin_archivo_nuevo_corrige_el_mismo_registro_y_devuelve_el_mismo_Id()
    {
        // Sin archivo nuevo no hay nada que sustituir: se corrigen fechas y comentarios en su sitio y el archivo
        // existente se queda donde está.
        var (documentoId, archivoUrl) = await SembrarDocumentoConArchivoAsync();
        int antes;
        await using (var conteo = CrearContexto())
            antes = await conteo.Documentos.CountAsync();

        await using (var contexto = CrearContexto())
        {
            var resultado = await ConstruirHandlerRenovar(contexto).Handle(
                new RenovarDocumentoCommand(documentoId, new DateOnly(2026, 2, 1), null, null, "Solo fechas"),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            resultado.Valor.Should().Be(documentoId);
        }

        await using var verificacion = CrearContexto();
        var documento = await verificacion.Documentos.SingleAsync(d => d.Id == documentoId);
        documento.EstaSustituido.Should().BeFalse();
        documento.ArchivoUrl.Should().Be(archivoUrl);
        documento.Comentarios.Should().Be("Solo fechas");
        (await verificacion.Documentos.CountAsync()).Should().Be(antes, "no nace ningún registro nuevo");
    }

    [Fact]
    public async Task Un_enlace_al_Id_antiguo_resuelve_al_historial_y_dice_quien_lo_sustituyo_D8()
    {
        var (anteriorId, _) = await SembrarDocumentoConArchivoAsync();
        Guid nuevoId;
        await using (var contexto = CrearContexto())
        {
            nuevoId = (await ConstruirHandlerRenovar(contexto).Handle(
                new RenovarDocumentoCommand(anteriorId, new DateOnly(2026, 2, 1), null, "archivos/renovado.pdf", null),
                CancellationToken.None)).Valor;
        }

        await using var consulta = CrearContexto();
        var historial = await ConstruirHandlerConsulta(consulta).Handle(new ObtenerDocumentoPorIdQuery(anteriorId), CancellationToken.None);
        var vigente = await ConstruirHandlerConsulta(consulta).Handle(new ObtenerDocumentoPorIdQuery(nuevoId), CancellationToken.None);

        historial.Should().NotBeNull("el Id antiguo sigue resolviendo: el sustituido conserva su identidad");
        historial!.SustitutoId.Should().Be(nuevoId);
        historial.SustituidoEn.Should().NotBeNull();
        vigente!.SustitutoId.Should().BeNull();
    }

    [Fact]
    public async Task Renovar_reenviando_el_archivo_que_ya_tiene_corrige_en_el_mismo_registro_y_no_comparte_el_blob()
    {
        // El formulario de edición reenvía el ArchivoUrl existente cuando solo se corrigen fechas.
        var (documentoId, archivoUrl) = await SembrarDocumentoConArchivoAsync();
        int antes;
        await using (var conteo = CrearContexto())
            antes = await conteo.Documentos.CountAsync();

        await using (var contexto = CrearContexto())
        {
            var resultado = await ConstruirHandlerRenovar(contexto).Handle(
                new RenovarDocumentoCommand(documentoId, new DateOnly(2026, 2, 1), null, archivoUrl, "Solo fechas"),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            resultado.Valor.Should().Be(documentoId, "no hay archivo nuevo: no hay documento nuevo");
        }

        await using var verificacion = CrearContexto();
        (await verificacion.Documentos.CountAsync()).Should().Be(antes);
        (await verificacion.Documentos.SingleAsync(d => d.Id == documentoId)).EstaSustituido.Should().BeFalse();
    }

    [Fact]
    public async Task Un_documento_del_historial_no_se_renueva_ni_con_archivo_ni_sin_el_y_no_cambia()
    {
        var (anteriorId, _) = await SembrarDocumentoConArchivoAsync();
        Guid nuevoId;
        await using (var contexto = CrearContexto())
        {
            nuevoId = (await ConstruirHandlerRenovar(contexto).Handle(
                new RenovarDocumentoCommand(anteriorId, new DateOnly(2026, 2, 1), null, "archivos/renovado.pdf", null),
                CancellationToken.None)).Valor;
        }

        int antes;
        await using (var conteo = CrearContexto())
            antes = await conteo.Documentos.CountAsync();

        foreach (var archivo in new string?[] { "archivos/otro.pdf", null })
        {
            await using var contexto = CrearContexto();
            var resultado = await ConstruirHandlerRenovar(contexto).Handle(
                new RenovarDocumentoCommand(anteriorId, new DateOnly(2026, 3, 1), null, archivo, "No debe aplicarse"),
                CancellationToken.None);

            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be(DocumentoEnHistorial.Codigo);
        }

        await using var verificacion = CrearContexto();
        var anterior = await verificacion.Documentos.SingleAsync(d => d.Id == anteriorId);
        anterior.FechaEmision.Should().Be(new DateOnly(2026, 1, 1));
        anterior.Comentarios.Should().BeNull();
        anterior.SustituidoPorDocumentoId.Should().Be(nuevoId, "el historial sigue apuntando a su único sustituto");
        (await verificacion.Documentos.CountAsync()).Should().Be(antes, "los rechazos no crean registros");
    }

    [Fact]
    public async Task El_historial_es_inmutable_en_el_dominio_CorregirVigencia_y_AdjuntarArchivo_lo_rechazan()
    {
        var (anteriorId, _) = await SembrarDocumentoConArchivoAsync();
        await using (var contexto = CrearContexto())
        {
            await ConstruirHandlerRenovar(contexto).Handle(
                new RenovarDocumentoCommand(anteriorId, new DateOnly(2026, 2, 1), null, "archivos/renovado.pdf", null), CancellationToken.None);
        }

        await using var verificacion = CrearContexto();
        var anterior = await verificacion.Documentos.SingleAsync(d => d.Id == anteriorId);

        var corregir = () => anterior.CorregirVigencia(new DateOnly(2026, 3, 1), VigenciaDocumento.NoCaduca);
        var adjuntar = () => anterior.AdjuntarArchivo("archivos/pisado.pdf");

        corregir.Should().Throw<InvalidOperationException>();
        adjuntar.Should().Throw<InvalidOperationException>();
        anterior.ArchivoUrl.Should().Be("archivos/original.pdf");
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

    private sealed class ColaAnalisisDocumentoFalsa : ITrabajoAnalisisDocumentoRepository
    {
        public void Agregar(TrabajoAnalisisDocumento trabajo)
        {
        }

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TrabajoAnalisisDocumento?>(null);

        public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TrabajoAnalisisDocumento?>(null);

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(
            TimeSpan umbral, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrabajoAnalisisDocumento>>([]);

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
