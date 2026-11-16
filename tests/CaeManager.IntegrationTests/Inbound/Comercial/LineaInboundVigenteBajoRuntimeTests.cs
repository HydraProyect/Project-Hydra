using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Comercial.Queries.LineaInboundVigente;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Inbound.Comercial;

/// <summary>
/// <see cref="LineaInboundVigenteQuery"/> contra PostgreSQL real y como <c>cae_app_runtime</c> con contexto firmado: la
/// Query se traduce a SQL, el runtime puede leer lo que necesita, y cada Tenant activo ve solo su línea. A tiene la línea
/// Inbound vigente; B no tiene ninguna, pero A sí (la de A no le cuenta); C la tuvo y la dio de baja. La mitad positiva
/// (A) evita que los rechazos pasen con una lectura que fallara siempre. La regla fila a fila la prueban los tests de
/// Application; el aislamiento de la tabla, <c>ProductosContratadosBajoRuntimeTests</c>.
/// </summary>
public class LineaInboundVigenteBajoRuntimeTests : IAsyncLifetime
{
    // Fechas relativas al reloj real: la Query lee con TimeProvider.System y exige que el inicio ya haya llegado.
    private static readonly DateTime Desde = DateTime.UtcNow.AddDays(-10);

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tenantC = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await using (var contexto = CrearContexto(_tenantA, _cadenaConexion))
        {
            contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
            contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));
            contexto.Tenants.Add(CrearTenant(_tenantC, "Tenant C S.L."));
            await contexto.SaveChangesAsync();
        }

        // La siembra va con la conexión de pruebas: la única vía de escritura de la aplicación es la de
        // cae_app_aprovisionamiento, que prueba ContratarLineaInboundPorAprovisionamientoTests.
        await using (var siembraA = CrearContexto(_tenantA, _cadenaConexion))
        {
            siembraA.ProductosContratados.Add(LineaInbound());
            await siembraA.SaveChangesAsync();
        }

        await using (var siembraC = CrearContexto(_tenantC, _cadenaConexion))
        {
            var dadaDeBaja = LineaInbound();
            dadaDeBaja.Finalizar(Desde.AddDays(1), ahoraUtc: Desde.AddDays(1));
            siembraC.ProductosContratados.Add(dadaDeBaja);
            await siembraC.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_Tenant_beneficiario_con_la_linea_Inbound_vigente_la_tiene()
    {
        (await LeerComoRuntimeAsync(_tenantA)).Should().BeTrue();
    }

    [Fact]
    public async Task Un_Tenant_sin_Producto_Contratado_no_tiene_la_linea_aunque_otro_Tenant_si()
    {
        (await LeerComoRuntimeAsync(_tenantB)).Should().BeFalse("la línea de A no cuenta para B");
    }

    [Fact]
    public async Task Un_Tenant_con_la_linea_Inbound_dada_de_baja_no_la_tiene()
    {
        (await LeerComoRuntimeAsync(_tenantC)).Should().BeFalse();
    }

    private async Task<bool> LeerComoRuntimeAsync(Guid tenant)
    {
        await using var contexto = CrearContexto(tenant);
        return await new LineaInboundVigenteQueryHandler(contexto, new TenantActualAmbiental { TenantId = tenant }, TimeProvider.System)
            .Handle(new LineaInboundVigenteQuery(), CancellationToken.None);
    }

    private static ProductoContratado LineaInbound() => ProductoContratado.Contratar(LineaProductoContratado.Inbound,
        "cus_PagadorTalveg", TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Anual, Desde, ahoraUtc: Desde);

    private CaeManagerDbContext CrearContexto(Guid tenant, string? cadena = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var builder = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));

        var usuario = new CurrentUserServiceFalso(_usuario, tenantOrigenId: tenant);
        builder.AddInterceptors(
            new TenantSelladoInterceptor(tenantActual),
            new TenantRlsConnectionInterceptor(tenantActual, new SinClienteSeleccionado(tenant), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls));

        return new CaeManagerDbContext(builder.Options, _dataProtection, tenantActual);
    }

    private static Tenant CrearTenant(Guid id, string nombre)
    {
        var tenant = new Tenant(nombre);
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(tenant, id);
        return tenant;
    }

    private sealed class SinClienteSeleccionado(Guid tenant) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenant;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
