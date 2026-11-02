using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// <see cref="RegistroSesionesUsuario.RevocarAsync"/>, <see cref="RegistroSesionesUsuario.CerrarTodasAsync"/>
/// y <see cref="RegistroSesionesUsuario.CerrarLasDemasAsync"/> con la implementación real, una cuenta
/// real de Identity y la identidad de la aplicación (<c>cae_app_runtime</c>), con la cuenta como quien
/// firma el contexto, como en la petición de cerrar sesión, o sin ella, como en el enlace de
/// restablecer la contraseña. <c>SesionesUsuarioBajoRuntimeTests</c> mide la tabla con SQL directo;
/// aquí se mide que el camino que usa la aplicación pasa la RLS (<c>solo_la_propia_cuenta_revoca</c>)
/// y el disparador <c>sesion_revocada_no_se_reabre</c>.
/// </summary>
public class RegistroSesionesUsuarioBajoRuntimeTests
{
    private static readonly Guid TenantA = TenantSeedData.IdPorDefecto;

    [Fact]
    public async Task Revocar_cierra_solo_esa_sesion_y_repetirlo_no_falla()
    {
        var cuenta = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(cuenta);
        await CrearCuentaAsync(arnes, cuenta);
        var esta = Guid.NewGuid();
        var otra = Guid.NewGuid();
        await RegistrarAsync(arnes, cuenta, esta, otra);

        (await RevocarAsync(arnes, cuenta, esta)).Should().BeTrue("control positivo: la sesión estaba viva");
        (await RevocarAsync(arnes, cuenta, esta)).Should().BeFalse("revocar otra vez no escribe ni choca con el disparador");

        (await VigenteAsync(arnes, cuenta, esta)).Should().BeFalse();
        (await VigenteAsync(arnes, cuenta, otra)).Should().BeTrue("la otra sesión de la misma cuenta sigue viva");
    }

