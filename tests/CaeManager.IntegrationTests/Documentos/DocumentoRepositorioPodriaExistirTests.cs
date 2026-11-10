using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// <see cref="DocumentoRepository.PodriaExistirAsync"/> decide si la compensación de GenerarDocumentoIndividual borra un
/// PDF (revisión puente de la ronda 3 sobre la PR #2121): cuenta un Documento eliminado del Tenant propietario, no uno
/// de otro Tenant, y sin Tenant propietario resuelto responde que sí.
/// </summary>
public class DocumentoRepositorioPodriaExistirTests : IAsyncLifetime
{
    private readonly string _conexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_conexion);

    [Fact]
    public async Task Cuenta_un_documento_eliminado_del_tenant_propietario()
    {
        var eliminado = await SembrarDocumentoAsync(_tenant, eliminado: true);

        await using var contexto = CrearContexto(_tenant);
        (await new DocumentoRepository(contexto).PodriaExistirAsync(eliminado.Id)).Should().BeTrue();
        (await new DocumentoRepository(contexto).PodriaExistirAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task No_cuenta_un_documento_de_otro_tenant()
    {
        var ajeno = await SembrarDocumentoAsync(_otroTenant, eliminado: false);

        await using var contexto = CrearContexto(_tenant);
        (await new DocumentoRepository(contexto).PodriaExistirAsync(ajeno.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Sin_tenant_propietario_responde_que_podria_existir()
    {
        await using var contexto = CrearContexto(null);
        (await new DocumentoRepository(contexto).PodriaExistirAsync(Guid.NewGuid())).Should().BeTrue();
    }

    private async Task<Documento> SembrarDocumentoAsync(Guid tenant, bool eliminado)
    {
        await using var contexto = CrearContexto(tenant);
        var empresa = new Empresa("Montajes Ebro S.L.", "B12345674");
        var tipo = new TipoDocumento("Plan de prevención", null, false, 1, AmbitoAplicacion.Empresa);
        contexto.Empresas.Add(empresa);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var documento = Documento.DeEmpresa(empresa.Id, tipo.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca, "y/plan.pdf");
        if (eliminado)
            documento.MarcarComoEliminado(Guid.NewGuid());
        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();
        return documento;
    }

    private CaeManagerDbContext CrearContexto(Guid? tenant)
    {
        var actual = new TenantActualAmbiental { TenantId = tenant };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_conexion, o => o.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(actual)).Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), actual);
    }
}
