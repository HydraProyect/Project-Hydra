using CaeManager.Application.Common;
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
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Comercial;

/// <summary>
/// El Producto Contratado mínimo (A5b; RQ-30, D-6) contra PostgreSQL real y como <c>cae_app_runtime</c>,
/// porque con la conexión de pruebas, que es superusuario, la política no se evalúa (OWASP ASVS 4.0.3
/// V4.1.3, V4.2.1). Las filas las siembra la conexión de pruebas: hasta A6 ningún rol de la aplicación las
/// escribe. La mitad positiva (el Tenant beneficiario ve su fila) evita que los rechazos pasen con una
/// política que lo prohibiera todo.
/// </summary>
public class ProductosContratadosBajoRuntimeTests : IAsyncLifetime
{
    private static readonly DateTime Desde = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();

    private Guid _productoA;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await using (var contexto = CrearContexto(_tenantA, _cadenaConexion))
        {
            contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
            contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));
            await contexto.SaveChangesAsync();
        }

        await using var siembra = CrearContexto(_tenantA, _cadenaConexion);
        var producto = ProductoContratado.Contratar(LineaProductoContratado.OutboundGestionPropia, "cus_PagadorA",
            TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Mensual, Desde);
        siembra.ProductosContratados.Add(producto);
        await siembra.SaveChangesAsync();
        _productoA = producto.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_Tenant_beneficiario_ve_su_Producto_Contratado_tal_como_se_contrato()
    {
        await using var contexto = CrearContexto(_tenantA);

        var producto = await contexto.ProductosContratados.SingleAsync();

        producto.Id.Should().Be(_productoA);
        producto.TenantId.Should().Be(_tenantA, "el interceptor sella el Tenant beneficiario al guardar");
        producto.PagadorStripeCustomerId.Should().Be("cus_PagadorA");
        producto.Tramo.Should().Be(TramoTrabajadoresActivos.De41A80);
        producto.Modalidad.Should().Be(ModalidadSuscripcionAlta.Mensual);
        producto.VigenteDesdeUtc.Should().Be(Desde);
        producto.Estado.Should().Be(EstadoProductoContratado.Vigente);
    }

    [Fact]
    public async Task Otro_Tenant_no_ve_el_Producto_Contratado_ni_con_el_filtro_apagado()
    {
        await using var contexto = CrearContexto(_tenantB);

        (await contexto.ProductosContratados.IgnoreQueryFilters().CountAsync()).Should().Be(0,
            "la política aislamiento_tenant oculta la fila del Tenant A aunque EF no filtre");

        await using var propio = CrearContexto(_tenantA);
        (await propio.ProductosContratados.IgnoreQueryFilters().CountAsync()).Should().Be(1, "control: el propio Tenant sí la ve");
    }

    [Theory]
    [InlineData("INSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task Ningun_Tenant_escribe_su_Producto_Contratado_desde_la_aplicacion(string operacion)
    {
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = operacion switch
        {
            "INSERT" => """
                INSERT INTO "ProductosContratados" ("Id", "Linea", "PagadorStripeCustomerId", "Tramo", "Modalidad",
                    "VigenteDesdeUtc", "Estado", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado")
                VALUES (@nuevo, 'OutboundGestionPropia', 'cus_Otro', 'De1A10', 'Anual', now(), 'Vigente', @tenant, @nuevo, now(), false);
                """,
            "UPDATE" => """UPDATE "ProductosContratados" SET "Tramo" = 'De1A10' WHERE "Id" = @id;""",
            _ => """DELETE FROM "ProductosContratados" WHERE "Id" = @id;""",
        };
        comando.Parameters.AddWithValue("nuevo", Guid.NewGuid());
        comando.Parameters.AddWithValue("tenant", _tenantA);
        comando.Parameters.AddWithValue("id", _productoA);

        var ejecutar = () => comando.ExecuteNonQueryAsync();

        (await ejecutar.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege, "solo SELECT hasta que A6 abra la vía de escritura");
    }

    [Theory]
    [InlineData("'Vigente'", "now()", "'cus_X'", "'OutboundGestionPropia'")]
    [InlineData("'Finalizado'", "NULL", "'cus_X'", "'OutboundGestionPropia'")]
    [InlineData("'Finalizado'", "now() - interval '1 day'", "'cus_X'", "'OutboundGestionPropia'")]
    [InlineData("'Vigente'", "NULL", "'   '", "'OutboundGestionPropia'")]
    [InlineData("'Vigente'", "NULL", "'cus_X'", "'Inbound'")]
    [InlineData("'Suspendido'", "NULL", "'cus_X'", "'OutboundGestionPropia'")]
    public async Task La_base_rechaza_filas_incoherentes_aunque_las_escriba_quien_evita_la_RLS(
        string estado, string hasta, string pagador, string linea)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"""
            INSERT INTO "ProductosContratados" ("Id", "Linea", "PagadorStripeCustomerId", "Tramo", "Modalidad",
                "VigenteDesdeUtc", "VigenteHastaUtc", "Estado", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES (@id, {linea}, {pagador}, 'De1A10', 'Anual', now(), {hasta}, {estado}, @tenant, @id, now(), false);
            """;
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("tenant", _tenantA);

        var ejecutar = () => comando.ExecuteNonQueryAsync();

        (await ejecutar.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Un_Producto_Contratado_finalizado_se_guarda_y_se_lee_con_su_fin()
    {
        var hasta = Desde.AddMonths(1);
        await using (var contexto = CrearContexto(_tenantA, _cadenaConexion))
        {
            var producto = await contexto.ProductosContratados.SingleAsync(p => p.Id == _productoA);
            producto.Finalizar(hasta);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(_tenantA);
        var leido = await lectura.ProductosContratados.SingleAsync();
        leido.Estado.Should().Be(EstadoProductoContratado.Finalizado);
        leido.VigenteHastaUtc.Should().Be(hasta);
    }

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
