using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// Cierra el hallazgo de CIERRE-TURNO-NOCTURNO-2026-09-18.md § 12: la gestión
/// de usuarios (<c>Usuarios.razor.cs</c>, <c>Roles.razor.cs</c>) escribe con
/// <c>UserManager</c> directo desde Web, sin pasar por MediatR, y hasta ahora
/// <see cref="AuditoriaInterceptor"/> solo cubría el namespace
/// <c>CaeManager.Domain</c> — <see cref="ApplicationUser"/> vive en
/// <c>CaeManager.Infrastructure.Identity</c> y quedaba fuera. El caso más
/// grave era el cambio de rol: no se podía saber quién hizo Administrador a
/// quién.
///
/// Estos tests entran por el <c>ChangeTracker</c> (como
/// <see cref="AuditoriaConIdentidadDualTests"/>), no por <c>UserManager</c>:
/// <c>UserManager.CreateAsync</c>/<c>AddToRoleAsync</c>/etc. terminan en el
/// mismo <c>DbContext.SaveChangesAsync</c> que estos tests ejercitan
/// directamente (<c>AddEntityFrameworkStores&lt;CaeManagerDbContext&gt;()</c>
/// en <c>InfrastructureServiceCollectionExtensions</c>), así que es el mismo
/// camino que <see cref="AuditoriaInterceptor"/> ve en producción — y por eso
/// la cobertura alcanza a CUALQUIER escritor de Identity presente o futuro,
/// no solo a la página <c>Usuarios</c>.
/// </summary>
public class AuditoriaDeGestionDeUsuariosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private const string PasswordHashSecreto = "SECRETO-HASH-DE-CONTRASENA-no-debe-aparecer-en-claro";
    private const string SecurityStampSecreto = "SECRETO-SECURITY-STAMP-no-debe-aparecer-en-claro";

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(ActorAuditoria.SinResolver);
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Crear_una_cuenta_deja_quien_la_creo_y_en_que_tenant()
    {
        var administradorId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();

        await using (var contexto = CrearContexto(ActorAuditoria.Normal(administradorId)))
        {
            contexto.Users.Add(NuevoUsuario(usuarioId));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync("Usuario", "Creado");

        registro.EntidadId.Should().Be(usuarioId);
        registro.UsuarioId.Should().Be(administradorId);
        registro.ActorRealUsuarioId.Should().Be(administradorId);
        registro.TenantId.Should().Be(_tenant, "TenantSelladoInterceptor sella RegistroAuditoria contra el tenant de quien opera");
    }

    /// <summary>
    /// El caso más grave del hallazgo: conceder un rol (incluido
    /// Administrador) es un alta en AspNetUserRoles
    /// (<see cref="IdentityUserRole{TKey}"/>), no una columna de
    /// <see cref="ApplicationUser"/> — antes de este cambio no dejaba NINGÚN
    /// rastro, en ningún namespace.
    /// </summary>
    [Fact]
    public async Task Conceder_un_rol_registra_quien_lo_concedio_y_a_quien_se_le_concedio()
    {
        var administradorId = Guid.NewGuid();
        var usuarioAfectadoId = Guid.NewGuid();

        await using (var contexto = CrearContexto(ActorAuditoria.SinResolver))
        {
            contexto.Users.Add(NuevoUsuario(usuarioAfectadoId));
            await contexto.SaveChangesAsync();
        }

        var rolId = await ObtenerRolIdAsync(Roles.Administrador);

        await using (var contexto = CrearContexto(ActorAuditoria.Normal(administradorId)))
        {
            contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = usuarioAfectadoId, RoleId = rolId });
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync("RolDeUsuario", "Creado");

        registro.EntidadId.Should().Be(usuarioAfectadoId, "EntidadId es a quién se le concedió el rol");
        registro.UsuarioId.Should().Be(administradorId, "quién lo concedió");
        registro.ActorRealUsuarioId.Should().Be(administradorId);
    }

    [Fact]
    public async Task Revocar_un_rol_tambien_deja_rastro()
    {
        var administradorId = Guid.NewGuid();
        var usuarioAfectadoId = Guid.NewGuid();
        var rolId = await ObtenerRolIdAsync(Roles.Consulta);

        await using (var contexto = CrearContexto(ActorAuditoria.SinResolver))
        {
            contexto.Users.Add(NuevoUsuario(usuarioAfectadoId));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(ActorAuditoria.SinResolver))
        {
            contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = usuarioAfectadoId, RoleId = rolId });
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(ActorAuditoria.Normal(administradorId)))
        {
            var asignacion = await contexto.UserRoles.SingleAsync(ur => ur.UserId == usuarioAfectadoId && ur.RoleId == rolId);
            contexto.UserRoles.Remove(asignacion);
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync("RolDeUsuario", "Eliminado");

        registro.EntidadId.Should().Be(usuarioAfectadoId);
        registro.UsuarioId.Should().Be(administradorId, "quién revocó el rol");
    }

    /// <summary>
    /// ADR-011 § 8.5: una sesión privilegiada (o una impersonación) nunca
    /// puede atribuirse solo al usuario simulado — el actor real se conserva
    /// siempre. Ejercitado aquí sobre el caso concreto de la misión: conceder
    /// un rol.
    /// </summary>
    [Fact]
    public async Task Conceder_un_rol_en_sesion_privilegiada_conserva_el_actor_real_y_el_simulado()
    {
        var actorRealId = Guid.NewGuid();
        var administradorSimuladoId = Guid.NewGuid();
        var usuarioAfectadoId = Guid.NewGuid();
        var sesionPrivilegiadaId = Guid.NewGuid();
        var rolId = await ObtenerRolIdAsync(Roles.Administrador);

        await using (var contexto = CrearContexto(ActorAuditoria.SinResolver))
        {
            contexto.Users.Add(NuevoUsuario(usuarioAfectadoId));
            await contexto.SaveChangesAsync();
        }

        var actor = new ActorAuditoria(actorRealId, administradorSimuladoId, TipoViaAcceso.SesionPrivilegiada, sesionPrivilegiadaId);
        await using (var contexto = CrearContexto(actor))
        {
            contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = usuarioAfectadoId, RoleId = rolId });
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync("RolDeUsuario", "Creado");

        registro.UsuarioId.Should().Be(administradorSimuladoId, "el autor visible es a quien se simula");
        registro.ActorRealUsuarioId.Should().Be(actorRealId, "el actor real nunca se pierde tras el simulado");
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.SesionPrivilegiada);
        registro.ViaAccesoId.Should().Be(sesionPrivilegiadaId);
    }

    [Fact]
    public async Task El_password_hash_no_aparece_en_claro_en_la_auditoria()
    {
        var usuarioId = Guid.NewGuid();

        await using (var contexto = CrearContexto(ActorAuditoria.SinResolver))
        {
            var usuario = NuevoUsuario(usuarioId);
            usuario.PasswordHash = PasswordHashSecreto;
            contexto.Users.Add(usuario);
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync("Usuario", "Creado");

        registro.DatosDespues.Should().NotContain(PasswordHashSecreto);
        registro.DatosDespues.Should().Contain("\"PasswordHash\":\"***\"");
    }

    [Fact]
    public async Task El_security_stamp_no_aparece_en_claro_al_rotarlo()
    {
        var usuarioId = Guid.NewGuid();

        await using (var contexto = CrearContexto(ActorAuditoria.SinResolver))
        {
            contexto.Users.Add(NuevoUsuario(usuarioId));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(ActorAuditoria.Normal(Guid.NewGuid())))
        {
            var usuario = await contexto.Users.SingleAsync(u => u.Id == usuarioId);
            // DebeCambiarContrasena viaja junto al stamp para que la fila no
            // caiga en la exclusión de "solo silenciosas" (no aplica aquí,
            // pero deja el caso realista: RestablecerContrasena.razor.cs
            // también toca otras columnas en el mismo SaveChanges).
            usuario.DebeCambiarContrasena = false;
            contexto.Entry(usuario).Property(u => u.SecurityStamp).CurrentValue = SecurityStampSecreto;
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync("Usuario", "Modificado");

        registro.DatosDespues.Should().NotContain(SecurityStampSecreto);
        registro.DatosDespues.Should().Contain("\"SecurityStamp\":\"***\"");
    }

    /// <summary>
    /// Prueba de sensibilidad de PropiedadesSilenciosasPorTipo: sin ella este
    /// test fallaría, porque <c>ActividadUsuarioService.RegistrarYEvaluarAsync</c>
    /// escribe exactamente este patrón (solo <c>UltimaActividadUtc</c>) una
    /// vez por minuto por cada usuario activo.
    /// </summary>
    [Fact]
    public async Task Actualizar_solo_la_ultima_actividad_no_genera_fila_de_auditoria()
    {
        var usuarioId = Guid.NewGuid();

        await using (var contexto = CrearContexto(ActorAuditoria.SinResolver))
        {
            contexto.Users.Add(NuevoUsuario(usuarioId));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(ActorAuditoria.Normal(Guid.NewGuid())))
        {
            var usuario = await contexto.Users.SingleAsync(u => u.Id == usuarioId);
            usuario.UltimaActividadUtc = DateTime.UtcNow;
            await contexto.SaveChangesAsync();
        }

        await using var verificacion = CrearContexto(ActorAuditoria.SinResolver);
        var hayModificado = await verificacion.RegistrosAuditoria
            .AnyAsync(r => r.EntidadTipo == "Usuario" && r.EntidadId == usuarioId && r.Accion == "Modificado");

        hayModificado.Should().BeFalse(
            "un cambio de solo UltimaActividadUtc es el patrón exacto de ActividadUsuarioService y no debe dejar fila");
    }

    /// <summary>
    /// Defensa de la exclusión de arriba: si el campo silenciado viaja JUNTO
    /// con un cambio real (p. ej. activar/desactivar mientras se registra la
    /// visita), la fila se genera igual — la exclusión es del disparo
    /// "nada más que ruido", no un agujero para colar un cambio real.
    /// </summary>
    [Fact]
    public async Task Un_cambio_real_junto_a_la_ultima_actividad_si_se_audita()
    {
        var administradorId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();

        await using (var contexto = CrearContexto(ActorAuditoria.SinResolver))
        {
            contexto.Users.Add(NuevoUsuario(usuarioId));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(ActorAuditoria.Normal(administradorId)))
        {
            var usuario = await contexto.Users.SingleAsync(u => u.Id == usuarioId);
            usuario.UltimaActividadUtc = DateTime.UtcNow;
            usuario.PermisoConsultarAccesoDocumentosSensibles = true;
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync("Usuario", "Modificado");

        registro.DatosDespues.Should().Contain("PermisoConsultarAccesoDocumentosSensibles");
        registro.UsuarioId.Should().Be(administradorId);
    }

    /// <summary>
    /// Hallazgo de Codex antes de abrir la PR (P1): un intento de contraseña
    /// fallido incrementa <see cref="ApplicationUser.AccessFailedCount"/> vía
    /// <c>UserManager.AccessFailedAsync</c> — sin sesión de CAE Manager, así
    /// que <see cref="ITenantActual.TenantId"/> es <c>null</c>. Antes de la
    /// excepción en <c>TenantSelladoInterceptor.ResolverTenantDeIdentidadAuditada</c>,
    /// el <c>RegistroAuditoria</c> nuevo de esa fila se rechazaba por "sin
    /// tenant resuelto" y el <c>SaveChanges</c> entero se revertía: el
    /// intento de login devolvía 500 en vez de "contraseña incorrecta", y el
    /// contador de bloqueo por fuerza bruta (P1-2, Lockout) nunca avanzaba.
    /// </summary>
    [Fact]
    public async Task Un_intento_de_contrasena_fallido_sin_sesion_no_revierte_por_falta_de_tenant()
    {
        var usuarioId = Guid.NewGuid();
        var tenantDelUsuario = Guid.NewGuid();

        await using (var contexto = CrearContexto(ActorAuditoria.SinResolver, tenantDelUsuario))
        {
            var usuario = NuevoUsuario(usuarioId);
            usuario.TenantId = tenantDelUsuario;
            contexto.Users.Add(usuario);
            await contexto.SaveChangesAsync();
        }

        // Sin tenant ambiental: reproduce exactamente el intento de login
        // anónimo — ITenantActual.TenantId es null.
        var accion = async () =>
        {
            await using var contexto = CrearContexto(ActorAuditoria.SinResolver, tenantAmbiental: null);
            var usuario = await contexto.Users.SingleAsync(u => u.Id == usuarioId);
            usuario.AccessFailedCount += 1;
            await contexto.SaveChangesAsync();
        };

        await accion.Should().NotThrowAsync(
            "un intento de contraseña incorrecto no debe devolver 500 solo porque no hay sesión");

        // El filtro global de tenant exige leer con el mismo tenant con el
        // que se escribió — no con _tenant, que es el de otros tests de esta
        // clase.
        var registro = await ObtenerRegistroAsync("Usuario", "Modificado", tenantDelUsuario);
        registro.TenantId.Should().Be(tenantDelUsuario,
            "sin tenant ambiental, el sellado cae al TenantId propio del ApplicationUser auditado");
    }

    /// <summary>
    /// Mismo hallazgo que el test anterior, para el alta (no la edición): el
    /// auto-aprovisionamiento por SSO (<c>IdentityEndpointsExtensions</c>)
    /// crea la cuenta ANTES de que exista sesión de CAE Manager, con el
    /// TenantId ya resuelto por configuración (no por <c>ITenantActual</c>).
    /// </summary>
    [Fact]
    public async Task Autoprovisionar_una_cuenta_sin_sesion_no_revierte_por_falta_de_tenant()
    {
        var usuarioId = Guid.NewGuid();
        var tenantDestino = Guid.NewGuid();

        var accion = async () =>
        {
            await using var contexto = CrearContexto(ActorAuditoria.SinResolver, tenantAmbiental: null);
            var usuario = NuevoUsuario(usuarioId);
            usuario.TenantId = tenantDestino;
            contexto.Users.Add(usuario);
            await contexto.SaveChangesAsync();
        };

        await accion.Should().NotThrowAsync(
            "el alta por SSO corre antes de que exista sesión de CAE Manager");

        var registro = await ObtenerRegistroAsync("Usuario", "Creado", tenantDestino);
        registro.EntidadId.Should().Be(usuarioId);
        registro.TenantId.Should().Be(tenantDestino);
    }

    private static ApplicationUser NuevoUsuario(Guid id) => new()
    {
        Id = id,
        UserName = $"{id}@ejemplo.com",
        NormalizedUserName = $"{id}@EJEMPLO.COM",
        Email = $"{id}@ejemplo.com",
        NormalizedEmail = $"{id}@EJEMPLO.COM",
        NombreCompleto = "Usuario de prueba",
        TenantId = Guid.NewGuid() // sin relevancia: ApplicationUser no es EntidadConTenant y no lo sella TenantSelladoInterceptor.
    };

    /// <summary>
    /// Los seis roles del sistema los siembra la migración de línea base
    /// (InsertData sobre AspNetRoles, ver Roles.cs) — no se crean aquí, para
    /// no chocar con esa siembra ni divergir del esquema real de producción.
    /// </summary>
    private async Task<Guid> ObtenerRolIdAsync(string nombreRol)
    {
        await using var contexto = CrearContexto(ActorAuditoria.SinResolver);

        return (await contexto.Roles.SingleAsync(r => r.Name == nombreRol)).Id;
    }

    /// <summary>
    /// <paramref name="tenant"/> por defecto es <see cref="_tenant"/> — el
    /// filtro global de tenant exige leer con el mismo tenant con el que se
    /// escribió, así que los tests que sellan contra un tenant propio (sin
    /// sesión) deben pasarlo explícitamente.
    /// </summary>
    private async Task<RegistroAuditoria> ObtenerRegistroAsync(string entidadTipo, string accion, Guid? tenant = null)
    {
        await using var contexto = CrearContexto(ActorAuditoria.SinResolver, tenant ?? _tenant);

        return await contexto.RegistrosAuditoria
            .Where(r => r.EntidadTipo == entidadTipo && r.Accion == accion)
            .OrderByDescending(r => r.FechaUtc)
            .FirstAsync();
    }

    private CaeManagerDbContext CrearContexto(ActorAuditoria actor) => CrearContexto(actor, _tenant);

    /// <summary>
    /// <paramref name="tenantAmbiental"/> en <c>null</c> reproduce el camino
    /// sin sesión (login anónimo, restablecer contraseña, auto-provisión por
    /// SSO antes de que exista sesión de CAE Manager) — ver
    /// <see cref="Un_intento_de_contrasena_fallido_sin_sesion_no_revierte_por_falta_de_tenant"/>.
    /// </summary>
    private CaeManagerDbContext CrearContexto(ActorAuditoria actor, Guid? tenantAmbiental)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantAmbiental };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            // Mismo orden que en producción (ver InfrastructureServiceCollectionExtensions
            // y el comentario equivalente en AuditoriaConIdentidadDualTests): auditoría
            // primero, sellado después.
            .AddInterceptors(new AuditoriaInterceptor(new ActorAuditoriaFalso(actor)), new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
