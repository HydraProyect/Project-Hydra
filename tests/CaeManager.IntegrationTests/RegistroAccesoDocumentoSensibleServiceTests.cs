using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
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
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// RegistroAccesoDocumentoSensibleService (DEC-36, REC-099) depende del
/// catálogo real de TipoDocumento (con su Sensibilidad ya clasificada por
/// TipoDocumentoSeedData) y de RLS sobre la tabla nueva — se prueba contra
/// Postgres real, mismo criterio que VerificacionIaDocumentoServiceTests.
/// </summary>
public class RegistroAccesoDocumentoSensibleServiceTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _dbContext = null!;
    private TenantActualAmbiental _tenantActual = null!;
    private Empresa _empresa = null!;

    public async Task InitializeAsync()
    {
        _tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
        await _dbContext.Database.MigrateAsync();

        _empresa = new Empresa("Ibertec S.A.");
        _dbContext.Empresas.Add(_empresa);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    private RegistroAccesoDocumentoSensibleService CrearServicio(ActorAuditoria actor) =>
        new(_dbContext, _dbContext, new ActorAuditoriaFalso(actor),
            new RegistroAccesoDocumentoSensibleRepository(_dbContext, NullLogger<RegistroAccesoDocumentoSensibleRepository>.Instance));

    private async Task<TipoDocumento> TipoConSensibilidadAsync(SensibilidadDocumental sensibilidad) =>
        await _dbContext.TiposDocumento.FirstAsync(t => t.Sensibilidad == sensibilidad);

    private Documento CrearDocumentoDeEmpresa(Guid tipoDocumentoId) =>
        Documento.DeEmpresa(_empresa.Id, tipoDocumentoId, DateOnly.FromDateTime(DateTime.UtcNow), null, "archivo.pdf");

    [Fact]
    public async Task Registra_el_acceso_cuando_el_tipo_revela_salud()
    {
        var tipo = await TipoConSensibilidadAsync(SensibilidadDocumental.CategoriaEspecialSalud);
        var documento = CrearDocumentoDeEmpresa(tipo.Id);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var usuarioId = Guid.NewGuid();
        var servicio = CrearServicio(ActorAuditoria.Normal(usuarioId));

        await servicio.RegistrarSiSensibleAsync(documento.Id, TipoAccesoDocumentoSensible.Apertura);

        var registros = await _dbContext.RegistrosAccesoDocumentoSensible.Where(r => r.DocumentoId == documento.Id).ToListAsync();
        registros.Should().ContainSingle();
        registros[0].Sensibilidad.Should().Be(SensibilidadDocumental.CategoriaEspecialSalud);
        registros[0].TipoAcceso.Should().Be(TipoAccesoDocumentoSensible.Apertura);
        registros[0].UsuarioId.Should().Be(usuarioId);
        registros[0].EsPrivilegiado.Should().BeFalse();
    }

    [Fact]
    public async Task Registra_el_acceso_cuando_el_tipo_tiene_datos_personales_sin_ser_de_salud()
    {
        var tipo = await TipoConSensibilidadAsync(SensibilidadDocumental.DatosPersonales);
        var documento = CrearDocumentoDeEmpresa(tipo.Id);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicio = CrearServicio(ActorAuditoria.Normal(Guid.NewGuid()));

        await servicio.RegistrarSiSensibleAsync(documento.Id, TipoAccesoDocumentoSensible.Apertura);

        (await _dbContext.RegistrosAccesoDocumentoSensible.CountAsync(r => r.DocumentoId == documento.Id)).Should().Be(1);
    }

    /// <summary>
    /// Control negativo del criterio 4 (HO-099-01 § 13/14): un Documento sin
    /// datos personales NO genera fila. Sin el control positivo de arriba,
    /// este test pasaría también si el registro estuviera roto del todo.
    /// </summary>
    [Fact]
    public async Task No_registra_el_acceso_cuando_el_tipo_no_tiene_datos_personales()
    {
        var tipo = await TipoConSensibilidadAsync(SensibilidadDocumental.SinDatosPersonales);
        var documento = CrearDocumentoDeEmpresa(tipo.Id);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicio = CrearServicio(ActorAuditoria.Normal(Guid.NewGuid()));

        await servicio.RegistrarSiSensibleAsync(documento.Id, TipoAccesoDocumentoSensible.Apertura);

        (await _dbContext.RegistrosAccesoDocumentoSensible.CountAsync(r => r.DocumentoId == documento.Id)).Should().Be(0);
    }

    /// <summary>
    /// Documento no resoluble (baja física, caso raro): se registra igual
    /// con la categoría más protectora en vez de perder el acceso en
    /// silencio (riesgo #3 del handoff: "registrar de menos... miente por
    /// omisión, que es peor que no tenerlo").
    /// </summary>
    [Fact]
    public async Task Registra_con_la_categoria_mas_protectora_cuando_el_documento_no_se_puede_resolver()
    {
        var documentoIdInexistente = Guid.NewGuid();
        var servicio = CrearServicio(ActorAuditoria.Normal(Guid.NewGuid()));

        await servicio.RegistrarSiSensibleAsync(documentoIdInexistente, TipoAccesoDocumentoSensible.Apertura);

        var registro = await _dbContext.RegistrosAccesoDocumentoSensible.SingleAsync(r => r.DocumentoId == documentoIdInexistente);
        registro.Sensibilidad.Should().Be(SensibilidadDocumental.CategoriaEspecialSalud);
    }

    [Fact]
    public async Task Registra_via_privilegiada_cuando_el_actor_opera_bajo_sesion_privilegiada()
    {
        var tipo = await TipoConSensibilidadAsync(SensibilidadDocumental.CategoriaEspecialSalud);
        var documento = CrearDocumentoDeEmpresa(tipo.Id);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var sesionId = Guid.NewGuid();
        var servicio = CrearServicio(new ActorAuditoria(Guid.NewGuid(), null, TipoViaAcceso.SesionPrivilegiada, sesionId));

        await servicio.RegistrarSiSensibleAsync(documento.Id, TipoAccesoDocumentoSensible.Apertura);

        var registro = await _dbContext.RegistrosAccesoDocumentoSensible.SingleAsync(r => r.DocumentoId == documento.Id);
        registro.EsPrivilegiado.Should().BeTrue();
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.SesionPrivilegiada);
        registro.ViaAccesoId.Should().Be(sesionId);
    }

    /// <summary>
    /// RLS (HO-099-01 § 9): un tenant distinto no ve las filas del primero,
    /// aunque conozca el TenantId no se puede leer sin AmbitoTenantExplicito.
    /// Segunda línea de defensa además del filtro global de EF.
    /// </summary>
    [Fact]
    public async Task Rls_impide_leer_filas_de_otro_tenant()
    {
        var tipo = await TipoConSensibilidadAsync(SensibilidadDocumental.CategoriaEspecialSalud);
        var documento = CrearDocumentoDeEmpresa(tipo.Id);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicio = CrearServicio(ActorAuditoria.Normal(Guid.NewGuid()));
        await servicio.RegistrarSiSensibleAsync(documento.Id, TipoAccesoDocumentoSensible.Apertura);

        _tenantActual.TenantId = Guid.NewGuid();
        try
        {
            var visibleDesdeOtroTenant = await _dbContext.RegistrosAccesoDocumentoSensible
                .AnyAsync(r => r.DocumentoId == documento.Id);

            visibleDesdeOtroTenant.Should().BeFalse();
        }
        finally
        {
            _tenantActual.TenantId = TenantSeedData.IdPorDefecto;
        }
    }

    /// <summary>
    /// Append-only a nivel de base, no solo de código C# (Codex, HO-099-01):
    /// conecta como <c>cae_app_runtime</c> real (login, no SET ROLE desde el
    /// propietario) y comprueba que ni siquiera un UPDATE/DELETE por SQL
    /// crudo puede tocar una fila ya escrita — la migración
    /// HabilitarRlsRegistrosAccesoDocumentoSensible revoca esos dos
    /// privilegios específicamente sobre esta tabla.
    /// </summary>
    [Fact]
    public async Task Cae_app_runtime_no_puede_actualizar_ni_borrar_filas_por_sql_directo()
    {
        var tipo = await TipoConSensibilidadAsync(SensibilidadDocumental.CategoriaEspecialSalud);
        var documento = CrearDocumentoDeEmpresa(tipo.Id);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var servicio = CrearServicio(ActorAuditoria.Normal(Guid.NewGuid()));
        await servicio.RegistrarSiSensibleAsync(documento.Id, TipoAccesoDocumentoSensible.Apertura);

        await using var conexionRuntime = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        await conexionRuntime.OpenAsync();
        await using var comandoSetTenant = conexionRuntime.CreateCommand();
        comandoSetTenant.CommandText = "SELECT set_config('app.tenant_id', $1, false);";
        comandoSetTenant.Parameters.AddWithValue(TenantSeedData.IdPorDefecto.ToString());
        await comandoSetTenant.ExecuteNonQueryAsync();

        await using var comandoUpdate = conexionRuntime.CreateCommand();
        comandoUpdate.CommandText = """UPDATE "RegistrosAccesoDocumentoSensible" SET "TipoAcceso" = 'VersionAnterior';""";
        var intentoUpdate = () => comandoUpdate.ExecuteNonQueryAsync();
        (await intentoUpdate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");

        await using var comandoDelete = conexionRuntime.CreateCommand();
        comandoDelete.CommandText = """DELETE FROM "RegistrosAccesoDocumentoSensible";""";
        var intentoDelete = () => comandoDelete.ExecuteNonQueryAsync();
        (await intentoDelete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");

        // Control positivo del propio instrumento: SELECT sigue funcionando
        // con el mismo rol — si esto fallara, los dos rechazos de arriba no
        // demostrarían el REVOKE, demostrarían una conexión rota.
        await using var comandoSelect = conexionRuntime.CreateCommand();
        comandoSelect.CommandText = """SELECT COUNT(*) FROM "RegistrosAccesoDocumentoSensible" WHERE "DocumentoId" = $1;""";
        comandoSelect.Parameters.AddWithValue(documento.Id);
        var total = (long)(await comandoSelect.ExecuteScalarAsync())!;
        total.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// La conexión real de una sesión privilegiada de plataforma (ADR-011 §
    /// 4bis) adopta <c>cae_app_soporte</c> con <c>SET ROLE</c>, sin
    /// privilegio de escritura sobre ninguna tabla — deliberado (ver
    /// RolSoporteSoloLectura). El repositorio debe tolerar ese fallo, no
    /// romper la descarga que lo desencadenó (Codex, HO-099-01).
    /// </summary>
    [Fact]
    public async Task El_repositorio_no_revienta_cuando_la_sesion_no_tiene_privilegio_de_escritura()
    {
        await using var conexionSoporte = new NpgsqlConnection(_cadenaConexion);
        await conexionSoporte.OpenAsync();
        await using (var comandoRol = conexionSoporte.CreateCommand())
        {
            comandoRol.CommandText = "SET ROLE cae_app_soporte;";
            await comandoRol.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(conexionSoporte, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        await using var dbContextSoporte = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
        var repositorio = new RegistroAccesoDocumentoSensibleRepository(dbContextSoporte, NullLogger<RegistroAccesoDocumentoSensibleRepository>.Instance);

        var registro = new RegistroAccesoDocumentoSensible(
            Guid.NewGuid(), SensibilidadDocumental.CategoriaEspecialSalud, TipoAccesoDocumentoSensible.Apertura,
            Guid.NewGuid(), Guid.NewGuid(), TipoViaAccesoAuditoria.SesionPrivilegiada, Guid.NewGuid());

        var guardar = () => repositorio.GuardarAsync(registro);

        (await guardar.Should().NotThrowAsync()).Which.Should().BeFalse();
    }

    /// <summary>
    /// DEC-36 (REC-099): el registro no debe filtrarse a la auditoría
    /// general, visible por cualquier Administrador sin el permiso
    /// específico — AuditoriaInterceptor debe excluir esta entidad igual que
    /// ya excluye RegistroAuditoria (Codex, HO-099-01).
    /// </summary>
    [Fact]
    public async Task No_genera_una_fila_duplicada_en_la_auditoria_general()
    {
        var tipo = await TipoConSensibilidadAsync(SensibilidadDocumental.CategoriaEspecialSalud);
        var documento = CrearDocumentoDeEmpresa(tipo.Id);
        _dbContext.Documentos.Add(documento);
        await _dbContext.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantActual),
                new CaeManager.Infrastructure.Auditing.AuditoriaInterceptor(new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid()))))
            .Options;

        await using var dbContextConAuditoria = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
        var repositorio = new RegistroAccesoDocumentoSensibleRepository(dbContextConAuditoria, NullLogger<RegistroAccesoDocumentoSensibleRepository>.Instance);

        var registro = new RegistroAccesoDocumentoSensible(
            documento.Id, SensibilidadDocumental.CategoriaEspecialSalud, TipoAccesoDocumentoSensible.Apertura,
            Guid.NewGuid(), Guid.NewGuid(), TipoViaAccesoAuditoria.Normal, null);

        (await repositorio.GuardarAsync(registro)).Should().BeTrue();

        var filasEnAuditoriaGeneral = await _dbContext.RegistrosAuditoria
            .CountAsync(r => r.EntidadTipo == nameof(RegistroAccesoDocumentoSensible));

        filasEnAuditoriaGeneral.Should().Be(0);
    }
}

/// <summary>Actor fijo, para no depender de una sesión real en estas pruebas.</summary>
internal class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
{
    public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
    public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
}
