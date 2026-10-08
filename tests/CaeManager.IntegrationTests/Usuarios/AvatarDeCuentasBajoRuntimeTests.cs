using CaeManager.Application.Common;
using CaeManager.Application.Usuarios.Commands.ElegirAvatarPropio;
using CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio;
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
/// Avatar de usuario contra PostgreSQL real con el rol <c>cae_app_runtime</c>
/// (<see cref="ArnesDeArranqueRuntime"/>), con el handler real y
/// <see cref="AvatarDeCuentasIdentity"/>, no con dobles. Las reglas de autorización caso a
/// caso están en <c>AvatarDeUsuarioTests</c> (Application); aquí se mide lo que solo la
/// base puede decir: que la columna existe tras las migraciones, que el rol de runtime
/// puede escribir la de la propia cuenta con un <c>UPDATE</c> directo, y que ese mismo
/// <c>UPDATE</c> no alcanza la cuenta de otro Tenant propietario.
/// </summary>
public class AvatarDeCuentasBajoRuntimeTests
{
    private static readonly Guid TenantA = TenantSeedData.IdPorDefecto;

    [Fact]
    public async Task La_propia_cuenta_elige_su_avatar_lo_lee_y_vuelve_a_sus_iniciales()
    {
        var usuarioId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(usuarioId);
        await CrearUsuarioAsync(arnes, usuarioId, "avatar-propio@caemanager.local", TenantA);
        (await LeerAvatarAsync(arnes.CadenaPropietario, usuarioId)).Should().BeNull("barrera: una cuenta nace sin avatar");

        var elegir = await ElegirAsync(arnes, "buho-ambar");

        elegir.EsExitoso.Should().BeTrue(elegir.EsFallido ? elegir.Error.Mensaje : "");
        (await LeerAvatarAsync(arnes.CadenaPropietario, usuarioId)).Should().Be("buho-ambar");
        (await LeerPropioAsync(arnes)).Should().Be("buho-ambar");

        var quitar = await ElegirAsync(arnes, null);

        quitar.EsExitoso.Should().BeTrue(quitar.EsFallido ? quitar.Error.Mensaje : "");
        (await LeerAvatarAsync(arnes.CadenaPropietario, usuarioId)).Should().BeNull("quitar el avatar deja la columna vacía, no una cadena");
        (await LeerPropioAsync(arnes)).Should().BeNull();
    }

    [Fact]
    public async Task Elegir_avatar_no_falla_aunque_el_sello_de_concurrencia_de_la_cuenta_haya_cambiado()
    {
        // La cuenta se escribe en cada navegación (ActividadUsuarioService) y su
        // ConcurrencyStamp cambia: por eso el puerto no pasa por UserManager.UpdateAsync.
        var usuarioId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(usuarioId);
        await CrearUsuarioAsync(arnes, usuarioId, "avatar-sello@caemanager.local", TenantA);

        using var circuito = arnes.Servicios.CreateScope();
        var sp = circuito.ServiceProvider;
        // El circuito ya tiene rastreada la cuenta con el sello de ahora…
        var rastreada = await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(usuarioId.ToString());
        rastreada.Should().NotBeNull();
        // …y otra escritura lo cambia por debajo.
        await EjecutarComoPropietarioAsync(arnes.CadenaPropietario,
            """UPDATE "AspNetUsers" SET "ConcurrencyStamp" = 'otro-sello' WHERE "Id" = @u""", usuarioId);

        var resultado = await Handler(sp).Handle(new ElegirAvatarPropioCommand("zorro-verde"), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        (await LeerAvatarAsync(arnes.CadenaPropietario, usuarioId)).Should().Be("zorro-verde");
    }

    [Fact]
    public async Task El_puerto_no_alcanza_la_cuenta_de_otro_Tenant_propietario()
    {
        var sesionId = Guid.NewGuid();
        var ajenaId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(sesionId);
        await CrearUsuarioAsync(arnes, sesionId, "avatar-sesion@caemanager.local", TenantA);
        await CrearUsuarioAsync(arnes, ajenaId, "avatar-ajena@caemanager.local", Guid.NewGuid());

        using var circuito = arnes.Servicios.CreateScope();
        var puerto = Puerto(circuito.ServiceProvider);

        // Control positivo: el mismo puerto, en el mismo ámbito, sí escribe la cuenta de sesión.
        (await puerto.GuardarAsync(sesionId, "rana-verde")).EsExitoso.Should().BeTrue();
        (await LeerAvatarAsync(arnes.CadenaPropietario, sesionId)).Should().Be("rana-verde");

        var ajena = await puerto.GuardarAsync(ajenaId, "oso-neutro");

        ajena.EsFallido.Should().BeTrue("la RLS de AspNetUsers no deja ver ni tocar la fila de otro Tenant");
        ajena.Error.Codigo.Should().Be("Avatar.NoGuardado");
        (await LeerAvatarAsync(arnes.CadenaPropietario, ajenaId)).Should().BeNull("la cuenta ajena queda como estaba");
        (await puerto.ObtenerAsync(ajenaId)).Should().BeNull();
    }

    // ---------- Arnés ----------

    private static Task<ArnesDeArranqueRuntime> CrearArnesAsync(Guid actorId) =>
        ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: new TenantFijo(TenantA),
            actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(actorId)),
            currentUserServicePersonalizado: new UsuarioFijo(actorId, "Consulta", TenantA));

    private static AvatarDeCuentasIdentity Puerto(IServiceProvider sp) =>
        new(sp.GetRequiredService<CaeManagerDbContext>(), new PuertaAccesoDatos());

    private static ElegirAvatarPropioCommandHandler Handler(IServiceProvider sp) => new(
        sp.GetRequiredService<ICurrentUserService>(), sp.GetRequiredService<IActorAuditoria>(), Puerto(sp));

    private static async Task<CaeManager.Domain.Common.Result> ElegirAsync(ArnesDeArranqueRuntime arnes, string? clave)
    {
        using var ambito = arnes.Servicios.CreateScope();
        return await Handler(ambito.ServiceProvider).Handle(new ElegirAvatarPropioCommand(clave), default);
    }

    private static async Task<string?> LeerPropioAsync(ArnesDeArranqueRuntime arnes)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        return await new ObtenerAvatarPropioQueryHandler(sp.GetRequiredService<ICurrentUserService>(), Puerto(sp))
            .Handle(new ObtenerAvatarPropioQuery(), default);
    }

    private static async Task CrearUsuarioAsync(ArnesDeArranqueRuntime arnes, Guid id, string email, Guid tenantId)
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
            var resultado = await um.CreateAsync(usuario, "Arnes#2026Seguro");
            resultado.Succeeded.Should().BeTrue(
                "errores: " + string.Join(", ", resultado.Errors.Select(e => e.Code + ":" + e.Description)));
        }
    }

    // ---------- Como PROPIETARIO (sin RLS): lo que de verdad quedó escrito ----------

    private static async Task<string?> LeerAvatarAsync(string cadena, Guid usuarioId)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """SELECT "Avatar" FROM "AspNetUsers" WHERE "Id" = @u""";
        comando.Parameters.AddWithValue("u", usuarioId);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("la cuenta tiene que existir para que un NULL signifique «sin avatar»");
        return lector.IsDBNull(0) ? null : lector.GetString(0);
    }

    private static async Task EjecutarComoPropietarioAsync(string cadena, string sql, Guid usuarioId)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", usuarioId);
        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
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
