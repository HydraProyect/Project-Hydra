using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// Hallazgo P1 de Codex, 5ª ronda: el fallback de tenant de
/// <c>TenantSelladoInterceptor</c> para Identity sin sesión (login fallido,
/// reset de contraseña anónimo, alta por SSO) solo asignaba el
/// <c>TenantId</c> correcto al objeto <c>RegistroAuditoria</c> EN MEMORIA —
/// nunca a <c>app.tenant_id</c>, la variable de sesión que la política RLS de
/// "RegistrosAuditoria" compara con <c>WITH CHECK</c>. Bajo el rol
/// restringido <c>cae_app_runtime</c> (confirmado en producción, ver
/// hydra-rls-fallo-cerrado-prerrequisito-despliegue), Postgres rechazaba el
/// INSERT con 42501 y el <c>SaveChanges</c> entero se revertía — el MISMO
/// síntoma original (login → 500) que ese fallback decía haber resuelto.
///
/// <para>
/// Los tests de <c>AuditoriaDeGestionDeUsuariosTests</c> que ya cubrían este
/// fallback NO detectaban el hueco: usan un <c>DbContext</c> de test contra
/// el rol PROPIETARIO (sin RLS activa), así que verificaban la lógica en
/// memoria del interceptor pero nunca ejercitaban la política real de
/// Postgres. Este archivo usa <see cref="ArnesDeArranqueRuntime"/> —el mismo
/// arnés que reproduce el arranque de producción conectando como
/// <c>cae_app_runtime</c>— precisamente para no repetir ese falso verde.
/// </para>
/// </summary>
public class PropagacionTenantRlsSinSesionTests
{
    [Fact]
    public async Task Crear_un_ApplicationUser_sin_ambito_de_tenant_no_lo_rechaza_RLS()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        using var ambito = arnes.Servicios.CreateScope();

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = new ApplicationUser
        {
            UserName = "sin-sesion-alta@caemanager.local",
            Email = "sin-sesion-alta@caemanager.local",
            NombreCompleto = "Alta sin sesión",
            EmailConfirmed = true,
            TenantId = TenantSeedData.IdPorDefecto,
        };

        // Deliberadamente SIN AmbitoTenantExplicito.Establecer(...): reproduce
        // login anónimo / reset de contraseña / SSO preautenticado, donde
        // ITenantActual.TenantId es null cuando TenantRlsConnectionInterceptor
        // fija app.tenant_id al abrir la conexión.
        var resultado = await userManager.CreateAsync(usuario, "Arnes#2026Seguro");

        resultado.Succeeded.Should().BeTrue(
            "el fallback de TenantSelladoInterceptor debe propagar el tenant también a la sesión RLS, " +
            "no solo al objeto en memoria — errores: " +
            string.Join(", ", resultado.Errors.Select(e => e.Code + ":" + e.Description)));

