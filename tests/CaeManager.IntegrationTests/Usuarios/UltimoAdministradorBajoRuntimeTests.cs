using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.EliminarUsuarioPendiente;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Usuarios;

/// <summary>
/// La regla «un Tenant no se queda sin Administrador» (decisión del 2026-10-30) y la atomicidad
/// de la rotación del sello con el cambio de rol, contra PostgreSQL real con el rol
/// <c>cae_app_runtime</c> (<see cref="ArnesDeArranqueRuntime"/>). Las ramas de cada Command están
/// en <c>GestionCuentasCommandsTests</c> (Application) con un doble; aquí se mide lo que el doble
/// no puede decir: quién cuenta como Administrador bajo RLS, que el candado es el mismo que toman
/// los triggers de alta y que dos retiradas a la vez no dejan el Tenant sin ninguno.
/// </summary>
public class UltimoAdministradorBajoRuntimeTests
{
    private static readonly Guid TenantA = TenantSeedData.IdPorDefecto;
    private const string Contrasena = "Arnes#2026Seguro";

    [Fact]
    public async Task Una_cuenta_sin_activar_cuenta_como_Administrador_y_una_desactivada_no()
    {
        var actorId = Guid.NewGuid();
        var pendienteA = Guid.NewGuid();
        var pendienteB = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(actorId);
        await CrearCuentaAsync(arnes, pendienteA, "pendiente-a@caemanager.local", null, Roles.Administrador);
        await CrearCuentaAsync(arnes, pendienteB, "pendiente-b@caemanager.local", null, Roles.Administrador);

        (await HayOtroAsync(arnes, TenantA, pendienteA)).Should().BeTrue("la otra cuenta sigue sin activar, pero cuenta");

        await DesactivarAsync(arnes, pendienteB);

        (await HayOtroAsync(arnes, TenantA, pendienteA)).Should().BeFalse("la desactivada no sostiene el Tenant");
    }

    [Fact]
    public async Task Otro_rol_y_otro_Tenant_no_cuentan_como_Administrador_del_Tenant()
    {
        var actorId = Guid.NewGuid();
        var administradorId = Guid.NewGuid();
        var gestoraId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(actorId);
        await CrearCuentaAsync(arnes, administradorId, "admin@caemanager.local", Contrasena, Roles.Administrador);
        await CrearCuentaAsync(arnes, gestoraId, "gestora@caemanager.local", Contrasena, Roles.GestorCae);

        (await HayOtroAsync(arnes, TenantA, administradorId)).Should().BeFalse("un Gestor CAE no es Administrador");
        (await HayOtroAsync(arnes, TenantA, gestoraId)).Should().BeTrue("control positivo: el Administrador se ve bajo RLS");
        (await HayOtroAsync(arnes, Guid.NewGuid(), gestoraId)).Should().BeFalse(
            "el Administrador del Tenant A no sostiene otro Tenant");
    }

    [Fact]
    public async Task Eliminar_al_ultimo_Administrador_sin_activar_se_rechaza_y_la_cuenta_sigue()
    {
        var actorId = Guid.NewGuid();
        var pendienteId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(actorId);
        await CrearCuentaAsync(arnes, pendienteId, "pendiente@caemanager.local", null, Roles.Administrador);

        var resultado = await EliminarAsync(arnes, pendienteId);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(AutoridadSobreCuentas.UltimoAdministrador.Codigo);
        (await ExisteAsync(arnes.CadenaPropietario, pendienteId)).Should().BeTrue();
    }

    [Fact]
    public async Task Si_queda_otro_Administrador_la_cuenta_sin_activar_se_elimina()
    {
        var actorId = Guid.NewGuid();
        var pendienteId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(actorId);
        await CrearCuentaAsync(arnes, Guid.NewGuid(), "admin@caemanager.local", Contrasena, Roles.Administrador);
        await CrearCuentaAsync(arnes, pendienteId, "pendiente@caemanager.local", null, Roles.Administrador);

        var resultado = await EliminarAsync(arnes, pendienteId);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        (await ExisteAsync(arnes.CadenaPropietario, pendienteId)).Should().BeFalse();
    }

    [Fact]
    public async Task Dos_retiradas_a_la_vez_dejan_al_menos_un_Administrador()
    {
        var actorId = Guid.NewGuid();
        var pendienteA = Guid.NewGuid();
        var pendienteB = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(actorId);
        await CrearCuentaAsync(arnes, pendienteA, "pendiente-a@caemanager.local", null, Roles.Administrador);
        await CrearCuentaAsync(arnes, pendienteB, "pendiente-b@caemanager.local", null, Roles.Administrador);

        var resultados = await Task.WhenAll(
            Task.Run(() => EliminarAsync(arnes, pendienteA)),
            Task.Run(() => EliminarAsync(arnes, pendienteB)));

        resultados.Count(r => r.EsExitoso).Should().Be(1, "cada retirada ve lo que confirmó la otra");
        resultados.Single(r => r.EsFallido).Error.Codigo.Should().Be(AutoridadSobreCuentas.UltimoAdministrador.Codigo);
        var quedan = new[]
        {
            await ExisteAsync(arnes.CadenaPropietario, pendienteA),
            await ExisteAsync(arnes.CadenaPropietario, pendienteB),
        };
        quedan.Count(existe => existe).Should().Be(1);
    }

