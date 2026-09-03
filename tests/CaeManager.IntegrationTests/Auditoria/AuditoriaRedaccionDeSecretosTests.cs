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
///
/// REC-111/REC-112, DEC-37/DEC-38 del propietario (2026-09-02): DEC-9 se
/// extiende en dos direcciones más. DEC-37 — el mismo PII del remitente
/// también aparece en entidades hermanas del mensaje:
/// <see cref="ParticipanteConversacion.Email"/> y
/// <see cref="ContactoWhatsApp.Telefono"/>/<see cref="ContactoWhatsApp.Nombre"/>.
/// No hay excepción por vivir en una fila distinta. DEC-38 — un resumen
/// generado por IA a partir del correo (<see cref="ClasificacionRelevanciaCae.Resumen"/>,
/// <see cref="SugerenciaGestionCorreo.Resumen"/>,
/// <see cref="SugerenciaVisitaCorreo.Resumen"/>) tampoco es menos sensible que
/// el contenido fuente por ser derivado: sería una vía indirecta de
/// reconstruirlo.
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

    // REC-111/REC-112, mismo motivo que los marcadores de arriba: ASCII sin
    // <, >, &, ', + para que System.Text.Json no los escape y el NotContain
    // observe de verdad si el dato quedó en claro.
    private const string EmailParticipanteSecretoLocal = "secreto-email-participante-no-debe-aparecer-en-claro";
    private const string EmailParticipanteSecreto = $"{EmailParticipanteSecretoLocal}@ejemplo.com";
    // Sin "+" a propósito, aunque ContactoWhatsApp.Telefono documente E.164
    // con "+": System.Text.Json también escapa el "+" (+), así que un
    // NotContain contra el literal con "+" nunca podría fallar, enmascarado o
    // no — el mismo instrumento-ciego que motiva este comentario.
    private const string TelefonoContactoSecreto = "0034600111222";
    private const string NombreContactoSecreto = "SECRETO-NOMBRE-PERFIL-WHATSAPP-no-debe-aparecer-en-claro";
    private const string ResumenClasificacionSecreto = "SECRETO-RESUMEN-CLASIFICACION-RELEVANCIA-no-debe-aparecer-en-claro";
    private const string ResumenSugerenciaGestionSecreto = "SECRETO-RESUMEN-SUGERENCIA-GESTION-no-debe-aparecer-en-claro";
    private const string ResumenSugerenciaVisitaSecreto = "SECRETO-RESUMEN-SUGERENCIA-VISITA-no-debe-aparecer-en-claro";

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
    /// DEC-37: el email del participante identifica a una persona igual que
    /// <see cref="Mensaje.Remitente"/>, aunque viva en su propia fila —
    /// <see cref="ParticipanteConversacion"/> no es una excepción al criterio
    /// de DEC-9 solo por ser una entidad hermana del mensaje.
    /// </summary>
    [Fact]
    public async Task El_email_de_un_participante_de_conversacion_no_aparece_en_claro_en_la_auditoria()
    {
        await using (var contexto = CrearContexto())
        {
            var conversacion = new Conversacion("Copia al participante");
            conversacion.AgregarParticipante(EmailParticipanteSecreto, RolParticipante.Cc, TipoParticipanteOrigen.Desconocido);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync(nameof(ParticipanteConversacion));

        registro.DatosDespues.Should().NotContain(EmailParticipanteSecretoLocal);
        registro.DatosDespues.Should().Contain("\"Email\":\"***\"");
    }

    /// <summary>
    /// Mismo motivo que los tests hermanos de <c>DatosAntes</c>: el alta solo
    /// ejercita <c>DatosDespues</c>. <see cref="ParticipanteConversacion"/> no
    /// expone ningún mutador de <see cref="ParticipanteConversacion.Email"/> —
    /// se fuerza por el ChangeTracker, igual que <see cref="Mensaje.CuerpoHtml"/>.
    /// La FK compuesta (ConversacionId, TenantId) exige un ancla real —
    /// mismo motivo por el que los tests de Mensaje siembran una Conversacion.
    /// </summary>
    [Fact]
    public async Task El_email_anterior_de_un_participante_tampoco_aparece_en_claro_en_DatosAntes_al_editarlo()
    {
        Guid participanteId;
        await using (var contexto = CrearContexto())
        {
            var conversacion = new Conversacion("Copia al participante");
            var participante = conversacion.AgregarParticipante(EmailParticipanteSecreto, RolParticipante.Cc, TipoParticipanteOrigen.Desconocido);
            participanteId = participante.Id;
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var participante = await contexto.ParticipantesConversacion.SingleAsync(p => p.Id == participanteId);
            contexto.Entry(participante).Property(p => p.Email).CurrentValue = "corregido@ejemplo.com";
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeModificacionAsync(nameof(ParticipanteConversacion));

        registro.DatosAntes.Should().NotContain(EmailParticipanteSecretoLocal);
        registro.DatosAntes.Should().Contain("\"Email\":\"***\"");
    }

    /// <summary>
    /// DEC-37: teléfono y nombre de perfil de <see cref="ContactoWhatsApp"/>
    /// identifican a la misma persona que <see cref="Mensaje.Remitente"/> en
    /// un correo — viven en el catálogo de enrutamiento, no en el mensaje,
    /// y eso no los exime del enmascarado.
    /// </summary>
    [Fact]
    public async Task El_telefono_y_el_nombre_de_un_contacto_de_whatsapp_no_aparecen_en_claro_en_la_auditoria()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.ContactosWhatsApp.Add(new ContactoWhatsApp(
                TelefonoContactoSecreto, Guid.NewGuid(), NombreContactoSecreto));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync(nameof(ContactoWhatsApp));

        registro.DatosDespues.Should().NotContain(TelefonoContactoSecreto);
        registro.DatosDespues.Should().NotContain(NombreContactoSecreto);
        registro.DatosDespues.Should().Contain("\"Telefono\":\"***\"");
        registro.DatosDespues.Should().Contain("\"Nombre\":\"***\"");
    }

    /// <summary>
    /// Mismo motivo que el resto de tests hermanos de <c>DatosAntes</c>.
    /// <see cref="ContactoWhatsApp"/> no expone ningún mutador de
    /// <see cref="ContactoWhatsApp.Telefono"/> (se fija solo en el
    /// constructor) — se fuerza por el ChangeTracker igual que el email de
    /// participante; <see cref="ContactoWhatsApp.ActualizarNombre"/> sí
    /// existe, pero se cambia también por ChangeTracker para que ambas
    /// propiedades queden <c>IsModified</c> en el mismo <c>SaveChanges</c> y
    /// el test cubra las dos con una sola edición.
    /// </summary>
    [Fact]
    public async Task El_telefono_y_el_nombre_anteriores_de_un_contacto_tampoco_aparecen_en_claro_en_DatosAntes_al_editarlo()
    {
        Guid contactoId;
        await using (var contexto = CrearContexto())
        {
            var contacto = new ContactoWhatsApp(TelefonoContactoSecreto, Guid.NewGuid(), NombreContactoSecreto);
            contactoId = contacto.Id;
            contexto.ContactosWhatsApp.Add(contacto);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var contacto = await contexto.ContactosWhatsApp.SingleAsync(c => c.Id == contactoId);
            contexto.Entry(contacto).Property(c => c.Telefono).CurrentValue = "+34600111333";
            contexto.Entry(contacto).Property(c => c.Nombre).CurrentValue = "Nombre corregido";
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeModificacionAsync(nameof(ContactoWhatsApp));

        registro.DatosAntes.Should().NotContain(TelefonoContactoSecreto);
        registro.DatosAntes.Should().NotContain(NombreContactoSecreto);
        registro.DatosAntes.Should().Contain("\"Telefono\":\"***\"");
        registro.DatosAntes.Should().Contain("\"Nombre\":\"***\"");
    }

    /// <summary>
    /// DEC-38: el resumen de <see cref="ClasificacionRelevanciaCae"/> es
    /// contenido derivado por IA a partir del cuerpo del correo — no es
    /// "menos sensible que el contenido fuente" solo por ser un resumen.
    /// </summary>
    [Fact]
    public async Task El_resumen_de_una_clasificacion_de_relevancia_cae_no_aparece_en_claro_en_la_auditoria()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.ClasificacionesRelevanciaCae.Add(new ClasificacionRelevanciaCae(
                Guid.NewGuid(), esAccionableCae: true, ResumenClasificacionSecreto, confianza: 90));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync(nameof(ClasificacionRelevanciaCae));

        registro.DatosDespues.Should().NotContain(ResumenClasificacionSecreto);
        registro.DatosDespues.Should().Contain("\"Resumen\":\"***\"");
    }

    /// <summary>
    /// Mismo motivo que el resto de tests hermanos de <c>DatosAntes</c>, pero
    /// aquí sí existe un mutador de dominio real —
    /// <see cref="ClasificacionRelevanciaCae.Actualizar"/> — y se usa en vez
    /// de forzar el ChangeTracker, porque el camino real (re-evaluación de la
    /// IA en <c>RelevanciaCaeService</c>) pasa por él.
    /// </summary>
    [Fact]
    public async Task El_resumen_anterior_de_una_clasificacion_tampoco_aparece_en_claro_en_DatosAntes_al_editarla()
    {
        Guid clasificacionId;
        await using (var contexto = CrearContexto())
        {
            var clasificacion = new ClasificacionRelevanciaCae(
                Guid.NewGuid(), esAccionableCae: false, ResumenClasificacionSecreto, confianza: 60);
            clasificacionId = clasificacion.Id;
            contexto.ClasificacionesRelevanciaCae.Add(clasificacion);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var clasificacion = await contexto.ClasificacionesRelevanciaCae.SingleAsync(c => c.Id == clasificacionId);
            clasificacion.Actualizar(esAccionableCae: true, "Resumen corregido tras nuevo mensaje.", confianza: 95);
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeModificacionAsync(nameof(ClasificacionRelevanciaCae));

        registro.DatosAntes.Should().NotContain(ResumenClasificacionSecreto);
        registro.DatosAntes.Should().Contain("\"Resumen\":\"***\"");
    }

    /// <summary>
    /// DEC-38: el resumen de <see cref="SugerenciaGestionCorreo"/> es la
    /// lectura de la IA sobre el cuerpo del mensaje que la originó — mismo
    /// criterio que <see cref="ClasificacionRelevanciaCae.Resumen"/>.
    /// </summary>
    [Fact]
    public async Task El_resumen_de_una_sugerencia_de_gestion_de_correo_no_aparece_en_claro_en_la_auditoria()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.SugerenciasGestionCorreo.Add(new SugerenciaGestionCorreo(
                Guid.NewGuid(), ResumenSugerenciaGestionSecreto, confianza: 85));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync(nameof(SugerenciaGestionCorreo));

        registro.DatosDespues.Should().NotContain(ResumenSugerenciaGestionSecreto);
        registro.DatosDespues.Should().Contain("\"Resumen\":\"***\"");
    }

    /// <summary>
    /// Mismo motivo que el resto de tests hermanos de <c>DatosAntes</c>.
    /// <see cref="SugerenciaGestionCorreo"/> no expone ningún mutador de
    /// <see cref="SugerenciaGestionCorreo.Resumen"/> (el constructor lo fija
    /// y <see cref="SugerenciaGestionCorreo.AgregarDetalle"/> no lo toca) —
    /// se fuerza por el ChangeTracker.
    /// </summary>
    [Fact]
    public async Task El_resumen_anterior_de_una_sugerencia_de_gestion_tampoco_aparece_en_claro_en_DatosAntes_al_editarla()
    {
        Guid sugerenciaId;
        await using (var contexto = CrearContexto())
        {
            var sugerencia = new SugerenciaGestionCorreo(Guid.NewGuid(), ResumenSugerenciaGestionSecreto, confianza: 85);
            sugerenciaId = sugerencia.Id;
            contexto.SugerenciasGestionCorreo.Add(sugerencia);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var sugerencia = await contexto.SugerenciasGestionCorreo.SingleAsync(s => s.Id == sugerenciaId);
            contexto.Entry(sugerencia).Property(s => s.Resumen).CurrentValue = "Resumen corregido.";
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeModificacionAsync(nameof(SugerenciaGestionCorreo));

        registro.DatosAntes.Should().NotContain(ResumenSugerenciaGestionSecreto);
        registro.DatosAntes.Should().Contain("\"Resumen\":\"***\"");
    }

    /// <summary>
    /// DEC-38: el resumen de <see cref="SugerenciaVisitaCorreo"/> es la misma
    /// clase de contenido derivado por IA que <see cref="SugerenciaGestionCorreo.Resumen"/>.
    /// </summary>
    [Fact]
    public async Task El_resumen_de_una_sugerencia_de_visita_de_correo_no_aparece_en_claro_en_la_auditoria()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.SugerenciasVisitaCorreo.Add(new SugerenciaVisitaCorreo(
                Guid.NewGuid(), null, null, null, ResumenSugerenciaVisitaSecreto, 90, 0, 0));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync(nameof(SugerenciaVisitaCorreo));

        registro.DatosDespues.Should().NotContain(ResumenSugerenciaVisitaSecreto);
        registro.DatosDespues.Should().Contain("\"Resumen\":\"***\"");
    }

    /// <summary>
    /// Mismo motivo que el resto de tests hermanos de <c>DatosAntes</c>.
    /// <see cref="SugerenciaVisitaCorreo"/> no expone ningún mutador de
    /// <see cref="SugerenciaVisitaCorreo.Resumen"/> (<see cref="SugerenciaVisitaCorreo.Resolver"/>
    /// no lo toca) — se fuerza por el ChangeTracker.
    /// </summary>
    [Fact]
    public async Task El_resumen_anterior_de_una_sugerencia_de_visita_tampoco_aparece_en_claro_en_DatosAntes_al_editarla()
    {
        Guid sugerenciaId;
        await using (var contexto = CrearContexto())
        {
            var sugerencia = new SugerenciaVisitaCorreo(
                Guid.NewGuid(), null, null, null, ResumenSugerenciaVisitaSecreto, 90, 0, 0);
            sugerenciaId = sugerencia.Id;
            contexto.SugerenciasVisitaCorreo.Add(sugerencia);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var sugerencia = await contexto.SugerenciasVisitaCorreo.SingleAsync(s => s.Id == sugerenciaId);
            contexto.Entry(sugerencia).Property(s => s.Resumen).CurrentValue = "Resumen corregido.";
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeModificacionAsync(nameof(SugerenciaVisitaCorreo));

        registro.DatosAntes.Should().NotContain(ResumenSugerenciaVisitaSecreto);
        registro.DatosAntes.Should().Contain("\"Resumen\":\"***\"");
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
