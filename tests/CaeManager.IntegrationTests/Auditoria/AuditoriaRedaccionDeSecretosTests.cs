using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Comunicaciones;
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
///
/// Hallazgo G-16 y decisión DEC-9 del propietario (2026-09-02): la denylist
/// tampoco cubría <see cref="Mensaje"/> ni <see cref="AdjuntoMensaje"/>, así
/// que cada alta de correo copiaba al rastro el cuerpo completo (hasta 1 MiB),
/// el remitente y el nombre del adjunto. A diferencia de los tokens, aquí no
/// se trata de un secreto cifrado en reposo sino de CONTENIDO: la auditoría
/// dice quién cambió qué y cuándo, y /auditoria la lee el rol Administrador,
/// que no es el rol con acceso a la bandeja de Comunicaciones.
/// </summary>
public class AuditoriaRedaccionDeSecretosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private const string RefreshTokenSecreto = "SECRETO-REFRESH-TOKEN-M365-no-debe-aparecer-en-claro";
    private const string ClientStateSecreto = "SECRETO-CLIENT-STATE-WEBHOOK-no-debe-aparecer-en-claro";
    private const string TokenAccesoSecreto = "SECRETO-TOKEN-WHATSAPP-no-debe-aparecer-en-claro";
    private const string AccessTokenSecreto = "SECRETO-ACCESS-TOKEN-GRAPH-no-debe-aparecer-en-claro";

    // Los tres marcadores de G-16/DEC-9 se escriben SIN caracteres que
    // System.Text.Json escape (menor que, mayor que, ampersand, apóstrofo,
    // más, no ASCII): si el marcador viajara escapado en el JSON, un
    // NotContain sobre el valor literal daría verde aunque el dato estuviera
    // en claro — el instrumento no podría observar lo que dice observar. Por
    // eso el cuerpo HTML se compone del marcador plano dentro de las
    // etiquetas, y las aserciones miran el marcador, no el HTML entero.
    private const string TextoSecretoDelCuerpo = "SECRETO-CUERPO-DEL-CORREO-no-debe-aparecer-en-claro";
    private const string CuerpoHtmlSecreto = $"<p>{TextoSecretoDelCuerpo}</p>";
    private const string RemitenteSecretoLocal = "secreto-remitente-no-debe-aparecer-en-claro";
    private const string RemitenteSecreto = $"{RemitenteSecretoLocal}@ejemplo.com";
    private const string NombreAdjuntoSecreto = "SECRETO-RECONOCIMIENTO-MEDICO-no-debe-aparecer-en-claro";

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

    /// <summary>
    /// G-16/DEC-9: el cuerpo del correo. Es el campo de mayor volumen de los
    /// tres — <see cref="Mensaje.LongitudMaximaCuerpoHtml"/> permite 1 MiB por
    /// mensaje, y el alta serializa la fila entera.
    /// </summary>
    [Fact]
    public async Task El_cuerpo_de_un_mensaje_no_aparece_en_claro_en_la_auditoria()
    {
        await SembrarMensajeConAdjuntoAsync();

        var registro = await ObtenerRegistroAsync(nameof(Mensaje));

        registro.DatosDespues.Should().NotContain(TextoSecretoDelCuerpo);
        registro.DatosDespues.Should().Contain("\"CuerpoHtml\":\"***\"");
    }

    /// <summary>
    /// G-16/DEC-9: el remitente. El propietario extendió aquí la
    /// recomendación —el argumento forense era débil en ambas direcciones—
    /// con el criterio de que también vive ya en la fila <c>Mensajes</c>.
    /// </summary>
    [Fact]
    public async Task El_remitente_de_un_mensaje_no_aparece_en_claro_en_la_auditoria()
    {
        await SembrarMensajeConAdjuntoAsync();

        var registro = await ObtenerRegistroAsync(nameof(Mensaje));

        registro.DatosDespues.Should().NotContain(RemitenteSecretoLocal);
        registro.DatosDespues.Should().Contain("\"Remitente\":\"***\"");
    }

    /// <summary>
    /// G-16/DEC-9: el nombre del adjunto — el mismo dato de categoría especial
    /// que G-3 acababa de retirar de los logs de ingesta. Limpiarlo del
    /// <c>ILogger</c> y dejarlo entrar por el rastro de auditoría sería una
    /// asimetría sin defensa.
    /// </summary>
    [Fact]
    public async Task El_nombre_del_adjunto_de_un_mensaje_no_aparece_en_claro_en_la_auditoria()
    {
        await SembrarMensajeConAdjuntoAsync();

        var registro = await ObtenerRegistroAsync(nameof(AdjuntoMensaje));

        registro.DatosDespues.Should().NotContain(NombreAdjuntoSecreto);
        registro.DatosDespues.Should().Contain("\"NombreArchivo\":\"***\"");
    }

    /// <summary>
    /// Mismo motivo que el test hermano de <c>DatosAntes</c> al rotar el
    /// refresh token: el alta solo ejercita <c>DatosDespues</c>, y la simetría
    /// del código no se da por sentada.
    ///
    /// La edición se fuerza por el ChangeTracker y no por un método de
    /// dominio porque <see cref="Mensaje"/> no expone hoy ningún mutador de
    /// <see cref="Mensaje.CuerpoHtml"/> — cambiar el modelo para poder
    /// probarlo queda fuera de DEC-9. El ChangeTracker es además exactamente
    /// la fuente que lee el interceptor, así que la prueba entra por el mismo
    /// camino que el dato real.
    /// </summary>
    [Fact]
    public async Task El_cuerpo_anterior_de_un_mensaje_tampoco_aparece_en_claro_en_DatosAntes_al_editarlo()
    {
        var mensajeId = await SembrarMensajeConAdjuntoAsync();

        await using (var contexto = CrearContexto())
        {
            var mensaje = await contexto.Mensajes.SingleAsync(m => m.Id == mensajeId);
            contexto.Entry(mensaje).Property(m => m.CuerpoHtml).CurrentValue = "<p>cuerpo corregido</p>";
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeModificacionAsync(nameof(Mensaje));

        registro.DatosAntes.Should().NotContain(TextoSecretoDelCuerpo);
        registro.DatosAntes.Should().Contain("\"CuerpoHtml\":\"***\"");
    }

    /// <summary>
    /// Un alta de correo real: la conversación arrastra el mensaje y el
    /// mensaje su adjunto en un único <c>SaveChanges</c>, que es como los
    /// escribe la ingesta de webhook. Devuelve el Id del mensaje.
    /// </summary>
    private async Task<Guid> SembrarMensajeConAdjuntoAsync()
    {
        await using var contexto = CrearContexto();

        var conversacion = new Conversacion("Documentación recibida en el buzón");
        var mensaje = conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, RemitenteSecreto, CuerpoHtmlSecreto);
        mensaje.AgregarAdjunto(NombreAdjuntoSecreto, "application/pdf", 2048, "blob/adjunto-de-prueba");

        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        return mensaje.Id;
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
