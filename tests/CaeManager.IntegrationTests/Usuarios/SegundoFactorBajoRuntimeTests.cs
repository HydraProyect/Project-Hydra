using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Usuarios.Commands.GenerarCodigosRecuperacion;
using CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Usuarios;

/// <summary>
/// P0-8 (hallazgo FS-01) contra PostgreSQL real con el rol <c>cae_app_runtime</c>
/// (<see cref="ArnesDeArranqueRuntime"/>): códigos de recuperación de 2FA con hash,
/// canje de un solo uso, regeneración, y restablecimiento de la 2FA de otra cuenta
/// por un Administrador de su mismo Tenant propietario — con los handlers reales y
/// <see cref="SegundoFactorDeCuentasIdentity"/>, no con dobles. Las reglas de
/// autorización caso a caso están en <c>SegundoFactorCommandsTests</c>
/// (Application); aquí se mide lo que solo la base puede decir: qué queda escrito,
/// que el rol de runtime puede escribirlo y que la auditoría separa Actor real de
/// Usuario afectado.
/// </summary>
public class SegundoFactorBajoRuntimeTests
{
    private static readonly Guid TenantA = TenantSeedData.IdPorDefecto;

    [Fact]
    public async Task Los_codigos_se_guardan_solo_con_hash_y_cada_uno_se_canjea_una_vez()
    {
        var usuarioId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(usuarioId, "Consulta");
        await CrearUsuarioCon2faAsync(arnes, usuarioId, "codigos@caemanager.local", TenantA);

        var codigos = await GenerarAsync(arnes);
        codigos.Should().HaveCount(CodigosRecuperacion.Cantidad).And.OnlyHaveUniqueItems();

        var fila = await LeerFilaCodigosAsync(arnes.CadenaPropietario, usuarioId);
        fila.Should().NotBeNull();
        var guardados = fila!.Split(';');
        guardados.Should().HaveCount(CodigosRecuperacion.Cantidad);
        guardados.Should().OnlyContain(g => g.StartsWith("pbkdf2-sha256$"));
        foreach (var codigo in codigos)
            fila.Should().NotContain(codigo, "en la base solo queda el hash, nunca el código en claro");
        await NingunaFilaDeAuditoriaContieneAsync(arnes.CadenaPropietario, codigos[0]);

        await EnAmbitoAsync(arnes, async um =>
        {
            var usuario = (await um.FindByIdAsync(usuarioId.ToString()))!;
            (await um.CountRecoveryCodesAsync(usuario)).Should().Be(CodigosRecuperacion.Cantidad);
            // Tolera el formato con que se copia a mano: minúsculas y sin guion.
            (await um.RedeemTwoFactorRecoveryCodeAsync(usuario, codigos[0].Replace("-", "").ToLowerInvariant()))
                .Succeeded.Should().BeTrue("el código recién generado es válido");
        });

        await EnAmbitoAsync(arnes, async um =>
        {
            var usuario = (await um.FindByIdAsync(usuarioId.ToString()))!;
            (await um.RedeemTwoFactorRecoveryCodeAsync(usuario, codigos[0]))
                .Succeeded.Should().BeFalse("un código de recuperación sirve una sola vez");
            (await um.CountRecoveryCodesAsync(usuario)).Should().Be(CodigosRecuperacion.Cantidad - 1);
            (await um.RedeemTwoFactorRecoveryCodeAsync(usuario, "ZZZZZ-ZZZZZ"))
                .Succeeded.Should().BeFalse("control negativo: un código inventado no vale");
        });
    }

    [Fact]
    public async Task Regenerar_invalida_los_codigos_anteriores()
    {
        var usuarioId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(usuarioId, "GestorCae");
        await CrearUsuarioCon2faAsync(arnes, usuarioId, "regenerar@caemanager.local", TenantA);

        var primeros = await GenerarAsync(arnes);
        var segundos = await GenerarAsync(arnes);

        segundos.Should().NotIntersectWith(primeros);
        await EnAmbitoAsync(arnes, async um =>
        {
            var usuario = (await um.FindByIdAsync(usuarioId.ToString()))!;
            (await um.RedeemTwoFactorRecoveryCodeAsync(usuario, primeros[0]))
                .Succeeded.Should().BeFalse("regenerar deja sin valor los códigos anteriores");
        });
        await EnAmbitoAsync(arnes, async um =>
        {
            var usuario = (await um.FindByIdAsync(usuarioId.ToString()))!;
            (await um.RedeemTwoFactorRecoveryCodeAsync(usuario, segundos[0]))
                .Succeeded.Should().BeTrue("control positivo: los nuevos sí valen");
        });
    }

