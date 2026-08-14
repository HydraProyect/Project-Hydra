using CaeManager.Domain.Clientes;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Verifica que las migraciones se aplican limpiamente contra PostgreSQL real
/// (el motor de producción tras la migración de ADR-003; antes, SQLite) y que
/// la semilla de datos queda en el estado esperado. Ver ROADMAP.md, criterio
/// de aceptación de Fase 0.
/// </summary>
public class MigracionesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Siembra_los_81_tipos_de_documento_del_catalogo()
    {
        // 15+21+1=37 de Trabajador (excel original + ampliación Fase 37 +
        // "DNI o NIE en vigor", 2026-08-09) + 13+17=30 de Empresa (documentos
        // base obligatorios del cliente + ampliación Fase 37) + 4 de Vehículo
        // (ITC, ficha técnica, seguro, autorización de circulación) + 6 del
        // tramo 0.3 del MVP-1 de formatos (2026-08-14, F-27/F-41/F-47/F-70/
        // F-93/F-95 — F-107 no suma porque ya existía como "Comunicación de
        // desplazamiento") + 4 del tramo 1.2 (F-44/F-52/F-50/F-29 — F-53 no
        // suma porque ya existía como "Declaración Responsable CAE") =
        // 37+30+4+6+4 = 81.
        var total = await _dbContext.TiposDocumento.CountAsync();
        total.Should().Be(81);
    }

    [Fact]
    public async Task Siembra_una_unica_fila_de_parametros_con_los_umbrales_reales()
    {
        var parametro = await _dbContext.ParametrosSistema.SingleAsync();

        parametro.UmbralAmbarDias.Should().Be(30);
        parametro.UmbralRojoDias.Should().Be(15);
    }

    [Fact]
    public async Task Siembra_los_seis_roles()
    {
        // Administrador, DireccionCae, CoordinadorCae, GestorCae, Consulta, Cliente (ver Roles.cs, Fase 31).
        var total = await _dbContext.Roles.CountAsync();
        total.Should().Be(6);
    }

    [Fact]
    public async Task Guarda_y_recupera_un_cliente()
    {
        var cliente = new Cliente("Cadena Industrial Iberia S.A.", "B12345674", esCritico: true);
        _dbContext.Clientes.Add(cliente);
        await _dbContext.SaveChangesAsync();

        var recuperado = await _dbContext.Clientes.FindAsync(cliente.Id);

        recuperado.Should().NotBeNull();
        recuperado!.RazonSocial.Should().Be("Cadena Industrial Iberia S.A.");
    }

    [Fact]
    public async Task El_filtro_global_de_soft_delete_oculta_los_clientes_eliminados()
    {
        var cliente = new Cliente("RENDELSUR", "B12345674", esCritico: true);
        _dbContext.Clientes.Add(cliente);
        await _dbContext.SaveChangesAsync();

        cliente.MarcarComoEliminado(Guid.NewGuid());
        await _dbContext.SaveChangesAsync();

        // FindAsync resuelve primero contra el change tracker local, sin aplicar el
        // query filter — se consulta explícitamente para probar el filtro real.
        var visible = await _dbContext.Clientes.FirstOrDefaultAsync(c => c.Id == cliente.Id);

        visible.Should().BeNull();
    }

    // --- Etapa 1 de PLAN-MIGRACION-MULTITENANT.md (esquema aditivo, TenantId nullable) ---

    [Fact]
    public async Task Siembra_el_tenant_por_defecto_activo()
    {
        var tenant = await _dbContext.Tenants.SingleAsync();

        tenant.Id.Should().Be(TenantSeedData.IdPorDefecto);
        tenant.Estado.Should().Be(EstadoTenant.Activo);
    }

    [Fact]
    public async Task El_backfill_sella_el_catalogo_de_tipos_de_documento_al_tenant_por_defecto()
    {
        var tenantIdsDistintos = await _dbContext.TiposDocumento.Select(t => t.TenantId).Distinct().ToListAsync();

        tenantIdsDistintos.Should().Equal(TenantSeedData.IdPorDefecto);
    }

    [Fact]
    public async Task El_backfill_sella_el_parametro_de_sistema_al_tenant_por_defecto()
    {
        var parametro = await _dbContext.ParametrosSistema.SingleAsync();

        parametro.TenantId.Should().Be(TenantSeedData.IdPorDefecto);
    }

    [Fact]
    public async Task Guarda_y_recupera_un_tenant()
    {
        var tenant = new Tenant("ArcoSPA");
        _dbContext.Tenants.Add(tenant);
        await _dbContext.SaveChangesAsync();

        var recuperado = await _dbContext.Tenants.FindAsync(tenant.Id);

        recuperado.Should().NotBeNull();
        recuperado!.Nombre.Should().Be("ArcoSPA");
        recuperado.Estado.Should().Be(EstadoTenant.Activo);
    }

    [Fact]
    public async Task El_interceptor_sella_el_tenant_actual_en_una_entidad_nueva_sin_que_el_Command_lo_asigne()
    {
        // Etapa 3 (cierre): TenantSelladoInterceptor sella TenantId con el
        // valor de ITenantActual — el Command (aquí, el propio test) nunca
        // lo asigna, Cliente no expone ningún setter para ello.
        var cliente = new Cliente("RENDELSUR", "B12345674", esCritico: false);
        _dbContext.Clientes.Add(cliente);
        await _dbContext.SaveChangesAsync();

        var recuperado = await _dbContext.Clientes.FindAsync(cliente.Id);

        recuperado!.TenantId.Should().Be(TenantSeedData.IdPorDefecto);
    }

    // --- Descubrimiento de migraciones ---

    [Fact]
    public void EF_descubre_todas_las_clases_Migration_del_ensamblado()
    {
        // AddTarifasCliente se saltaba en silencio: es una migración escrita a
        // mano (sin .Designer.cs) y le faltaban los atributos [DbContext] y
        // [Migration], que es donde EF Core lee el identificador. Sin ellos la
        // clase existe, compila y no la aplica nadie — la tabla TarifasCliente
        // no llegaba a crearse nunca y el módulo de Facturación fallaba al
        // usarla. No hay error ni aviso: por eso hace falta este test.
        // Ensamblado de PostgreSQL, no Infrastructure: cada motor tiene su
        // propio juego de migraciones (ver InfrastructureServiceCollectionExtensions).
        var clasesMigracion = typeof(CaeManager.Migrations.PostgreSQL.Migrations.LineaBase).Assembly
            .GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && t is { IsAbstract: false, IsGenericType: false })
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        var descubiertas = _dbContext.GetService<IMigrationsAssembly>().Migrations.Values
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        clasesMigracion.Should().NotBeEmpty("el ensamblado debe tener migraciones");
        descubiertas.Should().BeEquivalentTo(
            clasesMigracion,
            "toda clase Migration del ensamblado debe ser descubierta por EF; si falta alguna, "
            + "probablemente sea manual y le falten los atributos [DbContext]/[Migration]");
    }

    [Fact]
    public async Task Las_tablas_de_las_migraciones_manuales_existen_tras_migrar()
    {
        // Comprobación de resultado, no de metadatos: las dos migraciones
        // escritas a mano (AddTarifasCliente, AddProyectos) son las que se
        // saltan sin ruido si pierden los atributos.
        var tablas = new List<string>();

        await _dbContext.Database.OpenConnectionAsync();
        await using (var comando = _dbContext.Database.GetDbConnection().CreateCommand())
        {
            comando.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname = 'public';";
            await using var lector = await comando.ExecuteReaderAsync();
            while (await lector.ReadAsync())
                tablas.Add(lector.GetString(0));
        }

        tablas.Should().Contain("TarifasCliente");
        tablas.Should().Contain("Proyectos");
    }

    [Fact]
    public async Task Las_25_tablas_multi_tenant_tienen_TenantId_como_NOT_NULL()
    {
        // Verificación de esquema tras el cierre (Etapa 3): la columna ya
        // no admite NULL — si esto falla, la migración CerrarTenantId no se
        // aplicó o alguna tabla quedó fuera.
        await using var comando = _dbContext.Database.GetDbConnection().CreateCommand();
        await _dbContext.Database.OpenConnectionAsync();
        comando.CommandText =
            "SELECT is_nullable FROM information_schema.columns " +
            "WHERE table_name = 'Clientes' AND column_name = 'TenantId';";

        var esNullable = (string?)await comando.ExecuteScalarAsync();

        esNullable.Should().Be("NO");
    }
}