    /// <summary>
    /// El runtime no tiene EXECUTE sobre <c>app_bloquear_administradores_de_tenant</c> y repite su
    /// clave en C#: si se separaran, las retiradas dejarían de serializarse con las altas sin que
    /// nada fallara. Aquí la función, como propietario, choca con el candado que tomó el runtime.
    /// </summary>
    [Fact]
    public async Task El_candado_del_runtime_es_el_de_la_funcion_de_los_triggers()
    {
        await using var arnes = await CrearArnesAsync(Guid.NewGuid());
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        var estadoDentro = (string?)null;

        var resultado = await new TransaccionDeComando(sp.GetRequiredService<CaeManagerDbContext>()).EjecutarAsync(async ct =>
        {
            await Administradores(sp).BloquearAsync(TenantA, ct);
            estadoDentro = await IntentarLaFuncionComoPropietarioAsync(arnes.CadenaPropietario, TenantA);
            return Result.Exito();
        });

        resultado.EsExitoso.Should().BeTrue();
        estadoDentro.Should().Be(PostgresErrorCodes.LockNotAvailable, "mismo candado que la función");
        (await IntentarLaFuncionComoPropietarioAsync(arnes.CadenaPropietario, TenantA)).Should().BeNull(
            "control positivo: terminada la transacción, el candado queda libre");
    }

