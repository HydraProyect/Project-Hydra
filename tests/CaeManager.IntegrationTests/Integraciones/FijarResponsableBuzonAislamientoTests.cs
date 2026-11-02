using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Autorizacion;
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

    [Fact]
    public async Task Ser_responsable_de_un_buzon_es_un_vinculo_operativo_que_impide_la_baja()
    {
        var responsable = Guid.NewGuid();
        await using var contexto = CrearContexto(_tenantA);
        (await contexto.ConexionesIntegracion.SingleAsync(c => c.Id == _conexionDeA)).FijarResponsable(responsable);
        await contexto.SaveChangesAsync();

        var directorio = new DirectorioUsuariosTenant(
            null!, contexto, new TenantActualAmbiental { TenantId = _tenantA }, new PuertaAccesoDatos(), contexto, null!);

        (await directorio.TieneVinculoOperativoAsync(responsable)).Should().BeTrue();
        (await directorio.TieneVinculoOperativoAsync(Guid.NewGuid())).Should().BeFalse();
    }

    private static FijarResponsableBuzonCommandHandler CrearHandler(CaeManagerDbContext contexto) =>
        new(new ConexionIntegracionRepository(contexto), new AlcanceDatosServiceFalso(),
            new Directorio(), new CurrentUserServiceFalso(rol: "Administrador"), contexto);

    private sealed class Directorio : IDirectorioDestinosCartera
    {
        public Task<DestinoCartera?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DestinoCartera?>(new(Activa: true, "GestorCae", null, false));
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