    [Fact]
    public async Task Un_Administrador_restablece_la_2FA_de_otra_cuenta_de_su_Tenant_y_la_auditoria_separa_actor_de_afectado()
    {
        var adminId = Guid.NewGuid();
        var afectadoId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(adminId, "Administrador");
        await CrearUsuarioCon2faAsync(arnes, adminId, "admin@caemanager.local", TenantA);
        await CrearUsuarioCon2faAsync(arnes, afectadoId, "movil-perdido@caemanager.local", TenantA);
        await GenerarCodigosDeAsync(arnes, afectadoId);
        var selloAntes = await LeerSelloAsync(arnes.CadenaPropietario, afectadoId);

        var resultado = await RestablecerAsync(arnes, afectadoId);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        (await Leer2faActivaAsync(arnes.CadenaPropietario, afectadoId)).Should().BeFalse();
        (await ContarTokensAsync(arnes.CadenaPropietario, afectadoId)).Should().Be(0,
            "se borran la clave del autenticador y los códigos de recuperación");
        (await LeerSelloAsync(arnes.CadenaPropietario, afectadoId)).Should().NotBe(selloAntes,
            "el sello nuevo cierra las sesiones abiertas de la cuenta");

        var registro = await ObtenerRegistroAsync(arnes.CadenaPropietario, EntidadTipoAuditoria.Usuario, afectadoId);
        registro.TenantId.Should().Be(TenantA);
        registro.UsuarioId.Should().Be(adminId, "quien actuó es el Administrador, no la cuenta afectada");
        registro.ActorRealUsuarioId.Should().Be(adminId);
        registro.DatosDespues.Should().Contain("TwoFactorEnabled");
    }

    [Fact]
    public async Task Entre_Tenants_se_rechaza_y_la_cuenta_ajena_queda_intacta()
    {
        var adminId = Guid.NewGuid();
        var ajenoId = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(adminId, "Administrador");
        await CrearUsuarioCon2faAsync(arnes, adminId, "admin-a@caemanager.local", TenantA);
        await CrearUsuarioCon2faAsync(arnes, ajenoId, "usuario-b@caemanager.local", tenantB);
        var selloAntes = await LeerSelloAsync(arnes.CadenaPropietario, ajenoId);

        var resultado = await RestablecerAsync(arnes, ajenoId);

        resultado.Error.Codigo.Should().Be("SegundoFactor.CuentaNoEncontrada");
        (await Leer2faActivaAsync(arnes.CadenaPropietario, ajenoId)).Should().BeTrue();
        (await ContarTokensAsync(arnes.CadenaPropietario, ajenoId)).Should().Be(1, "su clave del autenticador sigue ahí");
        (await LeerSelloAsync(arnes.CadenaPropietario, ajenoId)).Should().Be(selloAntes);
    }

    [Fact]
    public async Task Un_2FA_restablecido_desde_otro_ambito_deja_de_contar_en_un_circuito_que_ya_rastrea_la_cuenta()
    {
        // P1-I1: TieneDobleFactorActivoAsync decide si se entregan datos de
        // credencial. En un circuito de Blazor el DbContext vive lo que el
        // circuito, y FindByIdAsync devolvería la cuenta ya rastreada con el
        // 2FA de cuando se cargó: un restablecimiento hecho desde otro ámbito
        // (RestablecerSegundoFactorCommand) no cerraría el acceso.
        var usuarioId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(usuarioId, "GestorCae");
        await CrearUsuarioCon2faAsync(arnes, usuarioId, "circuito@caemanager.local", TenantA);

        using var circuito = arnes.Servicios.CreateScope();
        var umCircuito = circuito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await umCircuito.FindByIdAsync(usuarioId.ToString())).Should().NotBeNull("el circuito rastrea la cuenta");
        var servicio = CrearCurrentUserService(circuito.ServiceProvider, usuarioId);

        (await servicio.TieneDobleFactorActivoAsync()).Should().BeTrue("control positivo: la cuenta tiene 2FA");

