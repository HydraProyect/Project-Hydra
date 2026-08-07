using CaeManager.Application.Common;
using CaeManager.Application.Documentos.ValidacionOficial;
using CaeManager.Application.Documentos.ValidacionOficial.Parsers;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// El pipeline de validación oficial se prueba contra PostgreSQL real
/// (mismo arnés que VerificacionIaDocumentoServiceTests); la verificación
/// criptográfica y la extracción de texto se sustituyen por fakes
/// deterministas — el motor real ya tiene los suyos
/// (VerificadorFirmaPdfServiceTests). Los parsers son los reales.
/// </summary>
public class ValidacionDocumentoOficialServiceTests : IAsyncLifetime
{
    private const string CifEmpresa = "B12345674";

    private const string TextoTgssValido =
        "TESORERÍA GENERAL DE LA SEGURIDAD SOCIAL Razón social: IBERTEC Código de Empresario: 0B12345674 " +
        "El presente certificado tiene carácter POSITIVO " +
        "Información obtenida a 4 de agosto de 2026 Código Electrónico de Autenticidad: ABC123DEF456GHI7";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _dbContext = null!;
    private Empresa _empresa = null!;
    private TipoDocumento _tipoCorrienteTgss = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        _empresa = new Empresa("Ibertec S.A.", CifEmpresa);
        _dbContext.Empresas.Add(_empresa);
        await _dbContext.SaveChangesAsync();

        // El tipo seed "Certificado de estar al corriente con la Seguridad
        // Social" ya trae el perfil asignado por TipoDocumentoSeedData.
        _tipoCorrienteTgss = await _dbContext.TiposDocumento
            .FirstAsync(t => t.PerfilDocumentoOficial == PerfilDocumentoOficial.CorrienteTgss);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    private ValidacionDocumentoOficialService CrearServicio(
        Result<ResultadoVerificacionFirmasPdf> firmas, Result<IReadOnlyList<string>>? texto = null) =>
        new(_dbContext, _dbContext, _dbContext, _dbContext,
            new AlmacenamientoFalso(),
            new VerificadorFirmaFalso(firmas),
            new ExtractorTextoFalso(texto ?? Result.Exito<IReadOnlyList<string>>([TextoTgssValido])),
            new ParserDocumentoOficialRegistry([
                new ParserCorrienteTgss(), new ParserCorrienteAeat(), new ParserIta(), new ParserRnt(), new ParserRlc()]),
            new FirmaDigitalDocumentoRepository(_dbContext),
            new VerificacionDocumentoOficialRepository(_dbContext),
            new AprobacionDocumentoRepository(_dbContext),
            new RevisionIaDocumentoRepository(_dbContext),
            _dbContext,
            NullLogger<ValidacionDocumentoOficialService>.Instance);

    private async Task<Documento> CrearDocumentoAsync(DateOnly? fechaEmision = null)
    {
        var documento = Documento.DeEmpresa(
            _empresa.Id, _tipoCorrienteTgss.Id, fechaEmision ?? new DateOnly(2026, 8, 4), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();
        return documento;
    }

    private static ResultadoVerificacionFirmasPdf FirmaSelloDeOrgano(
        NivelConfianzaDocumental nivel = NivelConfianzaDocumental.FirmaValida,
        EstadoFirmaPdf estado = EstadoFirmaPdf.Valida,
        bool esSelloDeOrgano = true) =>
        new(nivel,
        [
            new FirmaPdfVerificada(
                0, estado, CadenaConfiable: nivel >= NivelConfianzaDocumental.FirmaValidaSinRevocacion,
                ComprobacionRevocacion.ComprobadaEnLinea, "AC Administración Pública", "0A1B2C",
                esSelloDeOrgano, "TESORERIA GENERAL DE LA SEGURIDAD SOCIAL", "Q2827003A",
                new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc), TieneSelloDeTiempo: true,
                CubreDocumentoCompleto: true, MotivoInvalidez: null),
        ]);

    [Fact]
    public async Task Auto_valida_cuando_la_firma_es_valida_y_los_datos_cotejan()
    {
        var documento = await CrearDocumentoAsync();
        var servicio = CrearServicio(Result.Exito(FirmaSelloDeOrgano()));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var verificacion = await _dbContext.VerificacionesDocumentoOficial.SingleAsync(v => v.DocumentoId == documento.Id);
        verificacion.Decision.Should().Be(DecisionValidacionOficial.AutoValidado);
        verificacion.ResultadoCotejo.Should().Be(ResultadoCotejoDocumentoOficial.Coincide);
        verificacion.CodigoVerificacion.Should().Be("ABC123DEF456GHI7");
        verificacion.NivelConfianza.Should().Be(NivelConfianzaDocumental.FirmaValida);

        var aprobaciones = await _dbContext.AprobacionesDocumento.Where(a => a.DocumentoId == documento.Id).ToListAsync();
        aprobaciones.Should().ContainSingle().Which.Tipo.Should().Be(TipoAprobacionDocumento.Automatica);

        var firmas = await _dbContext.FirmasDigitalesDocumento.Where(f => f.DocumentoId == documento.Id).ToListAsync();
        firmas.Should().ContainSingle().Which.FirmanteNif.Should().Be("Q2827003A");
    }

