using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.ContratosAcceso;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.ContratosAcceso;

/// <summary>
/// Contrato de acceso (Inbound M1) bajo <c>cae_app_runtime</c> con contexto firmado: el aislamiento por Tenant lo da RLS
/// y no solo el filtro de EF, el runtime no puede borrar filas (solo soft delete), la FK compuesta contra el Centro
/// rechaza un ClienteEmpresarialId que no sea el del Centro, y el listado traduce a SQL el criterio de vigencia.
/// </summary>
public class ContratosAccesoBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _propietario = new("Tenant propietario del Cliente empresarial");
    private readonly Tenant _ajeno = new("Tenant propietario ajeno");
    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _administradorAjeno = Guid.NewGuid();
    private Guid _cliente;
    private Guid _otroCliente;
    private Guid _centro;
    private Guid _contratista;
    private Guid _contratoVigente;
    private Guid _contratoCaducado;
    private Guid _contratoAjeno;
    private Guid _contratistaAjena;

    public async Task InitializeAsync()
    {
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = Contexto(_propietario.Id))
        {
            await contexto.Database.MigrateAsync();
            contexto.Tenants.AddRange(_propietario, _ajeno);
            var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
            Cuenta(contexto, _administrador, _propietario.Id, roles[Roles.Administrador]);
            Cuenta(contexto, _administradorAjeno, _ajeno.Id, roles[Roles.Administrador]);
            var cliente = Empresa.CrearComoCliente("Cliente empresarial titular", "B10380186", false, null, null);
            var otroCliente = Empresa.CrearComoCliente("Otro Cliente empresarial", "B10380202", false, null, null);
            var contratista = new Empresa("Empresa contratista", "B10380194");
            contexto.Empresas.AddRange(cliente, otroCliente, contratista);
            var centro = new Centro(cliente.Id, cliente.Id, "Centro de trabajo del titular");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            var vigente = ContratoAcceso.Crear(cliente.Id, centro.Id, contratista.Id, hoy.AddDays(-10), hoy.AddDays(10), null);
            var caducado = ContratoAcceso.Crear(cliente.Id, centro.Id, contratista.Id, hoy.AddDays(-60), hoy.AddDays(-1), null);
            contexto.ContratosAcceso.AddRange(vigente, caducado);
            await contexto.SaveChangesAsync();
            _cliente = cliente.Id; _otroCliente = otroCliente.Id; _centro = centro.Id; _contratista = contratista.Id;
            _contratoVigente = vigente.Id; _contratoCaducado = caducado.Id;
        }
        await using (var contexto = Contexto(_ajeno.Id))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial ajeno", "B10380186", false, null, null);
            var contratista = new Empresa("Empresa contratista ajena", "B10380194");
            contexto.Empresas.AddRange(cliente, contratista);
            var centro = new Centro(cliente.Id, cliente.Id, "Centro de trabajo ajeno");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            var ajeno = ContratoAcceso.Crear(cliente.Id, centro.Id, contratista.Id, hoy.AddDays(-10), null, null);
            contexto.ContratosAcceso.Add(ajeno);
            await contexto.SaveChangesAsync();
            _contratoAjeno = ajeno.Id;
            _contratistaAjena = contratista.Id;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task El_contrato_de_otro_Tenant_es_invisible_bajo_runtime_aunque_se_quite_el_filtro_de_EF()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        (await contexto.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync()).Should().Be("cae_app_runtime");

        (await contexto.ContratosAcceso.IgnoreQueryFilters().AnyAsync(c => c.Id == _contratoVigente))
            .Should().BeTrue("control positivo: el contrato del propio Tenant se ve");
        (await contexto.ContratosAcceso.IgnoreQueryFilters().AnyAsync(c => c.Id == _contratoAjeno))
            .Should().BeFalse("RLS oculta la fila del Tenant ajeno sin ayuda del filtro de EF");

        await using var contextoAjeno = Contexto(_ajeno.Id, _administradorAjeno);
        (await contextoAjeno.ContratosAcceso.IgnoreQueryFilters().Select(c => c.Id).ToListAsync())
            .Should().Equal([_contratoAjeno], "y al revés: desde el Tenant ajeno solo se ve el suyo");
    }

    [Fact]
    public async Task El_runtime_no_puede_borrar_contratos_solo_darlos_de_baja()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);

        var borrado = () => contexto.Database.ExecuteSqlRawAsync("DELETE FROM \"ContratosAcceso\";");
        (await borrado.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la migración no concede DELETE a cae_app_runtime");

        // Control positivo: la baja lógica (UPDATE) sí pasa.
        var contrato = await contexto.ContratosAcceso.SingleAsync(c => c.Id == _contratoCaducado);
        contrato.MarcarComoEliminado(_administrador);
        await contexto.SaveChangesAsync();
        (await contexto.ContratosAcceso.IgnoreQueryFilters().SingleAsync(c => c.Id == _contratoCaducado)).EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task La_base_rechaza_un_contrato_cuyo_Cliente_empresarial_no_es_el_del_Centro()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var hoy = DiaDeNegocio.Hoy();

        // Control positivo: con el Cliente empresarial del Centro, el mismo alta pasa.
        contexto.ContratosAcceso.Add(ContratoAcceso.Crear(_cliente, _centro, _contratista, hoy, null, null));
        await contexto.SaveChangesAsync();

        contexto.ContratosAcceso.Add(ContratoAcceso.Crear(_otroCliente, _centro, _contratista, hoy, null, null));
        var guardar = () => contexto.SaveChangesAsync();
        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation,
                "la FK compuesta (TenantId, CentroId, ClienteEmpresarialId) contra Centros lo hace irrepresentable");
    }

    [Fact]
    public async Task Un_INSERT_por_SQL_con_el_TenantId_de_otro_Tenant_lo_rechaza_el_WITH_CHECK()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var hoy = DiaDeNegocio.Hoy();
        Task<int> Insertar(Guid tenantId) => contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ContratosAcceso" ("Id", "TenantId", "ClienteEmpresarialId", "CentroId", "EmpresaContratistaId",
                "FechaInicio", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES ({Guid.NewGuid()}, {tenantId}, {_cliente}, {_centro}, {_contratista}, {hoy}, {Guid.NewGuid()}, now(), false);
            """);

        var ajeno = () => Insertar(_ajeno.Id);
        (await ajeno.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la política aislamiento_tenant no deja escribir una fila con otro TenantId (new row violates row-level security)");

        // Control positivo: la misma fila con el TenantId del contexto entra.
        (await Insertar(_propietario.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Soporte_lee_los_contratos_de_su_Tenant_y_no_puede_insertar()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var preparar = conexion.CreateCommand())
        {
            preparar.CommandText = "SELECT set_config('app.tenant_id', @tenant, true); SET LOCAL ROLE cae_app_soporte;";
            preparar.Parameters.AddWithValue("tenant", _propietario.Id.ToString());
            await preparar.ExecuteNonQueryAsync();
        }

        await using (var leer = conexion.CreateCommand())
        {
            leer.CommandText = "SELECT count(*) FROM \"ContratosAcceso\";";
            ((long)(await leer.ExecuteScalarAsync())!).Should().Be(2, "Soporte lee los dos contratos del Tenant propietario y no el ajeno");
        }

        await using var escribir = conexion.CreateCommand();
        escribir.CommandText = """
            INSERT INTO "ContratosAcceso" ("Id", "TenantId", "ClienteEmpresarialId", "CentroId", "EmpresaContratistaId",
                "FechaInicio", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES (@id, @tenant, @cliente, @centro, @contratista, @inicio, @version, now(), false);
            """;
        escribir.Parameters.AddWithValue("id", Guid.NewGuid());
        escribir.Parameters.AddWithValue("tenant", _propietario.Id);
        escribir.Parameters.AddWithValue("cliente", _cliente);
        escribir.Parameters.AddWithValue("centro", _centro);
        escribir.Parameters.AddWithValue("contratista", _contratista);
        escribir.Parameters.AddWithValue("inicio", DiaDeNegocio.Hoy());
        escribir.Parameters.AddWithValue("version", Guid.NewGuid());
        (await FluentActions.Awaiting(() => escribir.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la migración solo concede SELECT a cae_app_soporte");
    }

    [Fact]
    public async Task La_base_rechaza_una_Empresa_contratista_de_otro_Tenant()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var hoy = DiaDeNegocio.Hoy();

        // Control positivo: con la Empresa contratista del propio Tenant, el mismo alta pasa.
        contexto.ContratosAcceso.Add(ContratoAcceso.Crear(_cliente, _centro, _contratista, hoy, null, null));
        await contexto.SaveChangesAsync();

        contexto.ContratosAcceso.Add(ContratoAcceso.Crear(_cliente, _centro, _contratistaAjena, hoy, null, null));
        var guardar = () => contexto.SaveChangesAsync();
        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation,
                "la FK (TenantId, EmpresaContratistaId) contra Empresas no casa con una Empresa de otro Tenant");
    }

    [Theory]
    [InlineData("CK_ContratosAcceso_FinNoAnteriorAlInicio")]
    [InlineData("CK_ContratosAcceso_CierreNoAnteriorAlInicio")]
    [InlineData("CK_ContratosAcceso_ContratistaDistintaDelCliente")]
    public async Task Un_UPDATE_por_SQL_que_rompe_una_invariante_lo_rechaza_su_CHECK(string restriccion)
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);

        // Control positivo: el borde permitido (fin y cierre el mismo día del inicio) pasa.
        (await contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"FechaFin\" = \"FechaInicio\", \"FechaCierreReal\" = \"FechaInicio\" WHERE \"Id\" = {_contratoVigente};"))
            .Should().Be(1);

        var sql = restriccion switch
        {
            "CK_ContratosAcceso_FinNoAnteriorAlInicio" =>
                "UPDATE \"ContratosAcceso\" SET \"FechaFin\" = \"FechaInicio\" - 1 WHERE \"Id\" = {0};",
            "CK_ContratosAcceso_CierreNoAnteriorAlInicio" =>
                "UPDATE \"ContratosAcceso\" SET \"FechaCierreReal\" = \"FechaInicio\" - 1 WHERE \"Id\" = {0};",
            _ => "UPDATE \"ContratosAcceso\" SET \"EmpresaContratistaId\" = \"ClienteEmpresarialId\" WHERE \"Id\" = {0};",
        };
        var romper = () => contexto.Database.ExecuteSqlRawAsync(sql, _contratoVigente);
        var error = (await romper.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be(restriccion);
    }

    [Fact]
    public async Task El_listado_traduce_a_SQL_el_filtro_de_vigencia_y_respeta_el_Tenant()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var usuario = new CurrentUserServiceFalso(_administrador, Roles.Administrador, tenantOrigenId: _propietario.Id);
        var tenant = new TenantActualAmbiental { TenantId = _propietario.Id };
        var alcance = new AlcanceDatosService(contexto, usuario, tenant, new SesionPrivilegiadaAusente());
        var handler = new ObtenerContratosAccesoQueryHandler(contexto, alcance, new PoliticaNotaInterna(usuario, new SesionPrivilegiadaAusente(), tenant));

        (await handler.Handle(new ObtenerContratosAccesoQuery(CentroId: _centro), CancellationToken.None))
            .Select(c => c.Id).Should().BeEquivalentTo([_contratoVigente, _contratoCaducado]);
        var vigentes = await handler.Handle(
            new ObtenerContratosAccesoQuery(_centro, _contratista, DiaDeNegocio.Hoy()), CancellationToken.None);
        vigentes.Select(c => c.Id).Should().Equal([_contratoVigente]);
        vigentes.Single().VigenteHoy.Should().BeTrue();
        (await handler.Handle(new ObtenerContratosAccesoQuery(), CancellationToken.None))
            .Select(c => c.Id).Should().NotContain(_contratoAjeno);
    }

    private static void Cuenta(CaeManagerDbContext contexto, Guid id, Guid tenantId, Guid rolId)
    {
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            TenantId = tenantId,
            UserName = $"{id:N}@caemanager.local",
            NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
            Email = $"{id:N}@caemanager.local",
            NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
            NombreCompleto = "Administrador",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString()
        });
        contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rolId });
    }

    private CaeManagerDbContext Contexto(Guid tenantId, Guid? usuario = null)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(
            usuario is null ? _cadena : BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena),
            n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        opciones.AddInterceptors(new TenantSelladoInterceptor(tenant));
        if (usuario is not null)
            opciones.AddInterceptors(new TenantRlsConnectionInterceptor(tenant, new SinTenantSeleccionado(),
                new CurrentUserServiceFalso(usuario.Value, Roles.Administrador, tenantOrigenId: tenantId), BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor());
        return new(opciones.Options, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
