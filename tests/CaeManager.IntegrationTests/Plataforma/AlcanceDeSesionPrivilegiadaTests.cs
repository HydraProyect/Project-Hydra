using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Qué ve una sesión privilegiada dentro del tenant que abre.
///
/// La pregunta no es retórica: una sesión de plano 3 no tiene rol de negocio
/// —<c>ObtenerRolActualAsync</c> devuelve null a propósito— y el resolutor de
/// alcance falla cerrado ante un rol desconocido. Sin una rama explícita, abrir
/// el tenant de un cliente para dar soporte habría dado una pantalla vacía: el
/// contexto correcto y cero filas.
///
/// La matriz que se prueba aquí es la del ADR-011 § 4bis.2, y su reparto no es
/// gradual sino por capacidad:
/// <list type="bullet">
/// <item><c>SoporteLectura</c> y <c>BreakGlass</c> ven el tenant entero — es
/// para lo que existen.</item>
/// <item><c>AdminPlataforma</c> no ve <b>nada</b> del contenido: administrar
/// tenants, facturación y configuración global es otra capacidad. Meter la
/// lectura de documentos dentro de ella reintroduciría el rol monolítico de
/// administrador de plataforma que la matriz por capacidades elimina.</item>
/// <item><c>Impersonacion</c> tampoco, todavía: su alcance es el del usuario
/// simulado, y resolverlo es trabajo de su propia fase. Hasta entonces cae a la
/// rama de rol, que sin rol no devuelve nada.</item>
/// </list>
///
/// Lo que en ningún caso cambia es el aislamiento: "acceso total" es total
/// dentro del tenant objetivo, porque el filtro global sigue puesto. Eso lo
/// prueban los tests de aislamiento de siempre, no estos.
/// </summary>
public class AlcanceDeSesionPrivilegiadaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantVisitado = Guid.NewGuid();
    private readonly Guid _usuarioPlataforma = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    public async Task Las_capacidades_de_inspeccion_ven_el_tenant_entero(CapacidadPrivilegio capacidad)
    {
        var alcance = CrearAlcanceConSesion(capacidad);

        (await alcance.TieneAccesoTotalAsync()).Should().BeTrue();

        // null = sin restricción por cliente, que es lo que significa acceso
        // total en este servicio (no confundir con lista vacía).
        (await alcance.ObtenerClienteIdsVisiblesAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    public async Task Las_capacidades_que_no_son_de_inspeccion_no_ven_contenido(CapacidadPrivilegio capacidad)
    {
        var alcance = CrearAlcanceConSesion(capacidad);

        (await alcance.TieneAccesoTotalAsync()).Should().BeFalse();

        // Lista vacía, no null: hay restricción y no alcanza a ningún cliente.
        (await alcance.ObtenerClienteIdsVisiblesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_sesion_privilegiada_manda_el_rol_de_siempre()
    {
        // Guarda de no regresión: la rama nueva no puede cambiar el alcance de
        // los usuarios normales, que son todos los de hoy.
        var alcance = new AlcanceDatosService(
            CrearContexto(), new CurrentUserServiceFalso(_usuarioPlataforma, "Administrador"),
            new TenantActualAmbiental { TenantId = _tenantVisitado }, new SesionPrivilegiadaAusente());

        (await alcance.TieneAccesoTotalAsync()).Should().BeTrue();
    }

    private AlcanceDatosService CrearAlcanceConSesion(CapacidadPrivilegio capacidad) =>
        new(CrearContexto(),
            // El rol del claim es "Administrador" —el que el técnico tiene en SU
            // tenant— pero CurrentUserService devuelve null bajo sesión
            // privilegiada. Se reproduce aquí ese null a propósito: si el
            // alcance saliera del rol, este test no probaría nada.
            new CurrentUserServiceFalso(_usuarioPlataforma, rol: null),
            new TenantActualAmbiental { TenantId = _tenantVisitado },
            new SesionPrivilegiadaFalsa(new SesionPrivilegiadaActiva(
                Guid.NewGuid(), Guid.NewGuid(), _tenantVisitado, capacidad, null)));

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantVisitado };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// Doble del resolutor, no de la revalidación: que la sesión sea legítima
    /// lo prueba <see cref="SesionPrivilegiadaActualTests"/> contra la base. Lo
    /// que se aísla aquí es la decisión de alcance dada una sesión ya válida.
    /// </summary>
    private sealed class SesionPrivilegiadaFalsa(SesionPrivilegiadaActiva sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(sesion);
    }
}