        await EnAmbitoAsync(arnes, async um =>
        {
            var usuario = await um.FindByIdAsync(usuarioId.ToString());
            Comprobar(await um.SetTwoFactorEnabledAsync(usuario!, false));
        });
        (await Leer2faActivaAsync(arnes.CadenaPropietario, usuarioId)).Should().BeFalse("barrera: la base ya lo tiene apagado");

        (await servicio.TieneDobleFactorActivoAsync()).Should().BeFalse(
            "el 2FA se lee de la base, no de la cuenta rastreada por el circuito");
    }

    // ---------- Arnés ----------

    private static Task<ArnesDeArranqueRuntime> CrearArnesAsync(Guid actorId, string rol) =>
        ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: new TenantFijo(TenantA),
            actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(actorId)),
            currentUserServicePersonalizado: new UsuarioFijo(actorId, rol, TenantA));

    private static async Task EnAmbitoAsync(ArnesDeArranqueRuntime arnes, Func<UserManager<ApplicationUser>, Task> accion)
    {
        using var ambito = arnes.Servicios.CreateScope();
        await accion(ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    private static SegundoFactorDeCuentasIdentity Puerto(IServiceProvider sp) => new(
        sp.GetRequiredService<UserManager<ApplicationUser>>(),
        sp.GetRequiredService<IUserStore<ApplicationUser>>(),
        sp.GetRequiredService<CaeManagerDbContext>(),
        new PuertaAccesoDatos());

    private static async Task<IReadOnlyList<string>> GenerarAsync(ArnesDeArranqueRuntime arnes)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        var handler = new GenerarCodigosRecuperacionCommandHandler(
            sp.GetRequiredService<ICurrentUserService>(), sp.GetRequiredService<IActorAuditoria>(), Puerto(sp));
        var resultado = await handler.Handle(new GenerarCodigosRecuperacionCommand(), default);
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        return resultado.Valor;
    }

    private static async Task GenerarCodigosDeAsync(ArnesDeArranqueRuntime arnes, Guid usuarioId)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var resultado = await Puerto(ambito.ServiceProvider)
            .GenerarCodigosRecuperacionAsync(usuarioId, CodigosRecuperacion.Cantidad);
        resultado.EsExitoso.Should().BeTrue();
        (await ContarTokensAsync(arnes.CadenaPropietario, usuarioId)).Should().Be(2,
            "control positivo: antes de restablecer hay clave y códigos que borrar");
    }

    private static async Task<CaeManager.Domain.Common.Result> RestablecerAsync(ArnesDeArranqueRuntime arnes, Guid usuarioId)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        var puerto = Puerto(sp);
        var handler = new RestablecerSegundoFactorCommandHandler(
            new AutorizacionRestablecerSegundoFactorAdministrador(
                sp.GetRequiredService<ICurrentUserService>(), sp.GetRequiredService<IActorAuditoria>(),
                sp.GetRequiredService<ITenantActual>(), puerto),
            puerto);
        return await handler.Handle(new RestablecerSegundoFactorCommand(usuarioId), default);
    }

    private static async Task CrearUsuarioCon2faAsync(
        ArnesDeArranqueRuntime arnes, Guid id, string email, Guid tenantId)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var um = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            NombreCompleto = email,
            EmailConfirmed = true,
            TenantId = tenantId,
        };

        // La preparación no es lo que se mide: su ámbito explícito para que un
        // fallo aquí no se confunda con el fenómeno bajo prueba.
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            Comprobar(await um.CreateAsync(usuario, "Arnes#2026Seguro"));
            Comprobar(await um.ResetAuthenticatorKeyAsync(usuario));
            Comprobar(await um.SetTwoFactorEnabledAsync(usuario, true));
        }
    }

    private static void Comprobar(IdentityResult resultado) =>
        resultado.Succeeded.Should().BeTrue(
            "errores: " + string.Join(", ", resultado.Errors.Select(e => e.Code + ":" + e.Description)));

    // ---------- Lecturas como PROPIETARIO (sin RLS): lo que de verdad quedó escrito ----------

    private static async Task<T> EscalarAsync<T>(string cadena, string sql, Guid usuarioId)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", usuarioId);
        var valor = await comando.ExecuteScalarAsync();
        return valor is null or DBNull ? default! : (T)valor;
    }

    private static Task<string?> LeerFilaCodigosAsync(string cadena, Guid usuarioId) => EscalarAsync<string?>(cadena,
        @"SELECT ""Value"" FROM ""AspNetUserTokens"" WHERE ""UserId"" = @u AND ""LoginProvider"" = '[AspNetUserStore]' AND ""Name"" = 'RecoveryCodes';",
        usuarioId);

    private static Task<long> ContarTokensAsync(string cadena, Guid usuarioId) => EscalarAsync<long>(cadena,
        @"SELECT COUNT(*) FROM ""AspNetUserTokens"" WHERE ""UserId"" = @u;", usuarioId);

    private static Task<bool> Leer2faActivaAsync(string cadena, Guid usuarioId) => EscalarAsync<bool>(cadena,
        @"SELECT ""TwoFactorEnabled"" FROM ""AspNetUsers"" WHERE ""Id"" = @u;", usuarioId);

    private static Task<string?> LeerSelloAsync(string cadena, Guid usuarioId) => EscalarAsync<string?>(cadena,
        @"SELECT ""SecurityStamp"" FROM ""AspNetUsers"" WHERE ""Id"" = @u;", usuarioId);

    private sealed record FilaDeAuditoria(Guid TenantId, string? DatosDespues, Guid? UsuarioId, Guid? ActorRealUsuarioId);

    private static async Task<FilaDeAuditoria> ObtenerRegistroAsync(string cadena, string entidadTipo, Guid entidadId)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT ""TenantId"", ""DatosDespues"", ""UsuarioId"", ""ActorRealUsuarioId""
