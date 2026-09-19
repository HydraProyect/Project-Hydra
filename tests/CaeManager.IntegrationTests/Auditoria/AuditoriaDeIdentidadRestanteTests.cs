using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// <b>Misión N6/V4 (2026-09-19)</b>: las dos escrituras de Identity que #704
/// dejó inventariadas pero sin auditar —vincular un login externo
/// (<c>AspNetUserLogins</c>, <c>IdentityEndpointsExtensions.AddLoginAsync</c>)
/// y regenerar la clave del autenticador de dos factores
/// (<c>AspNetUserTokens</c>,
/// <c>ConfigurarAutenticadorDosFactores.ResetAuthenticatorKeyAsync</c>,
/// hallazgo M1 de la sesión coordinadora)—.
///
/// <para>
/// <b>Por qué bajo <c>cae_app_runtime</c> y no con el arnés cómodo</b>:
/// <see cref="AuditoriaDeGestionDeUsuariosTests"/> entra por el
/// <c>ChangeTracker</c> con un <c>DbContext</c> del PROPIETARIO de la base, y
/// bajo ese rol RLS no filtra: verifica la lógica en memoria del interceptor
/// pero nunca la política real de Postgres. La fila de auditoría de estas dos
/// escrituras se sella con el <b>Tenant propietario de la cuenta</b> —que en
/// el camino sin sesión (callback SSO anónimo) no es el de la variable de
/// sesión <c>app.tenant_id</c>— y la política <c>aislamiento_tenant</c> de
/// "RegistrosAuditoria" la compara con <c>WITH CHECK</c>: sin la propagación
/// de <c>TenantSelladoInterceptor</c>, Postgres la rechaza con 42501 y el
/// <c>SaveChanges</c> entero se revierte. Ese modo de fallo —login SSO roto—
/// solo se observa con el rol restringido, que es lo que monta
/// <see cref="ArnesDeArranqueRuntime"/>.
/// </para>
/// </summary>
public class AuditoriaDeIdentidadRestanteTests
{
    private const string ProveedorExterno = "Microsoft";

    /// <summary>
    /// Reproduce el camino de <c>IdentityEndpointsExtensions</c>: el callback
    /// de Entra ID corre <b>antes</b> de que exista sesión de la aplicación
    /// (<c>AllowAnonymous</c>, y <c>SignInWithClaimsAsync</c> es posterior),
    /// así que <c>ITenantActual.TenantId</c> es null cuando
    /// <c>TenantRlsConnectionInterceptor</c> fija <c>app.tenant_id</c> al
    /// abrir la conexión. Si la fila de auditoría no propagase el Tenant
    /// propietario de la cuenta a la sesión RLS, este <c>AddLoginAsync</c>
    /// fallaría — y con él, todo login por SSO.
    /// </summary>
    [Fact]
    public async Task Vincular_un_login_externo_sin_sesion_deja_rastro_y_no_lo_rechaza_RLS()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        using var ambito = arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var usuario = await CrearUsuarioAsync(userManager, "sso-login@caemanager.local", TenantSeedData.IdPorDefecto);

        // Deliberadamente SIN AmbitoTenantExplicito: es el estado real del
        // callback SSO.
        var resultado = await userManager.AddLoginAsync(
            usuario, new UserLoginInfo(ProveedorExterno, "oid-de-entra-0001", ProveedorExterno));

        resultado.Succeeded.Should().BeTrue(
            "la fila 'LoginExterno' que audita esta vinculación hereda el TenantId del ApplicationUser, y sin " +
            "propagarlo a app.tenant_id la rechaza RLS con 42501 — errores: " +
            string.Join(", ", resultado.Errors.Select(e => e.Code + ":" + e.Description)));

        var registro = await ObtenerRegistroAsync(
            arnes.CadenaPropietario, EntidadTipoAuditoria.LoginExterno, usuario.Id);

