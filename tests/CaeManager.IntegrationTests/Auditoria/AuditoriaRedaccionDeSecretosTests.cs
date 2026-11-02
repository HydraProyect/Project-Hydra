using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Trabajadores;
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

    // Lo que un Gestor CAE teclea en el buscador de un listado y viaja en los
    // valores de un filtro guardado: un DNI inventado, con forma de DNI.
    private const string DniBuscado = "00000000T";
    private const string ValoresDeFiltroConDni = $$"""{"q":"{{DniBuscado}}","estado":"Vencido"}""";
    private const string EstadoDeLaVistaNueva = "ESTADO-DE-LA-VISTA-NUEVA-no-debe-aparecer-en-claro";
    private const string ValoresDeVistaNueva = $$"""{"estado":"{{EstadoDeLaVistaNueva}}"}""";

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
    public async Task El_codigo_seguro_de_un_documento_generado_no_aparece_en_claro_en_la_auditoria()
    {
        const string codigoSeguroSecreto = "CODIGO-SEGURO-no-debe-aparecer-en-claro";

        await using (var contexto = CrearContexto())
        {
            var tipo = new TipoDocumento("Ficha de prueba", 12, true, 1, AmbitoAplicacion.Empresa);
            var empresa = new Empresa("Empresa de prueba S.L.", "B10380194");
            var plantilla = new PlantillaDocumento(
                OrigenPlantilla.Externa, "Plantilla de prueba", AmbitoAplicacion.Empresa, FormatoOrigenPlantilla.PdfVisual, tipo.Id);
            var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "plantilla-de-prueba.pdf", new string('a', 64));
            contexto.AddRange(tipo, empresa, plantilla, version);
            await contexto.SaveChangesAsync();

            var documento = Documento.DeEmpresa(empresa.Id, tipo.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca);
            var generado = new DocumentoGenerado(
                version.Id, documento.Id, "{}", Guid.NewGuid(), DateTime.UtcNow, empresaId: empresa.Id);
            contexto.AddRange(documento, generado);
            // Ninguna generación rellena el código todavía: se escribe como lo hará la primera
            // plantilla de origen TalvegPropio.
            contexto.Entry(generado).Property(d => d.CodigoSeguroVerificacion).CurrentValue = codigoSeguroSecreto;
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync(nameof(DocumentoGenerado));

        registro.DatosDespues.Should().NotContain(codigoSeguroSecreto);
        registro.DatosDespues.Should().Contain("\"CodigoSeguroVerificacion\":\"***\"");
        registro.DatosDespues.Should().Contain("\"DatosUtilizadosJson\":\"***\"");
    }

    [Fact]
    public async Task Al_crearse_el_codigo_de_verificacion_y_el_payload_no_se_copian_en_la_auditoria()
    {
        const string codigo = "CODIGO-SECRETO-VERIFICACION";
        const string payload = "PAYLOAD-SECRETO-WEBHOOK";
        await using (var contexto = CrearContexto())
        {
            var tipo = new TipoDocumento("Ficha de prueba", 12, true, 1, AmbitoAplicacion.Empresa);
            var empresa = new Empresa("Empresa de prueba S.L.", "B10380194");
            var documento = Documento.DeEmpresa(empresa.Id, tipo.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca);
            var conexion = new ConexionIntegracion("+34600000000", "Linea de pruebas", proveedor: ProveedorIntegracion.WhatsApp);
            contexto.AddRange(tipo, empresa, documento, conexion);
            contexto.Add(new VerificacionDocumentoOficial(documento.Id, PerfilDocumentoOficial.CorrienteTgss,
                NivelConfianzaDocumental.FirmaValidaSinRevocacion, codigo, null, null, null, null,
                ResultadoCotejoDocumentoOficial.Coincide, DecisionValidacionOficial.AutoValidado, ""));
            contexto.Add(new EventoWebhook(conexion.Id, payload));
            await contexto.SaveChangesAsync();
        }

        var verificacion = await ObtenerRegistroAsync(nameof(VerificacionDocumentoOficial));
        var evento = await ObtenerRegistroAsync(nameof(EventoWebhook));
        verificacion.DatosDespues.Should().NotContain(codigo).And.Contain("\"CodigoVerificacion\":\"***\"");
        evento.DatosDespues.Should().NotContain(payload).And.Contain("\"PayloadCrudo\":\"***\"");
    }

    [Fact]
    public async Task Los_json_sensibles_no_aparecen_en_claro_en_la_auditoria()
    {
        const string contextoSecreto = "{\"secreto\":\"CONTEXTO-no-debe-aparecer-en-claro\"}";
        const string extraccionSecreta = "{\"secreto\":\"EXTRACCION-no-debe-aparecer-en-claro\"}";

        await using (var contexto = CrearContexto())
        {
            var tipo = new TipoDocumento("Ficha de prueba", 12, true, 1, AmbitoAplicacion.Empresa);
            var plantilla = new PlantillaDocumento(
                OrigenPlantilla.Externa, "Plantilla de prueba", AmbitoAplicacion.Empresa, FormatoOrigenPlantilla.PdfVisual, tipo.Id);
            var version = new PlantillaDocumentoVersion(plantilla.Id, 1, "plantilla-de-prueba.pdf", new string('a', 64));
            contexto.AddRange(tipo, plantilla, version);
            await contexto.SaveChangesAsync();

            contexto.Add(new LoteGeneracionDocumento(version.Id, 1, Guid.NewGuid(), DateTime.UtcNow, contextoSecreto));
            contexto.Add(ExtraccionIaCache.Crear(new string('b', 64), "Ficha", extraccionSecreta));
            await contexto.SaveChangesAsync();
        }

        var lote = await ObtenerRegistroAsync(nameof(LoteGeneracionDocumento));
        var extraccion = await ObtenerRegistroAsync(nameof(ExtraccionIaCache));

        lote.DatosDespues.Should().NotContain("CONTEXTO-no-debe-aparecer-en-claro");
        lote.DatosDespues.Should().Contain("\"ContextoJson\":\"***\"");
        extraccion.DatosDespues.Should().NotContain("EXTRACCION-no-debe-aparecer-en-claro");
        extraccion.DatosDespues.Should().Contain("\"ExtraccionJson\":\"***\"");
    }

    [Fact]
    public async Task La_solicitud_de_conexion_a_Microsoft_365_no_deja_sus_datos_en_claro_en_la_auditoria()
    {
        var usuarioSolicitante = Guid.NewGuid();

        await using (var contexto = CrearContexto())
        {
            contexto.Add(new SolicitudConexionMicrosoft365(usuarioSolicitante, clienteId: null, gestorPropietarioId: null, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroAsync(nameof(SolicitudConexionMicrosoft365));

        registro.DatosDespues.Should().NotContain(usuarioSolicitante.ToString());
        registro.DatosDespues.Should().Contain("\"UsuarioSolicitanteId\":\"***\"");
        registro.DatosDespues.Should().Contain("\"FechaExpiracionUtc\":\"***\"");
    }

    [Fact]
    public async Task Una_sesion_de_usuario_no_deja_su_identificador_en_la_auditoria()
    {
        var sid = Guid.NewGuid();

        await using (var contexto = CrearContexto())
        {
            // La Empresa del mismo guardado es el control del instrumento: ese guardado sí audita.
            contexto.Add(new Empresa("Empresa de prueba S.L.", "B10380194"));
            contexto.SesionesUsuario.Add(new CaeManager.Domain.Seguridad.SesionUsuario(Guid.NewGuid(), sid, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await ObtenerRegistroAsync(nameof(Empresa));
        await using var lectura = CrearContexto();
        var registros = await lectura.RegistrosAuditoria.ToListAsync();

        registros.Should().NotContain(r => r.EntidadTipo == nameof(CaeManager.Domain.Seguridad.SesionUsuario));
        registros.Should().NotContain(r => r.DatosDespues != null && r.DatosDespues.Contains(sid.ToString()));
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

    /// <summary>El motivo de rechazo es texto libre sobre un adjunto y puede nombrar a una persona: mismo criterio que su nombre.</summary>
    [Fact]
    public async Task El_motivo_de_rechazo_de_un_adjunto_no_aparece_en_claro_en_la_auditoria()
    {
        const string motivoSecreto = "El reconocimiento es de otra persona";
        var mensajeId = await SembrarMensajeConAdjuntoAsync();

        await using (var contexto = CrearContexto())
        {
            var adjunto = await contexto.AdjuntosMensaje.SingleAsync(a => a.MensajeId == mensajeId);
            adjunto.Rechazar(motivoSecreto, Guid.NewGuid(), DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeModificacionAsync(nameof(AdjuntoMensaje));

        registro.DatosDespues.Should().NotContain(motivoSecreto);
        registro.DatosDespues.Should().Contain("\"MotivoRechazo\":\"***\"");
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
    /// Los valores de un filtro guardado llevan la búsqueda libre del listado:
    /// quien busca a un Trabajador por su DNI y guarda el filtro no deja ese DNI
    /// en la auditoría, ni al guardarlo ni al borrarlo (la baja registra la fila
    /// entera en <c>DatosAntes</c>).
    /// </summary>
    [Fact]
    public async Task Los_valores_de_un_filtro_guardado_no_aparecen_en_claro_en_la_auditoria_ni_al_guardarlo_ni_al_borrarlo()
    {
        Guid filtroId;
        await using (var contexto = CrearContexto())
        {
            var filtro = new FiltroGuardado(Guid.NewGuid(), "Trabajadores", "Pendientes de Marta", ValoresDeFiltroConDni);
            filtroId = filtro.Id;
            contexto.FiltrosGuardados.Add(filtro);
            await contexto.SaveChangesAsync();
        }

        var alta = await ObtenerRegistroAsync(nameof(FiltroGuardado));
        alta.DatosDespues.Should().NotContain(DniBuscado);
        alta.DatosDespues.Should().Contain("\"ValoresJson\":\"***\"");
        alta.DatosDespues.Should().Contain("Pendientes de Marta", "control: el resto de la fila sí se audita");

        await using (var contexto = CrearContexto())
        {
            contexto.FiltrosGuardados.Remove(await contexto.FiltrosGuardados.SingleAsync(f => f.Id == filtroId));
            await contexto.SaveChangesAsync();
        }

        var baja = await ObtenerRegistroAsync(nameof(FiltroGuardado));
        baja.Accion.Should().NotBe(alta.Accion, "control: el registro leído es el de la baja");
        baja.DatosAntes.Should().NotContain(DniBuscado);
        baja.DatosAntes.Should().Contain("\"ValoresJson\":\"***\"");
    }

    /// <summary>
    /// La vista recordada es la misma entidad con nombre reservado y se reescribe
    /// en cada cambio de vista: ni la vista nueva ni la anterior quedan copiadas.
    /// Es el único camino de <c>Modificado</c> de esta entidad.
    /// </summary>
    [Fact]
    public async Task La_vista_recordada_no_aparece_en_claro_en_la_auditoria_ni_antes_ni_despues_de_cambiarla()
    {
        Guid vistaId;
        await using (var contexto = CrearContexto())
        {
            var vista = FiltroGuardado.CrearVistaRecordada(Guid.NewGuid(), "Trabajadores", ValoresDeFiltroConDni);
            vistaId = vista.Id;
            contexto.FiltrosGuardados.Add(vista);
            await contexto.SaveChangesAsync();
        }

        (await ObtenerRegistroAsync(nameof(FiltroGuardado))).DatosDespues.Should().NotContain(DniBuscado)
            .And.Contain("\"ValoresJson\":\"***\"");

        await using (var contexto = CrearContexto())
        {
            var vista = await contexto.FiltrosGuardados.SingleAsync(f => f.Id == vistaId);
            vista.RecordarVista(ValoresDeVistaNueva);
            await contexto.SaveChangesAsync();
        }

        var cambio = await ObtenerRegistroDeModificacionAsync(nameof(FiltroGuardado));
        cambio.DatosAntes.Should().NotContain(DniBuscado);
        cambio.DatosAntes.Should().Contain("\"ValoresJson\":\"***\"");
        cambio.DatosDespues.Should().NotContain(EstadoDeLaVistaNueva);
        cambio.DatosDespues.Should().Contain("\"ValoresJson\":\"***\"");
    }

    /// <summary>
    /// Punto 6 del interceptor: el número de afiliación a la Seguridad Social
    /// y la nacionalidad de la ficha de Trabajador 360 no quedan en claro ni
    /// al editarlos (DatosDespues) ni al anonimizar al Trabajador (DatosAntes
    /// de la supresión): el historial no tiene purga y conservaría el dato
    /// que Anonimizar acaba de vaciar. Marcadores ASCII sin caracteres que
    /// System.Text.Json escape, por el mismo motivo que los de arriba.
    /// </summary>
    [Fact]
    public async Task El_numero_de_afiliacion_de_un_trabajador_no_aparece_en_claro_ni_al_editarlo_ni_al_anonimizarlo()
    {
        const string afiliacion = "281234567840";
        const string nacionalidad = "NACIONALIDAD-no-debe-aparecer-en-claro";
        Guid trabajadorId;
        await using (var contexto = CrearContexto())
        {
            var empresa = new Empresa("Empresa de la ficha", "B10380194");
            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Nora", "Vidal", "12345678Z");
            trabajadorId = trabajador.Id;
            contexto.Empresas.Add(empresa);
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var trabajador = await contexto.Trabajadores.SingleAsync(t => t.Id == trabajadorId);
            trabajador.ActualizarFicha(afiliacion, null, null, nacionalidad, null, null, null, null, null, null, null);
            await contexto.SaveChangesAsync();
        }

        var edicion = await ObtenerRegistroDeModificacionAsync(nameof(Trabajador));
        edicion.DatosDespues.Should().NotContain(afiliacion);
        edicion.DatosDespues.Should().NotContain(nacionalidad);
        edicion.DatosDespues.Should().Contain("\"NumeroAfiliacionSeguridadSocial\":\"***\"");

        await using (var contexto = CrearContexto())
        {
            var trabajador = await contexto.Trabajadores.SingleAsync(t => t.Id == trabajadorId);
            trabajador.Anonimizar(DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var supresion = await ObtenerRegistroDeModificacionAsync(nameof(Trabajador));
        supresion.Id.Should().NotBe(edicion.Id, "la supresión deja su propia fila de modificación");
        supresion.DatosAntes.Should().NotContain(afiliacion);
        supresion.DatosAntes.Should().NotContain(nacionalidad);
        supresion.DatosAntes.Should().Contain("\"NumeroAfiliacionSeguridadSocial\":\"***\"");
    }

    /// <summary>
    /// Punto 8 del interceptor: quien firma y los nombres, teléfonos y correos
    /// de los contactos de la ficha de Empresa 360 no quedan en claro ni al
    /// escribirlos (DatosDespues) ni al cambiarlos (DatosAntes). La Mutua es
    /// un dato de organización y sigue en claro: así el test distingue
    /// «enmascara estos campos» de «enmascara toda la fila».
    /// </summary>
    [Fact]
    public async Task Los_contactos_de_la_ficha_de_empresa_no_aparecen_en_claro_ni_al_escribirlos_ni_al_cambiarlos()
    {
        const string firmante = "FIRMANTE-no-debe-aparecer";
        const string nombreSpa = "CONTACTO-SPA-no-debe-aparecer";
        const string telefonoSpa = "600111222";
        const string correoSpa = "contacto.spa@no-debe-aparecer.example";
        const string nombreFormacion = "CONTACTO-FORMACION-no-debe-aparecer";
        const string telefonoFormacion = "600333444";
        const string correoFormacion = "formacion@no-debe-aparecer.example";
        const string nombreTecnico = "TECNICO-PRL-no-debe-aparecer";
        const string telefonoTecnico = "600555666";
        const string correoTecnico = "tecnico@no-debe-aparecer.example";
        const string mutua = "MUTUA-en-claro";
        string[] personales = [firmante, nombreSpa, telefonoSpa, correoSpa, nombreFormacion, telefonoFormacion,
            correoFormacion, nombreTecnico, telefonoTecnico, correoTecnico];
        Guid empresaId;
        await using (var contexto = CrearContexto())
        {
            var empresa = new Empresa("Empresa de la ficha 360", "B10380194");
            empresaId = empresa.Id;
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var empresa = await contexto.Empresas.SingleAsync(e => e.Id == empresaId);
            empresa.ActualizarFicha("DocuSign", true, firmante, "Servicio de prevención ajeno", "Prevención Nostromo",
                nombreSpa, telefonoSpa, correoSpa, mutua, nombreFormacion, telefonoFormacion, correoFormacion,
                nombreTecnico, telefonoTecnico, correoTecnico);
            await contexto.SaveChangesAsync();
        }

        var alta = await ObtenerRegistroDeModificacionAsync(nameof(Empresa));
        foreach (var dato in personales) alta.DatosDespues.Should().NotContain(dato);
        alta.DatosDespues.Should().Contain("\"TecnicoPrlCorreo\":\"***\"");
        alta.DatosDespues.Should().Contain("\"FirmanteDocumentos\":\"***\"");
        alta.DatosDespues.Should().Contain(mutua, "la Mutua es un dato de organización, no de una persona");

        await using (var contexto = CrearContexto())
        {
            var empresa = await contexto.Empresas.SingleAsync(e => e.Id == empresaId);
            empresa.ActualizarFicha("Por correo", false, null, null, null, null, null, null, mutua,
                null, null, null, null, null, null);
            await contexto.SaveChangesAsync();
        }

        var vaciado = await ObtenerRegistroDeModificacionAsync(nameof(Empresa));
        vaciado.Id.Should().NotBe(alta.Id, "el vaciado deja su propia fila de modificación");
        foreach (var dato in personales) vaciado.DatosAntes.Should().NotContain(dato);
        vaciado.DatosAntes.Should().Contain("\"ContactoSpaNombre\":\"***\"");
    }

    /// <summary>
    /// Punto 11 del comentario de <see cref="AuditoriaInterceptor"/>: el contacto de reclamación de una Delegación de
    /// Empresa (persona física de un tercero) se enmascara en el alta y en cada cambio; el nombre de la Delegación, no.
    /// </summary>
    [Fact]
    public async Task El_contacto_de_reclamacion_de_la_delegacion_no_aparece_en_claro_ni_al_crearla_ni_al_cambiarlo()
    {
        const string nombre = "RECLAMACION-no-debe-aparecer";
        const string correo = "reclamacion@no-debe-aparecer.example";
        const string telefono = "600999111";
        const string nuevoNombre = "RECLAMACION-NUEVA-no-debe-aparecer";
        string[] personales = [nombre, correo, telefono, nuevoNombre];
        Guid delegacionId;
        await using (var contexto = CrearContexto())
        {
            var empresa = new Empresa("Empresa de la delegación", "B10380194");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();
            var delegacion = new DelegacionEmpresa(empresa.Id, "Sevilla Norte");
            delegacion.FijarContactoReclamacion(nombre, correo, telefono);
            contexto.DelegacionesEmpresa.Add(delegacion);
            await contexto.SaveChangesAsync();
            delegacionId = delegacion.Id;
        }

        var alta = await ObtenerRegistroAsync(nameof(DelegacionEmpresa));
        foreach (var dato in personales) alta.DatosDespues.Should().NotContain(dato);
        alta.DatosDespues.Should().Contain("\"ContactoReclamacionCorreo\":\"***\"");
        alta.DatosDespues.Should().Contain("Sevilla Norte", "el nombre de la Delegación es de la organización");

        await using (var contexto = CrearContexto())
        {
            var delegacion = await contexto.DelegacionesEmpresa.SingleAsync(d => d.Id == delegacionId);
            delegacion.FijarContactoReclamacion(nuevoNombre, null, null);
            await contexto.SaveChangesAsync();
        }

        var cambio = await ObtenerRegistroDeModificacionAsync(nameof(DelegacionEmpresa));
        foreach (var dato in personales)
        {
            cambio.DatosAntes.Should().NotContain(dato);
            cambio.DatosDespues.Should().NotContain(dato);
        }
        cambio.DatosDespues.Should().Contain("\"ContactoReclamacionNombre\":\"***\"");
    }

    /// <summary>
    /// Punto 10 del comentario de <see cref="AuditoriaInterceptor"/>: el contacto de la Visita 360 se enmascara en toda
    /// entrada de Visita, también en el alta, cuando se rellena copiando el contacto del Centro.
    /// </summary>
    [Fact]
    public async Task El_contacto_de_la_visita_no_aparece_en_claro_ni_al_crearla_ni_al_cambiarlo()
    {
        const string contactoDelCentro = "CONTACTO-CENTRO-no-debe-aparecer";
        const string nombre = "CONTACTO-VISITA-no-debe-aparecer";
        const string telefono = "600777888";
        const string correo = "contacto.visita@no-debe-aparecer.example";
        string[] personales = [contactoDelCentro, nombre, telefono, correo];
        Guid visitaId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente de la visita 360", "B10380194", false, null, null);
            var empresa = new Empresa("Empresa de la visita 360", "B10380186");
            contexto.Empresas.AddRange(cliente, empresa);
            await contexto.SaveChangesAsync();
            var centro = new Centro(cliente.Id, empresa.Id, "Centro de la visita 360");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var visita = new Visita(centro.Id, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3), null);
            visita.RellenarContactoDesdeCentro(contactoDelCentro);
            visitaId = visita.Id;
            contexto.Visitas.Add(visita);
            await contexto.SaveChangesAsync();
        }

        var alta = await ObtenerRegistroAsync(nameof(Visita));
        alta.Accion.Should().Be("Creado");
        foreach (var dato in personales) alta.DatosDespues.Should().NotContain(dato);
        alta.DatosDespues.Should().Contain("\"ContactoNombre\":\"***\"");

        await using (var contexto = CrearContexto())
        {
            var visita = await contexto.Visitas.SingleAsync(v => v.Id == visitaId);
            visita.ActualizarContacto(nombre, telefono, correo);
            await contexto.SaveChangesAsync();
        }

        var cambio = await ObtenerRegistroDeModificacionAsync(nameof(Visita));
        foreach (var dato in personales)
        {
            cambio.DatosAntes.Should().NotContain(dato);
            cambio.DatosDespues.Should().NotContain(dato);
        }
        cambio.DatosDespues.Should().Contain("\"ContactoCorreo\":\"***\"");
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