FROM ""RegistrosAuditoria""
WHERE ""EntidadTipo"" = @entidadTipo AND ""EntidadId"" = @entidadId AND ""Accion"" = 'Modificado'
ORDER BY ""FechaUtc"" DESC LIMIT 1;";
        comando.Parameters.AddWithValue("entidadTipo", entidadTipo);
        comando.Parameters.AddWithValue("entidadId", entidadId);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue($"tiene que existir una fila de auditoría '{entidadTipo}' para {entidadId}");
        return new FilaDeAuditoria(
            lector.GetGuid(0),
            lector.IsDBNull(1) ? null : lector.GetString(1),
            lector.IsDBNull(2) ? null : lector.GetGuid(2),
            lector.IsDBNull(3) ? null : lector.GetGuid(3));
    }

    private static async Task NingunaFilaDeAuditoriaContieneAsync(string cadena, string secreto)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT COUNT(*) FROM ""RegistrosAuditoria""
WHERE COALESCE(""DatosAntes"", '') LIKE '%' || @s || '%' OR COALESCE(""DatosDespues"", '') LIKE '%' || @s || '%';";
        comando.Parameters.AddWithValue("s", secreto);
        ((long)(await comando.ExecuteScalarAsync())!).Should().Be(0, "ningún código de recuperación entra en la auditoría");
    }

    /// <summary>
    /// Tenant de la sesión, salvo dentro de un <c>AmbitoTenantExplicito</c>, que
    /// manda igual que en el <c>TenantActual</c> real. Desde P1-M1 la preparación
    /// de una cuenta de otro Tenant necesita que el ámbito llegue al sellado:
    /// con el Tenant de sesión fijo, el alta de esa cuenta es un INSERT en un
    /// Tenant ajeno y la política de <c>AspNetUsers</c> lo rechaza (42501).
    /// </summary>
    private sealed class TenantFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantId;
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class UsuarioFijo(Guid usuarioId, string rol, Guid tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private static CurrentUserService CrearCurrentUserService(IServiceProvider circuito, Guid usuarioId) => new(
        new AuthenticationStateProviderFalso(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString()), new Claim(ClaimTypes.Role, "GestorCae")],
            "prueba"))),
        new HttpContextAccessorFalso(),
        new ClienteActivoSeleccionadoFalso(),
        circuito);

    private sealed class AuthenticationStateProviderFalso(ClaimsPrincipal usuario) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(usuario));
    }

    private sealed class HttpContextAccessorFalso : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => null;
            set => throw new NotSupportedException();
        }
    }

    private sealed class ClienteActivoSeleccionadoFalso : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => TenantA;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
