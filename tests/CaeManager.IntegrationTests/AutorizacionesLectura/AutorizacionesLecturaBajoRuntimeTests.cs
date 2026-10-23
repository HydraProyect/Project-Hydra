using CaeManager.Application.Common;
using CaeManager.Domain.AutorizacionesLectura;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.ContratosAcceso;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
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

namespace CaeManager.IntegrationTests.AutorizacionesLectura;

/// <summary>
/// Autorización de lectura del Tenant de la Empresa contratista (Inbound M1, PR 2) bajo <c>cae_app_runtime</c> con
/// contexto firmado. Lo que prueba aquí la base y no Application: RLS aísla las dos tablas por Tenant; el runtime solo
/// puede escribir las columnas de la revocación (GRANT por columna) y nunca borra la autorización; el trigger hace
/// definitiva la revocación y congela sus Trabajadores; el índice único parcial deja una sola autorización sin revocar
/// por destinatario y contrato; la FK compuesta no admite un Trabajador de otro Tenant; los CHECK impiden autorizarse a
/// uno mismo y vincular un contrato con su propio Tenant. Ninguna lectura cruza Tenants en este PR.
/// </summary>
public class AutorizacionesLecturaBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    // A es el Tenant de la Empresa contratista (concede); B, el del Cliente empresarial (destinatario).
    private readonly Tenant _tenantContratista = new("Tenant de la Empresa contratista");
    private readonly Tenant _tenantCliente = new("Tenant del Cliente empresarial");
    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _administradorCliente = Guid.NewGuid();
    private readonly Guid _contratoDelCliente = Guid.NewGuid();
    private Guid _trabajador;
    private Guid _otroTrabajador;
    private Guid _trabajadorDelCliente;
    private Guid _autorizacion;
    private Guid _revocada;
    private Guid _autorizacionDelCliente;
    private Guid _contratoAcceso;

    public async Task InitializeAsync()
    {
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = Contexto(_tenantContratista.Id))
        {
            await contexto.Database.MigrateAsync();
            contexto.Tenants.AddRange(_tenantContratista, _tenantCliente);
            var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
            Cuenta(contexto, _administrador, _tenantContratista.Id, roles[Roles.Administrador]);
            Cuenta(contexto, _administradorCliente, _tenantCliente.Id, roles[Roles.Administrador]);
            var empresa = new Empresa("Empresa contratista", "B10380194");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();
            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "Pérez", "12345678Z");
            var otroTrabajador = Trabajador.DeEmpresa(empresa.Id, "Luis", "García", "00000000T");
            contexto.Trabajadores.AddRange(trabajador, otroTrabajador);
            var autorizacion = Conceder(_tenantCliente.Id, _contratoDelCliente);
            var revocada = Conceder(_tenantCliente.Id, _contratoDelCliente);
            contexto.AutorizacionesLecturaDocumental.AddRange(autorizacion, revocada);
            await contexto.SaveChangesAsync();
            contexto.AutorizacionesLecturaTrabajadores.AddRange(
                autorizacion.AsignarTrabajador(trabajador.Id), revocada.AsignarTrabajador(trabajador.Id));
            await contexto.SaveChangesAsync();
            revocada.Revocar(_administrador, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
            _trabajador = trabajador.Id; _otroTrabajador = otroTrabajador.Id;
            _autorizacion = autorizacion.Id; _revocada = revocada.Id;
        }
        await using (var contexto = Contexto(_tenantCliente.Id))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial titular", "B10380186", false, null, null);
            var contratista = new Empresa("Empresa contratista", "B10380194");
            contexto.Empresas.AddRange(cliente, contratista);
            var centro = new Centro(cliente.Id, cliente.Id, "Centro de trabajo del titular");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            var trabajador = Trabajador.DeEmpresa(contratista.Id, "Eva", "Ruiz", "00000001R");
            var contrato = ContratoAcceso.Crear(cliente.Id, centro.Id, contratista.Id, hoy.AddDays(-10), null, null);
            var autorizacion = Conceder(_tenantContratista.Id, Guid.NewGuid());
            contexto.Trabajadores.Add(trabajador);
            contexto.ContratosAcceso.Add(contrato);
            contexto.AutorizacionesLecturaDocumental.Add(autorizacion);
            await contexto.SaveChangesAsync();
            contexto.AutorizacionesLecturaTrabajadores.Add(autorizacion.AsignarTrabajador(trabajador.Id));
            await contexto.SaveChangesAsync();
            _trabajadorDelCliente = trabajador.Id;
            _autorizacionDelCliente = autorizacion.Id;
            _contratoAcceso = contrato.Id;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Las_autorizaciones_y_sus_Trabajadores_de_otro_Tenant_son_invisibles_bajo_runtime()
    {
        await using var contexto = Contexto(_tenantContratista.Id, _administrador);
        (await contexto.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync()).Should().Be("cae_app_runtime");

        (await contexto.AutorizacionesLecturaDocumental.IgnoreQueryFilters().Select(a => a.Id).ToListAsync())
            .Should().BeEquivalentTo([_autorizacion, _revocada], "control positivo: las del propio Tenant se ven, y solo esas");
        (await contexto.AutorizacionesLecturaTrabajadores.IgnoreQueryFilters().Select(t => t.AutorizacionId).ToListAsync())
            .Should().BeEquivalentTo([_autorizacion, _revocada]);

        await using var contextoCliente = Contexto(_tenantCliente.Id, _administradorCliente);
        (await contextoCliente.AutorizacionesLecturaDocumental.IgnoreQueryFilters().Select(a => a.Id).ToListAsync())
            .Should().Equal([_autorizacionDelCliente], "RLS oculta las del Tenant de la Empresa contratista sin ayuda del filtro de EF");
        (await contextoCliente.AutorizacionesLecturaTrabajadores.IgnoreQueryFilters().Select(t => t.TrabajadorId).ToListAsync())
            .Should().Equal([_trabajadorDelCliente], "y los Trabajadores de sus autorizaciones tampoco");
    }

    [Fact]
    public async Task El_runtime_no_puede_cambiar_una_columna_de_nacimiento_y_el_trigger_lo_impide_tambien_al_propietario()
    {
        await using (var contexto = Contexto(_tenantContratista.Id, _administrador))
        {
            // Control positivo: el runtime sí escribe la columna Version de una autorización sin revocar.
            (await contexto.Database.ExecuteSqlAsync(
                $"UPDATE \"AutorizacionesLecturaDocumental\" SET \"Version\" = gen_random_uuid() WHERE \"Id\" = {_autorizacion};"))
                .Should().Be(1);

            var ampliar = () => contexto.Database.ExecuteSqlAsync(
                $"UPDATE \"AutorizacionesLecturaDocumental\" SET \"IncluyeDocumentosEmpresa\" = NOT \"IncluyeDocumentosEmpresa\" WHERE \"Id\" = {_autorizacion};");
            (await ampliar.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                    "la migración solo concede UPDATE de las columnas de la revocación a cae_app_runtime");
        }

        // Sin la barrera del GRANT (conexión propietaria), el trigger sigue rechazando el cambio.
        await using var propietario = Contexto(_tenantContratista.Id);
        var redirigir = () => propietario.Database.ExecuteSqlAsync(
            $"UPDATE \"AutorizacionesLecturaDocumental\" SET \"TenantDestinatarioId\" = gen_random_uuid() WHERE \"Id\" = {_autorizacion};");
        (await redirigir.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation, "TR_AutorizacionesLectura_Inmutable congela lo concedido");
    }

    [Fact]
    public async Task Una_autorizacion_revocada_no_cambia_ni_siquiera_en_las_columnas_de_la_revocacion()
    {
        await using var contexto = Contexto(_tenantContratista.Id, _administrador);

        var reabrir = () => contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"AutorizacionesLecturaDocumental\" SET \"RevocadaEnUtc\" = NULL, \"RevocadaPorUsuarioId\" = NULL WHERE \"Id\" = {_revocada};");
        (await reabrir.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation, "la revocación es definitiva");

        var tocar = () => contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"AutorizacionesLecturaDocumental\" SET \"Version\" = gen_random_uuid() WHERE \"Id\" = {_revocada};");
        (await tocar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Una_autorizacion_revocada_no_admite_anadir_ni_retirar_Trabajadores()
    {
        await using var contexto = Contexto(_tenantContratista.Id, _administrador);
        Task<int> Insertar(Guid autorizacionId) => contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AutorizacionesLecturaTrabajadores" ("Id", "TenantId", "AutorizacionId", "TrabajadorId")
            VALUES ({Guid.NewGuid()}, {_tenantContratista.Id}, {autorizacionId}, {_otroTrabajador});
            """);

        // Control positivo: a la autorización sin revocar sí se le añade.
        (await Insertar(_autorizacion)).Should().Be(1);

        var anadir = () => Insertar(_revocada);
        (await anadir.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation, "TR_AutorizacionesLecturaTrabajadores_SoloSinRevocar");

        var retirar = () => contexto.Database.ExecuteSqlAsync(
            $"DELETE FROM \"AutorizacionesLecturaTrabajadores\" WHERE \"AutorizacionId\" = {_revocada};");
        (await retirar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation, "tampoco se retira el Trabajador de una revocada");
    }

    [Fact]
    public async Task El_runtime_no_puede_borrar_una_autorizacion_y_si_retirar_un_Trabajador()
    {
        await using var contexto = Contexto(_tenantContratista.Id, _administrador);

        var borrar = () => contexto.Database.ExecuteSqlAsync(
            $"DELETE FROM \"AutorizacionesLecturaDocumental\" WHERE \"Id\" = {_autorizacion};");
        (await borrar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la autorización se revoca, nunca se borra");

        // Control positivo: retirar el Trabajador de una autorización sin revocar sí pasa (DELETE concedido en la tabla hija).
        (await contexto.Database.ExecuteSqlAsync(
            $"DELETE FROM \"AutorizacionesLecturaTrabajadores\" WHERE \"AutorizacionId\" = {_autorizacion};"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Solo_una_autorizacion_sin_revocar_por_destinatario_y_contrato_y_tras_revocar_se_concede_otra()
    {
        await using (var contexto = Contexto(_tenantContratista.Id, _administrador))
        {
            contexto.AutorizacionesLecturaDocumental.Add(Conceder(_tenantCliente.Id, _contratoDelCliente));
            var guardar = () => contexto.SaveChangesAsync();
            var error = (await guardar.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>().Which;
            error.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
            error.ConstraintName.Should().Be("IX_AutorizacionesLectura_UnaSinRevocarPorContrato");
        }

        await using (var contexto = Contexto(_tenantContratista.Id, _administrador))
        {
            var vigente = await contexto.AutorizacionesLecturaDocumental.SingleAsync(a => a.Id == _autorizacion);
            vigente.Revocar(_administrador, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = Contexto(_tenantContratista.Id, _administrador))
        {
            contexto.AutorizacionesLecturaDocumental.Add(Conceder(_tenantCliente.Id, _contratoDelCliente));
            await contexto.SaveChangesAsync();
            (await contexto.AutorizacionesLecturaDocumental.CountAsync(a => a.RevocadaEnUtc == null)).Should().Be(1);
        }
    }

    [Fact]
    public async Task La_base_rechaza_un_Trabajador_de_otro_Tenant_en_la_autorizacion()
    {
        await using var contexto = Contexto(_tenantContratista.Id, _administrador);
        var autorizacion = await contexto.AutorizacionesLecturaDocumental.SingleAsync(a => a.Id == _autorizacion);

        // Control positivo: un Trabajador del propio Tenant entra.
        contexto.AutorizacionesLecturaTrabajadores.Add(autorizacion.AsignarTrabajador(_otroTrabajador));
        await contexto.SaveChangesAsync();

        contexto.AutorizacionesLecturaTrabajadores.Add(autorizacion.AsignarTrabajador(_trabajadorDelCliente));
        var guardar = () => contexto.SaveChangesAsync();
        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation,
                "la FK (TenantId, TrabajadorId) contra Trabajadores no casa con un Trabajador de otro Tenant");
    }

    [Fact]
    public async Task La_base_rechaza_una_autorizacion_cuyo_destinatario_es_el_propio_Tenant()
    {
        await using var contexto = Contexto(_tenantContratista.Id, _administrador);

        contexto.AutorizacionesLecturaDocumental.Add(Conceder(_tenantContratista.Id, Guid.NewGuid()));
        var guardar = () => contexto.SaveChangesAsync();
        var error = (await guardar.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>().Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be("CK_AutorizacionesLectura_DestinatarioAjeno");
    }

    [Fact]
    public async Task La_base_rechaza_vincular_un_contrato_con_su_propio_Tenant()
    {
        await using var contexto = Contexto(_tenantCliente.Id, _administradorCliente);

        var propio = () => contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"TenantContratistaId\" = \"TenantId\" WHERE \"Id\" = {_contratoAcceso};");
        var error = (await propio.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be("CK_ContratosAcceso_TenantContratistaAjeno");

        // Control positivo: con el Tenant de la Empresa contratista, el mismo UPDATE pasa.
        (await contexto.Database.ExecuteSqlAsync(
            $"UPDATE \"ContratosAcceso\" SET \"TenantContratistaId\" = {_tenantContratista.Id} WHERE \"Id\" = {_contratoAcceso};"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Un_INSERT_por_SQL_con_el_TenantId_de_otro_Tenant_lo_rechaza_el_WITH_CHECK()
    {
        await using var contexto = Contexto(_tenantContratista.Id, _administrador);
        Task<int> Insertar(Guid tenantId, Guid destinatario) => contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AutorizacionesLecturaDocumental" ("Id", "TenantId", "TenantDestinatarioId", "ContratoAccesoId",
                "IncluyeDocumentosEmpresa", "VigenciaDesde", "ConcedidaPorUsuarioId", "ConcedidaEnUtc", "Version")
            VALUES ({Guid.NewGuid()}, {tenantId}, {destinatario}, {Guid.NewGuid()}, false, {DiaDeNegocio.Hoy()},
                {_administrador}, now(), {Guid.NewGuid()});
            """);

        var ajeno = () => Insertar(_tenantCliente.Id, _tenantContratista.Id);
        (await ajeno.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la política aislamiento_tenant no deja escribir una fila con otro TenantId (new row violates row-level security)");

        // Control positivo: la misma fila con el TenantId del contexto entra.
        (await Insertar(_tenantContratista.Id, _tenantCliente.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Soporte_lee_las_autorizaciones_de_su_Tenant_y_no_puede_insertar()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var preparar = conexion.CreateCommand())
        {
            preparar.CommandText = "SELECT set_config('app.tenant_id', @tenant, true); SET LOCAL ROLE cae_app_soporte;";
            preparar.Parameters.AddWithValue("tenant", _tenantContratista.Id.ToString());
            await preparar.ExecuteNonQueryAsync();
        }

        await using (var leer = conexion.CreateCommand())
        {
            leer.CommandText = "SELECT count(*) FROM \"AutorizacionesLecturaDocumental\";";
            ((long)(await leer.ExecuteScalarAsync())!).Should().Be(2, "Soporte lee las dos del Tenant y no la del Tenant ajeno");
        }

        await using var escribir = conexion.CreateCommand();
        escribir.CommandText = """
            INSERT INTO "AutorizacionesLecturaDocumental" ("Id", "TenantId", "TenantDestinatarioId", "ContratoAccesoId",
                "IncluyeDocumentosEmpresa", "VigenciaDesde", "ConcedidaPorUsuarioId", "ConcedidaEnUtc", "Version")
            VALUES (@id, @tenant, @destinatario, @contrato, false, @desde, @usuario, now(), @version);
            """;
        escribir.Parameters.AddWithValue("id", Guid.NewGuid());
        escribir.Parameters.AddWithValue("tenant", _tenantContratista.Id);
        escribir.Parameters.AddWithValue("destinatario", _tenantCliente.Id);
        escribir.Parameters.AddWithValue("contrato", Guid.NewGuid());
        escribir.Parameters.AddWithValue("desde", DiaDeNegocio.Hoy());
        escribir.Parameters.AddWithValue("usuario", _administrador);
        escribir.Parameters.AddWithValue("version", Guid.NewGuid());
        (await FluentActions.Awaiting(() => escribir.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la migración solo concede SELECT a cae_app_soporte");
    }

    private AutorizacionLecturaDocumental Conceder(Guid destinatario, Guid contrato) =>
        AutorizacionLecturaDocumental.Conceder(destinatario, contrato, true, DiaDeNegocio.Hoy().AddDays(-1), null, _administrador, DateTime.UtcNow);

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
