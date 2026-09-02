using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// Módulo 8 de la auditoría 2026-08-30: <see cref="AuditoriaInterceptor"/>
/// tenía una denylist de campos sensibles que no incluía
/// <see cref="CredencialIntegracion.RefreshToken"/>,
/// <see cref="SuscripcionWebhook.ClientState"/> ni
/// <see cref="LineaWhatsApp.TokenAcceso"/> — los tres se cifran con
/// ValueConverter al llegar a PostgreSQL, pero el interceptor lee el valor
/// plano directamente del ChangeTracker antes de ese cifrado, así que
/// quedaban en texto plano dentro de RegistroAuditoria.DatosDespues. Este
/// test es la prueba de sensibilidad que habría detectado el hueco original
/// y que debe seguir en rojo si alguien quita una entrada de la denylist.
///
/// Sesión nocturna 2026-09-02, ítem G-1: la denylist enmascaraba
/// <see cref="CredencialIntegracion.RefreshToken"/> pero no
/// <see cref="CredencialIntegracion.AccessToken"/>. Desde el PR #374,
/// <c>AccesoGraphService.ObtenerAccessTokenVigenteAsync</c> llama a
/// <c>CredencialIntegracion.ActualizarAccessTokenCacheado</c> en cada
/// refresco, así que un token de Graph válido (~1h) quedaba en claro en
/// <c>RegistroAuditoria.DatosDespues</c> cada vez que se cacheaba.
/// </summary>
public class AuditoriaRedaccionDeSecretosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private const string RefreshTokenSecreto = "SECRETO-REFRESH-TOKEN-M365-no-debe-aparecer-en-claro";
    private const string ClientStateSecreto = "SECRETO-CLIENT-STATE-WEBHOOK-no-debe-aparecer-en-claro";
    private const string TokenAccesoSecreto = "SECRETO-TOKEN-WHATSAPP-no-debe-aparecer-en-claro";
    private const string AccessTokenSecreto = "SECRETO-ACCESS-TOKEN-GRAPH-no-debe-aparecer-en-claro";

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_refresh_token_de_una_conexion_de_correo_no_aparece_en_claro_en_la_auditoria()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.CredencialesIntegracion.Add(
                new CredencialIntegracion(Guid.NewGuid(), RefreshTokenSecreto));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync(nameof(CredencialIntegracion));

        registro.DatosDespues.Should().NotContain(RefreshTokenSecreto);
        registro.DatosDespues.Should().Contain("\"RefreshToken\":\"***\"");
    }

    /// <summary>
    /// Revisión con Codex del cierre de G-1: el test anterior solo comprueba
    /// <c>DatosDespues</c> en un alta (<c>Added</c>), no <c>DatosAntes</c> en
    /// una modificación. El código de enmascarado es el mismo camino
    /// (<c>SerializarValores</c> aplica la misma denylist a los dos), pero se
    /// deja verificado explícitamente en vez de darlo por sentado por
    /// simetría del código.
    /// </summary>
    [Fact]
    public async Task El_refresh_token_anterior_tampoco_aparece_en_claro_en_DatosAntes_al_rotarlo()
    {
        const string refreshTokenAnterior = "SECRETO-REFRESH-TOKEN-ANTERIOR-no-debe-aparecer-en-claro";
        Guid credencialId;
        await using (var contexto = CrearContexto())
        {
            var credencial = new CredencialIntegracion(Guid.NewGuid(), refreshTokenAnterior);
            credencialId = credencial.Id;
            contexto.CredencialesIntegracion.Add(credencial);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var credencial = await contexto.CredencialesIntegracion.SingleAsync(c => c.Id == credencialId);
            credencial.ActualizarRefreshToken("nuevo-refresh-token-tras-rotar");
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeModificacionAsync(nameof(CredencialIntegracion));

        registro.DatosAntes.Should().NotContain(refreshTokenAnterior);
        registro.DatosAntes.Should().Contain("\"RefreshToken\":\"***\"");
    }

    [Fact]
    public async Task El_access_token_cacheado_de_una_conexion_de_correo_no_aparece_en_claro_en_la_auditoria()
    {
        Guid credencialId;
        await using (var contexto = CrearContexto())
        {
            var credencial = new CredencialIntegracion(Guid.NewGuid(), RefreshTokenSecreto);
            credencialId = credencial.Id;
            contexto.CredencialesIntegracion.Add(credencial);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var credencial = await contexto.CredencialesIntegracion.SingleAsync(c => c.Id == credencialId);
            credencial.ActualizarAccessTokenCacheado(AccessTokenSecreto, DateTime.UtcNow.AddHours(1));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeModificacionAsync(nameof(CredencialIntegracion));

        registro.DatosDespues.Should().NotContain(AccessTokenSecreto);
        registro.DatosDespues.Should().Contain("\"AccessToken\":\"***\"");
    }

    [Fact]
    public async Task El_client_state_de_una_suscripcion_de_webhook_no_aparece_en_claro_en_la_auditoria()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.SuscripcionesWebhook.Add(new SuscripcionWebhook(
                Guid.NewGuid(), "graph-subscription-id", ClientStateSecreto, DateTime.UtcNow.AddDays(3)));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync(nameof(SuscripcionWebhook));

        registro.DatosDespues.Should().NotContain(ClientStateSecreto);
        registro.DatosDespues.Should().Contain("\"ClientState\":\"***\"");
    }

    [Fact]
    public async Task El_token_de_acceso_de_una_linea_de_whatsapp_no_aparece_en_claro_en_la_auditoria()
    {
        await using (var contexto = CrearContexto())
        {
            var conexion = new ConexionIntegracion("+34600000000", "Línea de pruebas", proveedor: ProveedorIntegracion.WhatsApp);
            contexto.Add(conexion);

            contexto.Add(new LineaWhatsApp(
                conexion.Id, "phone-number-id", "waba-id", "+34600000000", TokenAccesoSecreto,
                ModoAsignacionLinea.PoolInbound));

            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync(nameof(LineaWhatsApp));

        registro.DatosDespues.Should().NotContain(TokenAccesoSecreto);
        registro.DatosDespues.Should().Contain("\"TokenAcceso\":\"***\"");
    }

    [Fact]
    public async Task Actualizar_un_solo_campo_no_duplica_el_resto_de_la_entidad_en_Antes_y_Despues()
    {
        Guid credencialId;
        await using (var contexto = CrearContexto())
        {
            var credencial = new CredencialIntegracion(Guid.NewGuid(), RefreshTokenSecreto);
            credencialId = credencial.Id;
            contexto.CredencialesIntegracion.Add(credencial);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var credencial = await contexto.CredencialesIntegracion.SingleAsync(c => c.Id == credencialId);
            credencial.ActualizarRefreshToken("otro-token-de-refresh");
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeModificacionAsync(nameof(CredencialIntegracion));

        // Solo RefreshToken cambió — TenantId/ConexionIntegracionId/Version no
        // deben aparecer como si también hubiesen sido parte de la edición.
        registro.DatosDespues.Should().Contain("RefreshToken");
        registro.DatosDespues.Should().NotContain("ConexionIntegracionId");
        registro.DatosAntes.Should().NotContain("ConexionIntegracionId");
    }

    private async Task<RegistroAuditoria> ObtenerRegistroAsync(string entidadTipo)
    {
        await using var contexto = CrearContexto();

        return await contexto.RegistrosAuditoria
            .Where(r => r.EntidadTipo == entidadTipo)
            .OrderByDescending(r => r.FechaUtc)
            .FirstAsync();
    }

    private async Task<RegistroAuditoria> ObtenerRegistroDeModificacionAsync(string entidadTipo)
    {
        await using var contexto = CrearContexto();

        return await contexto.RegistrosAuditoria
            .Where(r => r.EntidadTipo == entidadTipo && r.Accion == "Modificado")
            .OrderByDescending(r => r.FechaUtc)
            .FirstAsync();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var actor = new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid()));
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new AuditoriaInterceptor(actor), new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