        registro.Accion.Should().Be("Creado");
        registro.TenantId.Should().Be(TenantSeedData.IdPorDefecto, "la fila es del Tenant propietario de la cuenta");
        registro.DatosDespues.Should().Contain("oid-de-entra-0001",
            "sin el ProviderKey el registro no distingue qué identidad externa se vinculó, que es justo lo que " +
            "esta auditoría existe para responder — no es un secreto: es un identificador opaco del directorio");
        registro.DatosDespues.Should().Contain(ProveedorExterno);
    }

    /// <summary>
    /// Desvincular también: una cuenta a la que alguien le quita el login
    /// externo pierde un camino de entrada, y eso es igual de auditable que
    /// ganarlo (<c>EntityState.Deleted</c> pasa por la misma rama del
    /// interceptor, pero por un camino distinto — Deleted serializa
    /// DatosAntes, no DatosDespués).
    /// </summary>
    [Fact]
    public async Task Desvincular_un_login_externo_tambien_deja_rastro()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        using var ambito = arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var usuario = await CrearUsuarioAsync(userManager, "sso-desvincular@caemanager.local", TenantSeedData.IdPorDefecto);
        (await userManager.AddLoginAsync(
            usuario, new UserLoginInfo(ProveedorExterno, "oid-de-entra-0002", ProveedorExterno)))
            .Succeeded.Should().BeTrue();

        var resultado = await userManager.RemoveLoginAsync(usuario, ProveedorExterno, "oid-de-entra-0002");

        resultado.Succeeded.Should().BeTrue(
            "errores: " + string.Join(", ", resultado.Errors.Select(e => e.Code + ":" + e.Description)));

        var registro = await ObtenerRegistroAsync(
            arnes.CadenaPropietario, EntidadTipoAuditoria.LoginExterno, usuario.Id, accion: "Eliminado");

        registro.TenantId.Should().Be(TenantSeedData.IdPorDefecto);
        registro.DatosAntes.Should().Contain("oid-de-entra-0002", "en una baja lo que queda es el valor anterior");
    }

    /// <summary>
    /// El hallazgo M1: regenerar la clave del autenticador cambia qué código
    /// acepta el sistema como segundo factor de esa cuenta. Tiene que dejar
    /// rastro — y el rastro <b>no puede contener el secreto</b>: /auditoria la
    /// lee el rol Administrador, y una clave TOTP copiada ahí sería una
    /// segunda vía de suplantar el segundo factor de cualquiera.
    /// </summary>
    [Fact]
    public async Task Regenerar_la_clave_del_autenticador_deja_rastro_con_el_secreto_enmascarado()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        using var ambito = arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var usuario = await CrearUsuarioAsync(userManager, "reset-2fa@caemanager.local", TenantSeedData.IdPorDefecto);

        var resultado = await userManager.ResetAuthenticatorKeyAsync(usuario);
        resultado.Succeeded.Should().BeTrue(
            "errores: " + string.Join(", ", resultado.Errors.Select(e => e.Code + ":" + e.Description)));

        // Control positivo del enmascarado: si la clave fuera vacía o nula,
        // "no aparece en el historial" sería cierto por vacuidad.
        var claveReal = await userManager.GetAuthenticatorKeyAsync(usuario);
        claveReal.Should().NotBeNullOrWhiteSpace("sin clave real no se puede demostrar que NO se filtró");

        var registro = await ObtenerRegistroAsync(
            arnes.CadenaPropietario, EntidadTipoAuditoria.TokenDeUsuario, usuario.Id);

        registro.Accion.Should().Be("Creado");
        registro.TenantId.Should().Be(TenantSeedData.IdPorDefecto);
        registro.DatosDespues.Should().Contain("AuthenticatorKey",
            "el registro dice QUÉ token cambió; lo que se oculta es su valor");
        registro.DatosDespues.Should().Contain("***");
        registro.DatosDespues.Should().NotContain(claveReal!,
            "el secreto TOTP nunca entra en RegistrosAuditoria (PropiedadesSensiblesPorTipo)");

        await NingunaFilaDeAuditoriaContieneAsync(arnes.CadenaPropietario, claveReal!);
    }

    /// <summary>
    /// El mismo hallazgo ALTA que #704 cerró para <c>ApplicationUser</c>,
    /// ahora para las dos tablas nuevas: un Gestor CAE de un Operador CAE
    /// externo, operando dentro del Workspace operativo derivado de un Tenant
    /// beneficiario, que regenera su propio segundo factor. La fila se sella
    /// con el <b>Tenant propietario de su cuenta</b> (el del Operador), nunca
    /// con el del beneficiario cuyo workspace tiene seleccionado — mezclar el
    /// plano de Operación con el de Propiedad pondría la actividad de una
    /// cuenta ajena en la auditoría del beneficiario (ADR-011 § 1).
    /// </summary>
    [Fact]
    public async Task Desde_un_workspace_delegado_la_fila_se_sella_con_el_tenant_propietario_de_la_cuenta()
    {
        var tenantOperador = Guid.NewGuid();
        var tenantBeneficiario = Guid.NewGuid();
        var tenantActualDeTest = new TenantActualFijo { TenantId = tenantOperador };

        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false, tenantActualPersonalizado: tenantActualDeTest);
        using var ambito = arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var usuario = await CrearUsuarioAsync(userManager, "gestor-cae-operador@caemanager.local", tenantOperador);

        // A partir de aquí la sesión "ve" el tenant del workspace delegado.
        tenantActualDeTest.TenantId = tenantBeneficiario;

        (await userManager.ResetAuthenticatorKeyAsync(usuario)).Succeeded.Should().BeTrue();
        (await userManager.AddLoginAsync(
            usuario, new UserLoginInfo(ProveedorExterno, "oid-de-entra-0003", ProveedorExterno)))
            .Succeeded.Should().BeTrue();

        var token = await ObtenerRegistroAsync(arnes.CadenaPropietario, EntidadTipoAuditoria.TokenDeUsuario, usuario.Id);
        var login = await ObtenerRegistroAsync(arnes.CadenaPropietario, EntidadTipoAuditoria.LoginExterno, usuario.Id);

        token.TenantId.Should().Be(tenantOperador);
        login.TenantId.Should().Be(tenantOperador);

        await NingunaFilaDeAuditoriaTieneTenantAsync(arnes.CadenaPropietario, tenantBeneficiario);
    }

    /// <summary>
    /// ADR-011 § 8.5: una Sesión Privilegiada no puede confundir quién estaba
    /// detrás del teclado con a quién se simulaba. <c>UsuarioId</c> guarda el
    /// Usuario simulado y <c>ActorRealUsuarioId</c> el Actor real — el mismo
    /// patrón que #704 probó para <c>ApplicationUser</c>, aquí para las dos
    /// tablas nuevas.
    /// </summary>
    [Fact]
    public async Task Bajo_sesion_privilegiada_la_fila_separa_actor_real_de_usuario_simulado()
    {
        var actorReal = Guid.NewGuid();
        var usuarioSimulado = Guid.NewGuid();
        var sesionPrivilegiadaId = Guid.NewGuid();

        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            actorAuditoriaPersonalizado: new ActorFijo(new ActorAuditoria(
                actorReal, usuarioSimulado, TipoViaAcceso.SesionPrivilegiada, sesionPrivilegiadaId)));
        using var ambito = arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var usuario = await CrearUsuarioAsync(userManager, "impersonado@caemanager.local", TenantSeedData.IdPorDefecto);

        (await userManager.ResetAuthenticatorKeyAsync(usuario)).Succeeded.Should().BeTrue();

        var registro = await ObtenerRegistroAsync(
            arnes.CadenaPropietario, EntidadTipoAuditoria.TokenDeUsuario, usuario.Id);

        registro.UsuarioId.Should().Be(usuarioSimulado, "UsuarioId es a quién se simulaba");
        registro.ActorRealUsuarioId.Should().Be(actorReal, "nunca se sustituye por el simulado: ese es el punto");
        registro.ViaAccesoId.Should().Be(sesionPrivilegiadaId);
    }

    private static async Task<ApplicationUser> CrearUsuarioAsync(
        UserManager<ApplicationUser> userManager, string email, Guid tenantId)
    {
        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = email,
            EmailConfirmed = true,
            TenantId = tenantId,
        };

        // El alta previa no es lo que se mide: se le da su ámbito para que un
        // fallo aquí no se confunda con el fenómeno bajo prueba.
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var creacion = await userManager.CreateAsync(usuario, "Arnes#2026Seguro");
            creacion.Succeeded.Should().BeTrue(
                "errores: " + string.Join(", ", creacion.Errors.Select(e => e.Code + ":" + e.Description)));
        }

        return usuario;
    }

    private sealed class TenantActualFijo : ITenantActual
    {
        public Guid? TenantId { get; set; }
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed record FilaDeAuditoria(
        string Accion, Guid TenantId, string? DatosAntes, string? DatosDespues,
        Guid? UsuarioId, Guid? ActorRealUsuarioId, Guid? ViaAccesoId);

    /// <summary>
    /// Se consulta como PROPIETARIO (sin RLS) a propósito: que la operación no
    /// fallara no basta por sí solo — podría haber escrito la fila con el
    /// TenantId equivocado, o no haberla escrito en absoluto.
    /// </summary>
    private static async Task<FilaDeAuditoria> ObtenerRegistroAsync(
        string cadenaPropietario, string entidadTipo, Guid entidadId, string? accion = null)
    {
        await using var conexion = new NpgsqlConnection(cadenaPropietario);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        // El filtro de acción se compone en el texto y no como parámetro
        // opcional: un `@accion IS NULL OR ...` con DBNull deja a Postgres sin
        // tipo que inferir (42P08). No hay interpolación de datos —`accion` es
        // un literal del propio test, y el valor sigue viajando parametrizado.
        comando.CommandText = @"
SELECT ""Accion"", ""TenantId"", ""DatosAntes"", ""DatosDespues"", ""UsuarioId"", ""ActorRealUsuarioId"", ""ViaAccesoId""
FROM ""RegistrosAuditoria""
WHERE ""EntidadTipo"" = @entidadTipo AND ""EntidadId"" = @entidadId"
            + (accion is null ? "" : @" AND ""Accion"" = @accion")
            + @"
ORDER BY ""FechaUtc"" DESC LIMIT 1;";
        comando.Parameters.AddWithValue("entidadTipo", entidadTipo);
        comando.Parameters.AddWithValue("entidadId", entidadId);
        if (accion is not null) comando.Parameters.AddWithValue("accion", accion);

        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue(
            $"tiene que existir una fila de auditoría '{entidadTipo}' para {entidadId}" +
            (accion is null ? "" : $" con acción '{accion}'"));

        return new FilaDeAuditoria(
            lector.GetString(0),
            lector.GetGuid(1),
            lector.IsDBNull(2) ? null : lector.GetString(2),
            lector.IsDBNull(3) ? null : lector.GetString(3),
            lector.IsDBNull(4) ? null : lector.GetGuid(4),
            lector.IsDBNull(5) ? null : lector.GetGuid(5),
            lector.IsDBNull(6) ? null : lector.GetGuid(6));
    }

    /// <summary>
    /// El enmascarado se comprueba sobre TODAS las filas, no solo sobre la que
    /// se fue a buscar: el mismo <c>SaveChanges</c> escribe también la fila
    /// "Usuario" (el SecurityStamp cambia), y basta con que el secreto se
    /// hubiera colado por cualquiera de las dos.
    /// </summary>
    private static async Task NingunaFilaDeAuditoriaContieneAsync(string cadenaPropietario, string secreto)
    {
        await using var conexion = new NpgsqlConnection(cadenaPropietario);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT COUNT(*) FROM ""RegistrosAuditoria""
WHERE COALESCE(""DatosAntes"", '') LIKE '%' || @secreto || '%'
   OR COALESCE(""DatosDespues"", '') LIKE '%' || @secreto || '%';";
        comando.Parameters.AddWithValue("secreto", secreto);

        var filas = (long)(await comando.ExecuteScalarAsync())!;
        filas.Should().Be(0, "ninguna fila de auditoría puede contener el secreto TOTP en claro");
    }

    private static async Task NingunaFilaDeAuditoriaTieneTenantAsync(string cadenaPropietario, Guid tenantProhibido)
    {
        await using var conexion = new NpgsqlConnection(cadenaPropietario);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT COUNT(*) FROM ""RegistrosAuditoria"" WHERE ""TenantId"" = @tenant;";
        comando.Parameters.AddWithValue("tenant", tenantProhibido);

        var filas = (long)(await comando.ExecuteScalarAsync())!;
        filas.Should().Be(0,
            "ninguna fila puede quedar sellada con el Tenant beneficiario cuyo Workspace operativo derivado " +
            "estaba seleccionado: la cuenta es del Operador CAE externo");
    }
}
