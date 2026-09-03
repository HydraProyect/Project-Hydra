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
            new RegistroAccesoDocumentoSensibleRepository(_dbContext), _dbContext);

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
}

/// <summary>Actor fijo, para no depender de una sesión real en estas pruebas.</summary>
internal class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
{
    public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
    public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
}