    [Fact]
    public async Task Un_documento_manipulado_queda_sin_firma_valida_y_sin_aprobacion()
    {
        var documento = await CrearDocumentoAsync();
        var servicio = CrearServicio(Result.Exito(FirmaSelloDeOrgano(
            NivelConfianzaDocumental.Manipulado, EstadoFirmaPdf.Invalida)));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var verificacion = await _dbContext.VerificacionesDocumentoOficial.SingleAsync(v => v.DocumentoId == documento.Id);
        verificacion.Decision.Should().Be(DecisionValidacionOficial.SinFirmaValida);
        verificacion.NivelConfianza.Should().Be(NivelConfianzaDocumental.Manipulado);
        verificacion.Motivos.Should().Contain("alterado");

        (await _dbContext.AprobacionesDocumento.AnyAsync(a => a.DocumentoId == documento.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Un_certificado_en_negativo_exige_revision_aunque_la_firma_sea_valida()
    {
        var documento = await CrearDocumentoAsync();
        var textoNegativo = TextoTgssValido.Replace("tiene carácter POSITIVO", "tiene carácter NEGATIVO");
        var servicio = CrearServicio(
            Result.Exito(FirmaSelloDeOrgano()), Result.Exito<IReadOnlyList<string>>([textoNegativo]));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var verificacion = await _dbContext.VerificacionesDocumentoOficial.SingleAsync(v => v.DocumentoId == documento.Id);
        verificacion.Decision.Should().Be(DecisionValidacionOficial.RevisionRequerida);
        verificacion.Motivos.Should().Contain("NEGATIVO");
        (await _dbContext.AprobacionesDocumento.AnyAsync(a => a.DocumentoId == documento.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Un_cif_discrepante_exige_revision()
    {
        var documento = await CrearDocumentoAsync();
        var textoOtroCif = TextoTgssValido.Replace("B12345674", "B99999999");
        var servicio = CrearServicio(
            Result.Exito(FirmaSelloDeOrgano()), Result.Exito<IReadOnlyList<string>>([textoOtroCif]));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var verificacion = await _dbContext.VerificacionesDocumentoOficial.SingleAsync(v => v.DocumentoId == documento.Id);
        verificacion.Decision.Should().Be(DecisionValidacionOficial.RevisionRequerida);
        verificacion.ResultadoCotejo.Should().Be(ResultadoCotejoDocumentoOficial.Discrepancia);
        verificacion.Motivos.Should().Contain("B99999999");
    }

    [Fact]
    public async Task Sin_firma_digital_no_hay_circuito_oficial_pero_si_extraccion()
    {
        var documento = await CrearDocumentoAsync();
        var servicio = CrearServicio(Result.Exito(
            new ResultadoVerificacionFirmasPdf(NivelConfianzaDocumental.SoloLectura, [])));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var verificacion = await _dbContext.VerificacionesDocumentoOficial.SingleAsync(v => v.DocumentoId == documento.Id);
        verificacion.Decision.Should().Be(DecisionValidacionOficial.SinFirmaValida);
        verificacion.Motivos.Should().Contain("no está firmado");
        // La extracción corre igualmente: el CEA guardado es el insumo de la
        // verificación oficial contra el organismo (épica 3) — también para
        // documentos que llegan sin firma.
        verificacion.CodigoVerificacion.Should().Be("ABC123DEF456GHI7");
    }

    [Fact]
    public async Task Una_reimpresion_recibe_un_motivo_accionable()
    {
        var documento = await CrearDocumentoAsync();
        var servicio = CrearServicio(Result.Exito(
            new ResultadoVerificacionFirmasPdf(NivelConfianzaDocumental.SoloLectura, [], AparentaReimpresion: true)));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var verificacion = await _dbContext.VerificacionesDocumentoOficial.SingleAsync(v => v.DocumentoId == documento.Id);
        verificacion.Decision.Should().Be(DecisionValidacionOficial.SinFirmaValida);
        verificacion.Motivos.Should().Contain("reimpresión");
        verificacion.Motivos.Should().Contain("original");
    }

    [Fact]
    public async Task Reprocesar_reemplaza_el_resultado_anterior_en_vez_de_acumular()
    {
        var documento = await CrearDocumentoAsync();
        await CrearServicio(Result.Exito(
            new ResultadoVerificacionFirmasPdf(NivelConfianzaDocumental.SoloLectura, [])))
            .ProcesarDocumentoAsync(documento.Id);

        // Renovación: el archivo nuevo sí viene firmado y coteja.
        await CrearServicio(Result.Exito(FirmaSelloDeOrgano())).ProcesarDocumentoAsync(documento.Id);

        var verificaciones = await _dbContext.VerificacionesDocumentoOficial
            .Where(v => v.DocumentoId == documento.Id).ToListAsync();
        verificaciones.Should().ContainSingle().Which.Decision.Should().Be(DecisionValidacionOficial.AutoValidado);
    }

    [Fact]
    public async Task El_firmante_persona_fisica_no_se_persiste_hasta_confirmar_rgpd()
    {
        var documento = await CrearDocumentoAsync();
        var servicio = CrearServicio(Result.Exito(FirmaSelloDeOrgano(esSelloDeOrgano: false)));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var firma = await _dbContext.FirmasDigitalesDocumento.SingleAsync(f => f.DocumentoId == documento.Id);
        firma.FirmanteNombre.Should().BeNull();
        firma.FirmanteNif.Should().BeNull();
        firma.EsSelloDeOrgano.Should().BeFalse();
    }

    /// <summary>
    /// Documento firmado y válido cuyo texto no trae un CIF reconocible con
    /// el prefijo esperado (ver ParserRlc/ParserDocumentoOficialBase): el
    /// parser lo declara obligatorio faltante y cae a revisión — nunca se
    /// auto-valida a ciegas.
    /// </summary>
    [Fact]
    public async Task Rlc_sin_cif_reconocible_cae_a_revision()
    {
        var tipoRlc = await _dbContext.TiposDocumento
            .FirstAsync(t => t.PerfilDocumentoOficial == PerfilDocumentoOficial.Rlc);
        var documento = Documento.DeEmpresa(_empresa.Id, tipoRlc.Id, new DateOnly(2026, 8, 4), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var textoSinCif = "Raz!n social C!digo cuenta cotizaci!n Periodo de liquidaci!n EMPRESA sin identificador 06/2026";
        var servicio = CrearServicio(
            Result.Exito(FirmaSelloDeOrgano()), Result.Exito<IReadOnlyList<string>>([textoSinCif]));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var verificacion = await _dbContext.VerificacionesDocumentoOficial.SingleAsync(v => v.DocumentoId == documento.Id);
        verificacion.Decision.Should().Be(DecisionValidacionOficial.RevisionRequerida);
        verificacion.ResultadoCotejo.Should().Be(ResultadoCotejoDocumentoOficial.ParserSinDatos);
        (await _dbContext.AprobacionesDocumento.AnyAsync(a => a.DocumentoId == documento.Id)).Should().BeFalse();
    }

    /// <summary>
    /// El caso feliz de RLC/RNT/ITA: CIF con el prefijo numérico real
    /// (confirmado por el usuario) coincide con el propietario y auto-valida.
    /// </summary>
    [Fact]
    public async Task Rlc_con_cif_prefijado_coincidente_se_auto_valida()
    {
        var tipoRlc = await _dbContext.TiposDocumento
            .FirstAsync(t => t.PerfilDocumentoOficial == PerfilDocumentoOficial.Rlc);
        // La fecha de emisión registrada debe ser el día 1 del mes del
        // periodo (confirmado por el usuario) para que el cotejo coincida.
        var documento = Documento.DeEmpresa(_empresa.Id, tipoRlc.Id, new DateOnly(2026, 8, 1), null, "archivo.pdf");
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var texto = $"Código de Empresario: 90{CifEmpresa} Período de liquidación: 08/2026 Huella: 1A2B3C4D5E6F7G8H";
        var servicio = CrearServicio(
            Result.Exito(FirmaSelloDeOrgano()), Result.Exito<IReadOnlyList<string>>([texto]));

        await servicio.ProcesarDocumentoAsync(documento.Id);

        var verificacion = await _dbContext.VerificacionesDocumentoOficial.SingleAsync(v => v.DocumentoId == documento.Id);
        verificacion.Decision.Should().Be(DecisionValidacionOficial.AutoValidado);
        verificacion.PeriodoDetectado.Should().Be("2026-08");
    }

    private sealed class VerificadorFirmaFalso(Result<ResultadoVerificacionFirmasPdf> resultado) : IVerificadorFirmaPdfService
    {
        public Task<Result<ResultadoVerificacionFirmasPdf>> VerificarAsync(byte[] contenidoPdf, CancellationToken cancellationToken = default) =>
            Task.FromResult(resultado);
    }

    private sealed class ExtractorTextoFalso(Result<IReadOnlyList<string>> resultado) : IExtractorTextoDigitalService
    {
        public Result<IReadOnlyList<string>> ExtraerTextoPorPagina(byte[] contenidoPdf) => resultado;
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
