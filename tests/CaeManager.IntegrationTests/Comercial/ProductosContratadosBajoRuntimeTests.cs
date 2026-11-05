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

    /// <summary>
    /// Los privilegios por rol medidos en la base, no por el error de un intento: sin contexto de Tenant, un
    /// INSERT concedido por error también fallaría con 42501, pero por el WITH CHECK de la política.
    /// </summary>
    [Theory]
    [InlineData("cae_app_runtime", "SELECT", true)]
    [InlineData("cae_app_runtime", "INSERT", false)]
    [InlineData("cae_app_runtime", "UPDATE", false)]
    [InlineData("cae_app_runtime", "DELETE", false)]
    [InlineData("cae_app_soporte", "SELECT", true)]
    [InlineData("cae_app_soporte", "INSERT", false)]
    [InlineData("cae_app_soporte", "UPDATE", false)]
    [InlineData("cae_app_soporte", "DELETE", false)]
    [InlineData("cae_app_aprovisionamiento", "SELECT", false)]
    [InlineData("cae_app_aprovisionamiento", "INSERT", false)]
    [InlineData("cae_app_aprovisionamiento", "UPDATE", false)]
    [InlineData("cae_app_aprovisionamiento", "DELETE", false)]
    [InlineData("public", "SELECT", false)]
    [InlineData("public", "INSERT", false)]
    public async Task Solo_runtime_y_soporte_leen_y_ningun_rol_de_la_aplicacion_escribe(string rol, string privilegio, bool esperado)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_table_privilege(@rol, 'public.\"ProductosContratados\"', @privilegio);";
        comando.Parameters.AddWithValue("rol", rol);
        comando.Parameters.AddWithValue("privilegio", privilegio);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().Be(esperado,
            "hasta que A6 abra la vía de escritura, ningún Tenant cambia lo contratado ni quién lo paga");
    }

    [Theory]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task El_Tenant_beneficiario_no_cambia_ni_borra_su_Producto_Contratado(string operacion)
    {
        await using var contexto = CrearContexto(_tenantA);
        var sql = operacion == "UPDATE"
            ? """UPDATE "ProductosContratados" SET "Tramo" = 'De1A10' WHERE "Id" = {0}"""
            : """DELETE FROM "ProductosContratados" WHERE "Id" = {0}""";
        (await contexto.ProductosContratados.CountAsync()).Should().Be(1, "control: con su contexto de Tenant ve la fila");

        var ejecutar = () => contexto.Database.ExecuteSqlRawAsync(sql, _productoA);

        (await ejecutar.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData("CK_ProductosContratados_EstadoYVigencia", "'Vigente'", "now()", "'cus_X'", "'OutboundGestionPropia'", "'De1A10'", "'Anual'")]
    [InlineData("CK_ProductosContratados_EstadoYVigencia", "'Finalizado'", "NULL", "'cus_X'", "'OutboundGestionPropia'", "'De1A10'", "'Anual'")]
    [InlineData("CK_ProductosContratados_EstadoYVigencia", "'Finalizado'", "now() - interval '1 day'", "'cus_X'", "'OutboundGestionPropia'", "'De1A10'", "'Anual'")]
    [InlineData("CK_ProductosContratados_PagadorFormato", "'Vigente'", "NULL", "''", "'OutboundGestionPropia'", "'De1A10'", "'Anual'")]
    [InlineData("CK_ProductosContratados_PagadorFormato", "'Vigente'", "NULL", "'   '", "'OutboundGestionPropia'", "'De1A10'", "'Anual'")]
    [InlineData("CK_ProductosContratados_PagadorFormato", "'Vigente'", "NULL", "' cus_X '", "'OutboundGestionPropia'", "'De1A10'", "'Anual'")]
    [InlineData("CK_ProductosContratados_PagadorFormato", "'Vigente'", "NULL", "E'\\t\\t'", "'OutboundGestionPropia'", "'De1A10'", "'Anual'")]
    [InlineData("CK_ProductosContratados_PagadorFormato", "'Vigente'", "NULL", "'cus-X'", "'OutboundGestionPropia'", "'De1A10'", "'Anual'")]
    [InlineData("CK_ProductosContratados_Linea", "'Vigente'", "NULL", "'cus_X'", "'Inbound'", "'De1A10'", "'Anual'")]
    [InlineData("CK_ProductosContratados_Tramo", "'Vigente'", "NULL", "'cus_X'", "'OutboundGestionPropia'", "'De301'", "'Anual'")]
    [InlineData("CK_ProductosContratados_Modalidad", "'Vigente'", "NULL", "'cus_X'", "'OutboundGestionPropia'", "'De1A10'", "'Trimestral'")]
    public async Task La_base_rechaza_filas_incoherentes_aunque_las_escriba_quien_evita_la_RLS(
        string restriccion, string estado, string hasta, string pagador, string linea, string tramo, string modalidad)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"""
            INSERT INTO "ProductosContratados" ("Id", "Linea", "PagadorStripeCustomerId", "Tramo", "Modalidad",
                "VigenteDesdeUtc", "VigenteHastaUtc", "Estado", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES (@id, {linea}, {pagador}, {tramo}, {modalidad}, now(), {hasta}, {estado}, @tenant, @id, now(), false);
            """;
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("tenant", _tenantA);

        var ejecutar = () => comando.ExecuteNonQueryAsync();

        var error = (await ejecutar.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be(restriccion);
    }

    [Fact]
    public async Task Un_estado_fuera_de_la_lista_lo_rechaza_su_propio_CHECK()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            ALTER TABLE "ProductosContratados" DROP CONSTRAINT "CK_ProductosContratados_EstadoYVigencia";
            INSERT INTO "ProductosContratados" ("Id", "Linea", "PagadorStripeCustomerId", "Tramo", "Modalidad",
                "VigenteDesdeUtc", "Estado", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES (@id, 'OutboundGestionPropia', 'cus_X', 'De1A10', 'Anual', now(), 'Suspendido', @tenant, @id, now(), false);
            """;
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("tenant", _tenantA);

        var ejecutar = () => comando.ExecuteNonQueryAsync();

        var error = (await ejecutar.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be("CK_ProductosContratados_Estado",
            "el estado de cobro no entra aquí: no hay «Suspendido», que es de Tenant.EstadoComercial");
    }

    [Fact]
    public async Task Un_Producto_Contratado_finalizado_se_guarda_y_se_lee_con_su_fin()
    {
        var hasta = Desde.AddMonths(1);
        await using (var contexto = CrearContexto(_tenantA, _cadenaConexion))
        {
            var producto = await contexto.ProductosContratados.SingleAsync(p => p.Id == _productoA);
            producto.Finalizar(hasta, ahoraUtc: hasta);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(_tenantA);
        var leido = await lectura.ProductosContratados.SingleAsync();
        leido.Estado.Should().Be(EstadoProductoContratado.Finalizado);
        leido.VigenteHastaUtc.Should().Be(hasta);
        leido.VigenteHastaUtc!.Value.Kind.Should().Be(DateTimeKind.Utc, "EstaVigenteEn exige UTC");
        leido.VigenteDesdeUtc.Kind.Should().Be(DateTimeKind.Utc);
        leido.EstaVigenteEn(hasta.AddTicks(-1)).Should().BeTrue();
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