    [Fact]
    public async Task Dos_cierres_simultaneos_de_la_misma_sesion_no_fallan_y_solo_uno_revoca()
    {
        var cuenta = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(cuenta);
        await CrearCuentaAsync(arnes, cuenta);
        var sid = Guid.NewGuid();
        await RegistrarAsync(arnes, cuenta, sid);

        var resultados = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() => RevocarAsync(arnes, cuenta, sid))));

        resultados.Count(r => r).Should().Be(1, "una sola sentencia encuentra la fila viva; las demás no escriben");
        (await VigenteAsync(arnes, cuenta, sid)).Should().BeFalse();
    }

    [Fact]
    public async Task Cerrar_todas_rota_el_sello_y_revoca_todas_las_sesiones_de_la_cuenta()
    {
        var cuenta = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(cuenta);
        await CrearCuentaAsync(arnes, cuenta);
        var primera = Guid.NewGuid();
        var segunda = Guid.NewGuid();
        var yaRevocada = Guid.NewGuid();
        await RegistrarAsync(arnes, cuenta, primera, segunda, yaRevocada);
        (await RevocarAsync(arnes, cuenta, yaRevocada)).Should().BeTrue();

        // Otra cuenta del mismo Tenant, sembrada como propietario: sus sesiones no se tocan.
        var ajena = Guid.NewGuid();
        var sidAjeno = Guid.NewGuid();
        await SembrarSesionComoPropietarioAsync(arnes, ajena, sidAjeno);

        var selloAntes = await LeerSelloAsync(arnes, cuenta);

        int revocadas;
        using (var ambito = arnes.Servicios.CreateScope())
            revocadas = await ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>().CerrarTodasAsync(cuenta);

        revocadas.Should().Be(2, "solo las dos vivas; la ya revocada conserva su revocación");
        (await LeerSelloAsync(arnes, cuenta)).Should().NotBe(selloAntes, "cerrar todas rota el sello de seguridad");
        (await VigenteAsync(arnes, cuenta, primera)).Should().BeFalse();
        (await VigenteAsync(arnes, cuenta, segunda)).Should().BeFalse();
        (await VigenteAsync(arnes, ajena, sidAjeno)).Should().BeTrue("las sesiones de otra cuenta del Tenant siguen vivas");
    }

    [Fact]
    public async Task Cerrar_las_demas_conserva_solo_esa_sesion_y_no_rota_el_sello()
    {
        var cuenta = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(cuenta);
        await CrearCuentaAsync(arnes, cuenta);
        var esta = Guid.NewGuid();
        var otra = Guid.NewGuid();
        await RegistrarAsync(arnes, cuenta, esta, otra);
        var selloAntes = await LeerSelloAsync(arnes, cuenta);

        int revocadas;
        using (var ambito = arnes.Servicios.CreateScope())
            revocadas = await ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>().CerrarLasDemasAsync(cuenta, esta);

        revocadas.Should().Be(1);
        (await VigenteAsync(arnes, cuenta, esta)).Should().BeTrue("quien cambia su contraseña conserva su sesión");
        (await VigenteAsync(arnes, cuenta, otra)).Should().BeFalse();
        (await LeerSelloAsync(arnes, cuenta)).Should().Be(selloAntes, "el sello lo ha rotado el cambio de credencial");
    }

    /// <summary>
    /// El caso que la RLS no deja pasar a un UPDATE: el enlace de restablecer la contraseña llega sin
    /// sesión, o con la de otra cuenta, así que nadie firma el contexto como esa cuenta. El control
    /// negativo mide que aquí <c>solo_la_propia_cuenta_revoca</c> oculta las filas: sin él, el test
    /// pasaría también con el UPDATE directo, que devuelve 0 sin error.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_firmar_como_la_cuenta_cerrar_todas_revoca_igual(bool conSesionDeOtraCuenta)
    {
        var cuenta = Guid.NewGuid();
        ICurrentUserService quienFirma = conSesionDeOtraCuenta ? new UsuarioFijo(Guid.NewGuid(), TenantA) : new SinUsuario();
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: new TenantFijo(TenantA),
            actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(cuenta)),
            currentUserServicePersonalizado: quienFirma,
            serviciosAdicionales: s => s.AddScoped<IRegistroSesionesUsuario, RegistroSesionesUsuario>());
        await CrearCuentaAsync(arnes, cuenta);
        var sid = Guid.NewGuid();
        await SembrarSesionComoPropietarioAsync(arnes, cuenta, sid);
        var selloAntes = await LeerSelloAsync(arnes, cuenta);

        using (var ambito = arnes.Servicios.CreateScope())
        using (AmbitoTenantExplicito.Establecer(TenantA))
        {
            var bd = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            (await bd.SesionesUsuario.Where(s => s.Sid == sid)
                    .ExecuteUpdateAsync(c => c.SetProperty(s => s.RevocadaEnUtc, DateTime.UtcNow)))
                .Should().Be(0, "control negativo: sin la firma de la cuenta, la RLS oculta la fila al UPDATE");
        }

        int revocadas;
        using (var ambito = arnes.Servicios.CreateScope())
            revocadas = await ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>().CerrarTodasAsync(cuenta);

        revocadas.Should().Be(1);
        (await VigenteAsync(arnes, cuenta, sid)).Should().BeFalse("una sesión viva se refrescaría con el sello nuevo");
        (await LeerSelloAsync(arnes, cuenta)).Should().NotBe(selloAntes);
    }

    [Fact]
    public async Task La_funcion_no_revoca_desde_el_contexto_de_otro_Tenant_y_soporte_no_la_ejecuta()
    {
        var cuenta = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(cuenta);
        await CrearCuentaAsync(arnes, cuenta);
        var sid = Guid.NewGuid();
        await SembrarSesionComoPropietarioAsync(arnes, cuenta, sid);

        using (var ambito = arnes.Servicios.CreateScope())
        using (AmbitoTenantExplicito.Establecer(Guid.NewGuid()))
        {
            var bd = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var revocar = () => bd.Database
                .SqlQuery<int>($"SELECT public.app_revocar_sesiones_de_cuenta({cuenta}, {Guid.Empty}) AS \"Value\"")
                .SingleAsync();
            (await revocar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        }

        (await VigenteAsync(arnes, cuenta, sid)).Should().BeTrue("el intento rechazado no revocó nada");

        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using var permisos = new NpgsqlCommand(
            """
            SELECT has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   p.prosecdef
              FROM pg_proc p
             WHERE p.oid = 'public.app_revocar_sesiones_de_cuenta(uuid, uuid)'::regprocedure;
            """, propietario);
        await using var fila = await permisos.ExecuteReaderAsync();
        (await fila.ReadAsync()).Should().BeTrue();
        fila.GetBoolean(0).Should().BeTrue();
        fila.GetBoolean(1).Should().BeFalse();
        fila.GetBoolean(2).Should().BeFalse();
        fila.GetBoolean(3).Should().BeTrue();
    }

    // ---------- Arnés ----------

    private static Task<ArnesDeArranqueRuntime> CrearArnesAsync(Guid cuenta) =>
        ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: new TenantFijo(TenantA),
            actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(cuenta)),
            currentUserServicePersonalizado: new UsuarioFijo(cuenta, TenantA),
            serviciosAdicionales: s => s.AddScoped<IRegistroSesionesUsuario, RegistroSesionesUsuario>());

    private static async Task CrearCuentaAsync(ArnesDeArranqueRuntime arnes, Guid id)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var um = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"sesiones-{id:N}@talveg.test";
        var usuario = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            NombreCompleto = email,
            EmailConfirmed = true,
            TenantId = TenantA,
        };

        // La preparación no es lo que se mide: su ámbito explícito, como en SegundoFactorBajoRuntimeTests.
        using (AmbitoTenantExplicito.Establecer(TenantA))
        {
            var resultado = await um.CreateAsync(usuario, "Arnes#2026Seguro");
            resultado.Succeeded.Should().BeTrue(string.Join(", ", resultado.Errors.Select(e => e.Code)));
        }
    }

    private static async Task RegistrarAsync(ArnesDeArranqueRuntime arnes, Guid cuenta, params Guid[] sids)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sesiones = ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>();
        foreach (var sid in sids)
            await sesiones.RegistrarAsync(cuenta, TenantA, sid);
    }

    private static async Task<bool> RevocarAsync(ArnesDeArranqueRuntime arnes, Guid cuenta, Guid sid)
    {
        using var ambito = arnes.Servicios.CreateScope();
        return await ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>().RevocarAsync(cuenta, sid);
    }

    private static async Task<bool> VigenteAsync(ArnesDeArranqueRuntime arnes, Guid cuenta, Guid sid)
    {
        using var ambito = arnes.Servicios.CreateScope();
        return await ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>().EstaVigenteAsync(cuenta, sid);
    }

    private static async Task SembrarSesionComoPropietarioAsync(ArnesDeArranqueRuntime arnes, Guid cuenta, Guid sid)
    {
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using var sembrar = new NpgsqlCommand(
            """
            INSERT INTO "SesionesUsuario" ("Id", "UsuarioId", "Sid", "EmitidaEnUtc", "TenantId")
            VALUES (@id, @usuario, @sid, now(), @tenant);
            """, propietario);
        sembrar.Parameters.AddWithValue("id", Guid.CreateVersion7());
        sembrar.Parameters.AddWithValue("usuario", cuenta);
        sembrar.Parameters.AddWithValue("sid", sid);
        sembrar.Parameters.AddWithValue("tenant", TenantA);
        await sembrar.ExecuteNonQueryAsync();
    }

    private static async Task<string?> LeerSelloAsync(ArnesDeArranqueRuntime arnes, Guid cuenta)
    {
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using var leer = new NpgsqlCommand("""SELECT "SecurityStamp" FROM "AspNetUsers" WHERE "Id" = @u;""", propietario);
        leer.Parameters.AddWithValue("u", cuenta);
        return (string?)await leer.ExecuteScalarAsync();
    }

    private sealed class TenantFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantId;
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class SinUsuario : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(null);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(null);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }

    private sealed class UsuarioFijo(Guid usuarioId, Guid tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Consulta");
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("Consulta");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }
}
