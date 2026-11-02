using CaeManager.Application.Common;
using CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// El responsable se persiste y un Administrador de otro Tenant no alcanza el buzón ni conociendo su Id: la
/// prueba de Application usa un repositorio falso y no puede demostrar el filtro por Tenant.
/// </summary>
public class FijarResponsableBuzonAislamientoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private Guid _conexionDeA;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantA);
        await contexto.Database.MigrateAsync();

        var conexion = new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE");
        contexto.ConexionesIntegracion.Add(conexion);
        await contexto.SaveChangesAsync();
        _conexionDeA = conexion.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_fija_el_responsable()
    {
        var responsable = Guid.NewGuid();
        await using (var contexto = CrearContexto(_tenantA))
        {
            var resultado = await CrearHandler(contexto)
                .Handle(new FijarResponsableBuzonCommand(_conexionDeA, responsable), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        await using var lectura = CrearContexto(_tenantA);
        (await lectura.ConexionesIntegracion.SingleAsync(c => c.Id == _conexionDeA))
            .ResponsableUsuarioId.Should().Be(responsable);
    }

    [Fact]
    public async Task Un_Administrador_de_otro_Tenant_no_alcanza_el_buzon_con_su_Id()
    {
        await using (var contexto = CrearContexto(_tenantB))
        {
            var resultado = await CrearHandler(contexto)
                .Handle(new FijarResponsableBuzonCommand(_conexionDeA, Guid.NewGuid()), CancellationToken.None);
            resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        }

        await using var lectura = CrearContexto(_tenantA);
        (await lectura.ConexionesIntegracion.SingleAsync(c => c.Id == _conexionDeA))
            .ResponsableUsuarioId.Should().BeNull();
    }

    private static FijarResponsableBuzonCommandHandler CrearHandler(CaeManagerDbContext contexto) =>
        new(new ConexionIntegracionRepository(contexto), new AlcanceDatosServiceFalso(),
            new Directorio(), new CurrentUserServiceFalso(rol: "Administrador"), contexto);

    private sealed class Directorio : IDirectorioUsuariosService
    {
        public Task<bool> EsVisibleEnTenantActualAsync(Guid id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        public Task<bool> EsCuentaActivaConRolAsync(Guid id, Guid tenant, string rol, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(Guid tenant, string rol, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