        await ComprobarTenantRealDelRegistroAsync(
            arnes.CadenaPropietario, "Usuario", usuario.Id, TenantSeedData.IdPorDefecto);
    }

    [Fact]
    public async Task Conceder_un_rol_sin_ambito_de_tenant_no_lo_rechaza_RLS()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        using var ambito = arnes.Servicios.CreateScope();

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = new ApplicationUser
        {
            UserName = "sin-sesion-rol@caemanager.local",
            Email = "sin-sesion-rol@caemanager.local",
            NombreCompleto = "Concesión sin sesión",
            EmailConfirmed = true,
            TenantId = TenantSeedData.IdPorDefecto,
        };

        using (CaeManager.Application.Common.AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
        {
            (await userManager.CreateAsync(usuario, "Arnes#2026Seguro")).Succeeded.Should().BeTrue(
                "el alta previa sí lleva ámbito — es la concesión de rol de abajo la que se prueba sin él");
        }

        // El caso más grave que esta auditoría existe para responder (ver
        // OBJECTIVE del incremento): conceder Administrador, sin sesión.
        var resultado = await userManager.AddToRoleAsync(usuario, Roles.Administrador);

        resultado.Succeeded.Should().BeTrue(
            "mismo hallazgo que el alta: la fila 'RolDeUsuario' que audita esta concesión hereda el " +
            "TenantId del ApplicationUser, y sin propagarlo a app.tenant_id la rechaza RLS — errores: " +
            string.Join(", ", resultado.Errors.Select(e => e.Code + ":" + e.Description)));

        await ComprobarTenantRealDelRegistroAsync(
            arnes.CadenaPropietario, "RolDeUsuario", usuario.Id, TenantSeedData.IdPorDefecto);
    }

    /// <summary>
    /// Hallazgo ALTA de sesión coordinadora (revisión de `8982cdc8`): el orden
    /// <c>tenantId ?? ResolverTenantDeIdentidadAuditada(...)</c> hacía ganar
    /// siempre al tenant del CONTEXTO cuando existía sesión — pero
    /// <c>TenantActual.TenantId</c> real (<c>TenantActual.cs:84</c>) es
    /// <c>clienteActivoSeleccionado.TenantIdSeleccionado ?? tenantId</c>: con
    /// un Workspace operativo derivado seleccionado, el tenant de sesión es
    /// el del Tenant BENEFICIARIO, no el del Operador CAE externo. Un Gestor
    /// CAE de ese operador, operando en el workspace del beneficiario, que
    /// cambia su propio teléfono/tema/2FA, generaba una fila de auditoría
    /// sellada con el TenantId del BENEFICIARIO — visible en la auditoría del
    /// beneficiario, aunque la cuenta (y el cambio) sean del operador. Mezcla
    /// el plano de Operación con el de Propiedad (ADR-011).
    ///
    /// <c>TenantActualFijo</c> simula el efecto NETO de esa composición sin
    /// reproducir toda la cadena de claims: la sesión "ve" el tenant del
    /// workspace seleccionado, sea cual sea el tenant propietario real de la
    /// cuenta que se está modificando en ese instante.
    /// </summary>
    [Fact]
    public async Task Modificar_el_propio_ApplicationUser_desde_un_workspace_delegado_sella_con_el_tenant_propietario()
    {
        var tenantOperador = Guid.NewGuid();
        var tenantBeneficiario = Guid.NewGuid();
        var tenantActualDeTest = new TenantActualFijo { TenantId = tenantOperador };

        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false, tenantActualPersonalizado: tenantActualDeTest);
        using var ambito = arnes.Servicios.CreateScope();

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = new ApplicationUser
        {
            UserName = "gestor-operador@caemanager.local",
            Email = "gestor-operador@caemanager.local",
            NombreCompleto = "Gestor del Operador",
            EmailConfirmed = true,
            TenantId = tenantOperador,
        };

        // Alta con sesión propia del operador — no es el caso que se prueba,
        // solo el punto de partida.
        (await userManager.CreateAsync(usuario, "Arnes#2026Seguro")).Succeeded.Should().BeTrue();

        // La misma cuenta, ahora modificada mientras la sesión "ve" el
        // tenant del WORKSPACE DELEGADO (beneficiario) — reproduce el
        // TenantIdSeleccionado real de una sesión de Operador Delegado.
        tenantActualDeTest.TenantId = tenantBeneficiario;
        usuario.PhoneNumber = "600111222";
        var resultado = await userManager.UpdateAsync(usuario);

        resultado.Succeeded.Should().BeTrue(
            "la actualización no debe fallar por RLS, sea cual sea el tenant que finalmente selle la fila — " +
            "errores: " + string.Join(", ", resultado.Errors.Select(e => e.Code + ":" + e.Description)));

        await ComprobarTenantRealDelRegistroAsync(arnes.CadenaPropietario, "Usuario", usuario.Id, tenantOperador);
    }

    private sealed class TenantActualFijo : ITenantActual
    {
        public Guid? TenantId { get; set; }
    }

    /// <summary>
    /// Consulta como PROPIETARIO (sin RLS) para confirmar que la fila
    /// realmente se escribió con el tenant correcto — que el alta no fallara
    /// no basta por sí solo: podría haber tenido éxito escribiendo un
    /// TenantId equivocado si <c>set_config</c> hubiera propagado el valor
    /// erróneo.
    /// </summary>
    private static async Task ComprobarTenantRealDelRegistroAsync(
        string cadenaPropietario, string entidadTipo, Guid entidadId, Guid tenantEsperado)
    {
        await using var conexion = new NpgsqlConnection(cadenaPropietario);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT ""TenantId"" FROM ""RegistrosAuditoria""
WHERE ""EntidadTipo"" = @entidadTipo AND ""EntidadId"" = @entidadId
ORDER BY ""FechaUtc"" DESC LIMIT 1;";
        comando.Parameters.AddWithValue("entidadTipo", entidadTipo);
        comando.Parameters.AddWithValue("entidadId", entidadId);

        var resultado = await comando.ExecuteScalarAsync();
        resultado.Should().NotBeNull("la fila debe existir — si el INSERT hubiera fallado, no habría llegado aquí");
        ((Guid)resultado!).Should().Be(tenantEsperado);
    }
}