    [Fact]
    public async Task El_candado_fuera_de_una_transaccion_se_niega()
    {
        await using var arnes = await CrearArnesAsync(Guid.NewGuid());
        using var ambito = arnes.Servicios.CreateScope();

        var tomar = () => Administradores(ambito.ServiceProvider).BloquearAsync(TenantA);

        await tomar.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// El comentario de <c>CambiarRolAsync</c> dice que el sello nuevo se persiste junto al rol.
    /// Una transacción de Command que falla después de cambiar el rol no deja ni rol nuevo ni sello
    /// nuevo: la cuenta no pierde la sesión por un cambio que no ocurrió.
    /// </summary>
    [Fact]
    public async Task Si_la_transaccion_falla_tras_cambiar_el_rol_no_quedan_ni_el_rol_ni_el_sello()
    {
        var cuentaId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(Guid.NewGuid());
        await CrearCuentaAsync(arnes, cuentaId, "cuenta@caemanager.local", Contrasena, Roles.Administrador);
        var selloAntes = await LeerSelloAsync(arnes.CadenaPropietario, cuentaId);
        var abortada = Error.Crear("Prueba.Abortada", "La transacción se aborta después del cambio.");

        var resultado = await CambiarRolEnTransaccionAsync(arnes, cuentaId, Roles.GestorCae, Result.Fallo(abortada));

        resultado.EsFallido.Should().BeTrue();
        (await LeerSelloAsync(arnes.CadenaPropietario, cuentaId)).Should().Be(selloAntes);
        (await LeerRolesAsync(arnes.CadenaPropietario, cuentaId)).Should().Equal(Roles.Administrador);
    }

    [Fact]
    public async Task Si_la_transaccion_confirma_quedan_el_rol_nuevo_y_el_sello_rotado()
    {
        var cuentaId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(Guid.NewGuid());
        await CrearCuentaAsync(arnes, cuentaId, "cuenta@caemanager.local", Contrasena, Roles.Administrador);
        var selloAntes = await LeerSelloAsync(arnes.CadenaPropietario, cuentaId);

        var resultado = await CambiarRolEnTransaccionAsync(arnes, cuentaId, Roles.GestorCae, Result.Exito());

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        (await LeerSelloAsync(arnes.CadenaPropietario, cuentaId)).Should().NotBe(selloAntes);
        (await LeerRolesAsync(arnes.CadenaPropietario, cuentaId)).Should().Equal(Roles.GestorCae);
    }

    private static Task<ArnesDeArranqueRuntime> CrearArnesAsync(Guid actorId) =>
        ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: new TenantFijo(TenantA),
            actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(actorId)),
            currentUserServicePersonalizado: new UsuarioFijo(actorId, Roles.Administrador, TenantA));

    private static GestionCuentasUsuarioIdentity Puerto(IServiceProvider sp)
    {
        var usuarios = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var contexto = sp.GetRequiredService<CaeManagerDbContext>();
        var puerta = new PuertaAccesoDatos();
        return new GestionCuentasUsuarioIdentity(usuarios, puerta, Directorio(sp), contexto);
    }

    private static DirectorioUsuariosTenant Directorio(IServiceProvider sp)
    {
        var contexto = sp.GetRequiredService<CaeManagerDbContext>();
        return new DirectorioUsuariosTenant(
            sp.GetRequiredService<UserManager<ApplicationUser>>(), contexto,
            sp.GetRequiredService<ITenantActual>(), new PuertaAccesoDatos(), contexto, null!);
    }

    private static AdministradoresDelTenant Administradores(IServiceProvider sp) =>
        new(sp.GetRequiredService<CaeManagerDbContext>(), Directorio(sp));

    private static async Task<bool> HayOtroAsync(ArnesDeArranqueRuntime arnes, Guid tenantId, Guid excluido)
    {
        using var ambito = arnes.Servicios.CreateScope();
        return await Administradores(ambito.ServiceProvider).HayOtroAdministradorAsync(tenantId, excluido);
    }

    /// <summary>Por el Command real, en un ámbito propio como cada pulsación de «Eliminar».</summary>
    private static async Task<Result> EliminarAsync(ArnesDeArranqueRuntime arnes, Guid usuarioId)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        return await new EliminarUsuarioPendienteCommandHandler(
                Puerto(sp), sp.GetRequiredService<ICurrentUserService>(), sp.GetRequiredService<ITenantActual>(),
                new TransaccionDeComando(sp.GetRequiredService<CaeManagerDbContext>()), Administradores(sp))
            .Handle(new EliminarUsuarioPendienteCommand(usuarioId), default);
    }

    private static async Task<Result> CambiarRolEnTransaccionAsync(
        ArnesDeArranqueRuntime arnes, Guid usuarioId, string rolNuevo, Result desenlace)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        return await new TransaccionDeComando(sp.GetRequiredService<CaeManagerDbContext>()).EjecutarAsync(async ct =>
        {
            var cambio = await Puerto(sp).CambiarRolAsync(usuarioId, rolNuevo, ct);
            cambio.Desenlace.Should().Be(DesenlaceCambioRol.Cambiado, cambio.MotivoIdentity);
            return desenlace;
        });
    }

    private static async Task DesactivarAsync(ArnesDeArranqueRuntime arnes, Guid usuarioId)
    {
        using var ambito = arnes.Servicios.CreateScope();
        (await Puerto(ambito.ServiceProvider).CambiarActivacionAsync(usuarioId, activar: false)).EsExitoso.Should().BeTrue();
    }

    private static async Task CrearCuentaAsync(
        ArnesDeArranqueRuntime arnes, Guid id, string email, string? contrasena, string rol)
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
            TenantId = TenantA,
        };

        var alta = contrasena is null ? await um.CreateAsync(usuario) : await um.CreateAsync(usuario, contrasena);
        alta.Succeeded.Should().BeTrue("errores: " + string.Join(", ", alta.Errors.Select(e => e.Code + ":" + e.Description)));
        (await um.AddToRoleAsync(usuario, rol)).Succeeded.Should().BeTrue();
    }

    /// <summary>El código de error de PostgreSQL al chocar con el candado, o null si se obtuvo.</summary>
    private static async Task<string?> IntentarLaFuncionComoPropietarioAsync(string cadena, Guid tenantId)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SET LOCAL lock_timeout = '500ms'; SELECT public.app_bloquear_administradores_de_tenant(@t);";
        comando.Parameters.AddWithValue("t", tenantId);
        try
        {
            await comando.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException error)
        {
            return error.SqlState;
        }
    }

    private static async Task<bool> ExisteAsync(string cadena, Guid usuarioId)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT EXISTS (SELECT 1 FROM ""AspNetUsers"" WHERE ""Id"" = @u);";
        comando.Parameters.AddWithValue("u", usuarioId);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<string?> LeerSelloAsync(string cadena, Guid usuarioId)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT ""SecurityStamp"" FROM ""AspNetUsers"" WHERE ""Id"" = @u;";
        comando.Parameters.AddWithValue("u", usuarioId);
        return (string?)await comando.ExecuteScalarAsync();
    }

    private static async Task<List<string>> LeerRolesAsync(string cadena, Guid usuarioId)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            @"SELECT r.""Name"" FROM ""AspNetUserRoles"" ur JOIN ""AspNetRoles"" r ON r.""Id"" = ur.""RoleId"" WHERE ur.""UserId"" = @u;";
        comando.Parameters.AddWithValue("u", usuarioId);
        var roles = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            roles.Add(lector.GetString(0));
        return roles;
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

    private sealed class UsuarioFijo(Guid usuarioId, string rol, Guid tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
