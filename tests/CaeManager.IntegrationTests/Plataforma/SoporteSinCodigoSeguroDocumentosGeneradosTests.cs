using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerValidacionOficialDocumento;
using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// <c>cae_app_soporte</c> no lee <c>DocumentosGenerados.CodigoSeguroVerificacion</c> (OWASP ASVS 4.0.3 V4.1.3),
/// y el listado de documentos generados sigue funcionando con ese rol. Lo mide en la base migrada, con una
/// fila real: <c>has_column_privilege</c> incluye herencia de roles y <c>PUBLIC</c>, de modo que un grant de
/// tabla que anulara la revocación por columna pondría en rojo el primer test.
/// </summary>
public class SoporteSinCodigoSeguroDocumentosGeneradosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenant;
    private Guid _documentoGeneradoId;
    private Guid _documentoId;
    private string _razonSocial = "";

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        var tenant = new Tenant("Tenant visitado de prueba");
        _tenant = tenant.Id;

        await using var contexto = CrearContexto();
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();

        var tipo = new TipoDocumento("Ficha de prueba", 12, true, 1, AmbitoAplicacion.Empresa);
        var empresa = new Empresa("Empresa de prueba S.L.", "B10380194");
        var plantilla = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Plantilla de prueba", AmbitoAplicacion.Empresa, FormatoOrigenPlantilla.PdfVisual, tipo.Id);
        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "plantilla-de-prueba.pdf", new string('a', 64));
        contexto.TiposDocumento.Add(tipo);
        contexto.Empresas.Add(empresa);
        contexto.PlantillasDocumento.Add(plantilla);
        contexto.PlantillasDocumentoVersion.Add(version);
        await contexto.SaveChangesAsync();

        var documento = Documento.DeEmpresa(empresa.Id, tipo.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca);
        contexto.Documentos.Add(documento);
        contexto.VerificacionesDocumentoOficial.Add(new VerificacionDocumentoOficial(
            documento.Id, PerfilDocumentoOficial.CorrienteTgss, NivelConfianzaDocumental.FirmaValidaSinRevocacion,
            "CSV-123", null, null, null, null, ResultadoCotejoDocumentoOficial.Coincide,
            DecisionValidacionOficial.AutoValidado, ""));
        _documentoId = documento.Id;
        var generado = new DocumentoGenerado(
            version.Id, documento.Id, "{}", Guid.NewGuid(), DateTime.UtcNow, empresaId: empresa.Id, conAvisos: true);
        contexto.DocumentosGenerados.Add(generado);
        await contexto.SaveChangesAsync();

        _documentoGeneradoId = generado.Id;
        _razonSocial = empresa.RazonSocial;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Soporte_lee_las_columnas_permitidas_del_documento_generado()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT a.attname, has_column_privilege('cae_app_soporte', a.attrelid, a.attnum, 'SELECT')
            FROM pg_attribute a
            WHERE a.attrelid = 'public."DocumentosGenerados"'::regclass AND a.attnum > 0 AND NOT a.attisdropped;
            """;

        var todas = new List<string>();
        var legibles = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            todas.Add(lector.GetString(0));
            if (lector.GetBoolean(1)) legibles.Add(lector.GetString(0));
        }

        todas.Should().Contain("CodigoSeguroVerificacion", "control del instrumento: la columna existe");
        legibles.Should().BeEquivalentTo(todas.Where(c => c != "CodigoSeguroVerificacion" && c != "DatosUtilizadosJson"));
        legibles.Should().BeEquivalentTo(new[]
        {
            "Id", "CentroId", "CreadoEnUtc", "DocumentoId", "EliminadoEnUtc",
            "EliminadoPorUsuarioId", "EmpresaId", "EstaEliminado", "Estado", "GeneradoEnUtc",
            "GeneradoPorUsuarioId", "HashSha256Impreso", "PlantillaDocumentoVersionId",
            "SelloElectronicoAplicadoEnUtc", "TenantId", "TrabajadorId", "Version",
        });
    }

    [Theory]
    [InlineData("SELECT \"CodigoSeguroVerificacion\" FROM \"DocumentosGenerados\";")]
    [InlineData("SELECT * FROM \"DocumentosGenerados\";")]
    public async Task Soporte_no_puede_leer_el_codigo_con_sql_directo(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenant);
        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_soporte;";
        await setRol.ExecuteNonQueryAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        var leer = async () => await comando.ExecuteScalarAsync();
        (await leer.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_el_listado_y_su_contador_funcionan_sin_leer_el_codigo()
    {
        await using var contexto = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());

        var listado = await new ObtenerDocumentosGeneradosQueryHandler(contexto, contexto, contexto, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerDocumentosGeneradosQuery(), CancellationToken.None);
        var fila = listado.Should().ContainSingle().Subject;
        fila.DocumentoGeneradoId.Should().Be(_documentoGeneradoId);
        fila.PlantillaNombre.Should().Be("Plantilla de prueba");
        fila.EmpresaRazonSocial.Should().Be(_razonSocial);

        var conAvisos = await new ObtenerTotalDocumentosGeneradosConAvisosQueryHandler(contexto, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerTotalDocumentosGeneradosConAvisosQuery(), CancellationToken.None);
        conAvisos.Should().Be(1);
    }

    [Fact]
    public async Task Materializar_la_entidad_falla_solo_en_Sesion_Privilegiada()
    {
        // Guarda de no regresión: la migración solo toca cae_app_soporte; el runtime sigue leyendo la fila.
        await using var contextoRuntime = CrearContexto(cadenaConexion: BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        (await contextoRuntime.DocumentosGenerados.ToListAsync()).Should().ContainSingle(
            "control del instrumento: sin Sesión Privilegiada la fila existe y se materializa");

        await using var contextoSoporte = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());
        var materializar = async () => await contextoSoporte.DocumentosGenerados.ToListAsync();
        (await materializar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Materializar_verificaciones_o_eventos_de_webhook_falla_en_Sesion_Privilegiada()
    {
        await using var contextoRuntime = CrearContexto(cadenaConexion: BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        (await contextoRuntime.VerificacionesDocumentoOficial.ToListAsync()).Should().ContainSingle(
            "control del instrumento: sin Sesión Privilegiada la fila existe y se materializa");
        await contextoRuntime.EventosWebhook.ToListAsync();

        await using var contextoSoporte = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());
        var verificaciones = async () => await contextoSoporte.VerificacionesDocumentoOficial.ToListAsync();
        (await verificaciones.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        var eventos = async () => await contextoSoporte.EventosWebhook.ToListAsync();
        (await eventos.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Consulta_de_verificacion_funciona_con_ambos_roles()
    {
        var sesion = Guid.NewGuid();
        await using var soporte = CrearContexto(sesionPrivilegiadaId: sesion);
        var resultadoSoporte = await new ObtenerValidacionOficialDocumentoQueryHandler(
            soporte, new ClienteActivoSeleccionadoFalso(_tenant, sesion)).Handle(new(_documentoId), CancellationToken.None);
        resultadoSoporte.Should().NotBeNull();
        resultadoSoporte!.CodigoVerificacionDisponible.Should().BeFalse();
        resultadoSoporte.CodigoVerificacion.Should().BeNull();

        await using var runtime = CrearContexto(cadenaConexion: BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        var resultadoRuntime = await new ObtenerValidacionOficialDocumentoQueryHandler(
            runtime, new ClienteActivoSeleccionadoFalso(_tenant, null)).Handle(new(_documentoId), CancellationToken.None);
        resultadoRuntime.Should().NotBeNull();
        resultadoRuntime!.CodigoVerificacionDisponible.Should().BeTrue();
        resultadoRuntime.CodigoVerificacion.Should().Be("CSV-123");
    }

    private CaeManagerDbContext CrearContexto(Guid? sesionPrivilegiadaId = null, string? cadenaConexion = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaConexion ?? _cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual,
                    new ClienteActivoSeleccionadoFalso(_tenant, sesionPrivilegiadaId),
                    new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenant), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid tenantId, Guid? sesionPrivilegiadaId) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
