using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Consume la cola de <see cref="EventoWebhook"/> — invocado desde
/// <c>IngestaWebhookHostedService</c> (Infrastructure), nunca desde el
/// propio endpoint HTTP del webhook (ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md §
/// 6.4: nunca procesar Graph de forma síncrona dentro del request
/// entrante). No es un Command de MediatR a propósito: el hosted service ya
/// carga el <see cref="EventoWebhook"/> pendiente dentro de su propio scope
/// (mismo patrón que <c>ProcesadorAnalisisDocumentoHostedService</c>), así
/// que un Command que solo recibiera el Id obligaría a recargarlo dos veces
/// en la misma operación.
/// </summary>
public class IngestaWebhookService(
    IConexionIntegracionRepository conexionRepositorio,
    IConversacionRepository conversacionRepositorio,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IFileStorageService almacenamiento,
    ISugerenciaVisitaCorreoService sugerenciaVisita,
    ISugerenciaGestionCorreoService sugerenciaGestion,
    IResolucionParticipanteConversacionService resolucionParticipante,
    IResolucionProveedorPlataformaCaeService resolucionPlataforma,
    IClasificacionRuidoMensajeRepository clasificacionRuidoRepositorio,
    IClasificacionRuidoMensajeService clasificacionRuidoMensaje,
    IRelevanciaCaeService relevanciaCae,
    IAlertaCriticaService alertaCritica,
    ILogger<IngestaWebhookService> logger,
    IIntencionConversacionService intenciones,
    CaeManager.Application.Clientes.IDirectorioDestinosCartera directorioDestinos)
{
    private readonly List<string> _archivosGuardados = [];

    /// <summary>
    /// Claves de <see cref="IFileStorageService"/> escritas durante ESTE
    /// <see cref="ProcesarAsync"/> (auditoría módulo 6, retención/orfandad de
    /// blobs): el registro que las referenciaría se guarda en una única
    /// transacción, DESPUÉS de que este método vuelve — si ese guardado
    /// falla, el llamador (<c>IngestaWebhookHostedService</c>) necesita esta
    /// lista para poder compensar y no dejar el archivo huérfano en el
    /// almacenamiento. Instancia nueva por evento (esta clase se resuelve
    /// dentro de un <c>IServiceScope</c> por evento), así que no hace falta
    /// limpiarla entre llamadas.
    /// </summary>
    public IReadOnlyList<string> ArchivosGuardados => _archivosGuardados;

    public async Task ProcesarAsync(EventoWebhook evento, CancellationToken cancellationToken)
    {
        try
        {
            await IngerirNotificacionAsync(evento, cancellationToken);
            evento.MarcarProcesado();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Apagado cooperativo de la aplicación, no un fallo de ingesta:
            // debe propagarse tal cual para que el hosted service devuelva
            // el evento a Pendiente sin gastar un intento — ver
            // IngestaWebhookHostedService.ProcesarPendientesDelTenantAsync.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo procesando el evento de webhook {EventoId} (intento {Intento}).", evento.Id, evento.Intentos + 1);
            evento.RegistrarFallo(ex.Message);
        }
    }

    private async Task IngerirNotificacionAsync(EventoWebhook evento, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(evento.ConexionIntegracionId, cancellationToken);
        if (conexion is null)
            return; // se borró entre medias — se descarta sin reintentar, no es un fallo transitorio.

        if (conexion.Estado != EstadoConexionIntegracion.Habilitada)
        {
            // Hasta que N4 cablee MarcarConError desde la renovación de
            // suscripción de Graph, ConError era inalcanzable en producción
            // (solo lo ponía el seeder de demo) y este descarte silencioso
            // no importaba. Con esa llamada real, una notificación de Graph
            // sí puede llegar mientras la conexión está ConError o
            // Deshabilitada — sin este log, el síntoma es "no llegan
            // mensajes" descubierto semanas después, sin ningún rastro de
            // por qué. RegistrarFallo no encaja aquí: no es un fallo
            // transitorio que deba reintentarse, es un descarte de negocio
            // deliberado que se resuelve reactivando la conexión.
            logger.LogWarning(
                "Notificación de webhook {EventoId} descartada: la conexión {ConexionId} está en estado {Estado}.",
                evento.Id, conexion.Id, conexion.Estado);
            return;
        }

        var mensajeIds = graphClient.ExtraerMensajeIdsDeNotificacion(evento.PayloadCrudo);

        foreach (var mensajeId in mensajeIds)
            await IngerirMensajeAsync(conexion, mensajeId, cancellationToken);
    }

    private async Task IngerirMensajeAsync(ConexionIntegracion conexion, string mensajeId, CancellationToken cancellationToken)
    {
        var accessTokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);
        if (accessTokenResultado.EsFallido)
            throw new InvalidOperationException(accessTokenResultado.Error.Mensaje);

        var mensajeResultado = await graphClient.ObtenerMensajeAsync(accessTokenResultado.Valor, conexion.BuzonEmail, mensajeId, cancellationToken);
        if (mensajeResultado.EsFallido)
        {
            // Un mensaje puntual que Graph ya no tiene (borrado entre la
            // notificación y la ingesta) no debe tirar el resto del evento.
            logger.LogWarning(
                "No se pudo obtener el mensaje {MensajeId} de Graph para la conexión {ConexionId}: {Error}",
                mensajeId, conexion.Id, mensajeResultado.Error.Mensaje);
            return;
        }

        var mensaje = mensajeResultado.Valor;

        if (await conversacionRepositorio.ExisteMensajeExternoAsync(conexion.Id, mensaje.MensajeExternoId, cancellationToken))
            return; // idempotencia: Graph puede reenviar la misma notificación más de una vez.

        var candidatos = await resolucionPlataforma.ResolverPorDominioCorreoAsync(mensaje.Remitente, cancellationToken);
        var esPlataformaAutenticada = candidatos.Count > 0 && AutenticacionCorreoPlataforma.EsValida(mensaje);
        if (candidatos.Count > 0 && !esPlataformaAutenticada)
            logger.LogInformation(
                "Correo {MensajeExternoId} con dominio de Plataforma CAE sin autenticación SPF/DKIM/DMARC válida: se trata como correo externo.",
                mensaje.MensajeExternoId);
        // Un remitente que suplanta una Plataforma CAE tampoco puede eludir la continuación
        // de un hilo cerrado mediante noreply o cabeceras de respuesta automática.
        var esCorreoAutomatico = esPlataformaAutenticada
            || (candidatos.Count == 0 && DeteccionCorreoAutomatico.EsAutomatico(mensaje));

        var conversacion = await conversacionRepositorio.ObtenerPorHiloExternoAsync(conexion.Id, mensaje.HiloExternoId, cancellationToken);
        var conversacionNueva = false;
        if (conversacion is null)
        {
            var anterior = await conversacionRepositorio.ObtenerUltimaCerradaPorHiloExternoAsync(conexion.Id, mensaje.HiloExternoId, cancellationToken);
            // Un automático (autorrespondedor, aviso de plataforma) sobre un hilo Cerrado se anexa a la Cerrada sin
            // reabrirla: abrir una continuación solo con ruido llenaría el triage.
            if (anterior is not null && esCorreoAutomatico)
                conversacion = anterior;
            else
            {
                conversacion = anterior is not null
                    ? Conversacion.ContinuarCorreo(anterior, mensaje.Asunto)
                    : new Conversacion(mensaje.Asunto, conexion.ClienteId);
                if (anterior is null)
                {
                    conversacion.AsociarConexion(conexion.Id, mensaje.HiloExternoId);
                    await AsignarAlResponsableDelBuzonAsync(conversacion, conexion, cancellationToken);
                }
                conversacionRepositorio.Agregar(conversacion);
                conversacionNueva = true;
            }
        }
        var mensajeCreado = conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, conversacion.Canal, mensaje.Remitente, mensaje.CuerpoHtml, mensaje.FechaUtc, mensaje.MensajeExternoId,
            esAutomatico: esCorreoAutomatico);
        // Un correo que llega con retraso trae la fecha de su envío, anterior a la creación del hilo: la lista debe
        // enseñar la del mensaje, no la de la ingesta.
        if (conversacionNueva)
            conversacion.AlinearFechaConLosMensajes();

        await intenciones.ProcesarAsync(conversacion, cancellationToken);

        await alertaCritica.ProcesarAsync(mensajeCreado, cancellationToken);

        foreach (var participante in mensaje.Participantes)
        {
            var (tipoOrigen, entidadRelacionadaId) = await resolucionParticipante.ResolverAsync(
                participante.Email, conversacion.ClienteId, cancellationToken);
            conversacion.AgregarParticipante(participante.Email, participante.Rol, tipoOrigen, entidadRelacionadaId);
        }

        foreach (var adjunto in mensaje.Adjuntos)
            await DescargarYGuardarAdjuntoAsync(mensajeCreado, accessTokenResultado.Valor, mensaje.MensajeExternoId, adjunto, cancellationToken);

        // Gate compartido por todos los patrones de ruido de esta ronda — solo Correo tiene
        // dominio en el remitente (WhatsApp es un teléfono). Se calcula una vez aquí y queda
        // estable aunque el catálogo de plataformas cambie después, igual que las sugerencias de IA.
        var esNotificacionAutomatica = false;
        ClasificacionRuidoMensaje? clasificacionMensaje = null;
        Guid? proveedorPlataformaCaeId = null;
        if (conversacion.Canal == CanalConversacion.Correo)
        {
            esNotificacionAutomatica = esPlataformaAutenticada;
            proveedorPlataformaCaeId = esNotificacionAutomatica ? candidatos[0].Id : null;
            clasificacionMensaje = new ClasificacionRuidoMensaje(mensajeCreado.Id, esNotificacionAutomatica, proveedorPlataformaCaeId);
            clasificacionRuidoRepositorio.Agregar(clasificacionMensaje);

            // En el buzón personal del Gestor CAE, solo una Plataforma CAE autenticada evita
            // la clasificación como correo interno o posible phishing. En un buzón del
            // Cliente empresarial se conserva el tratamiento habitual del correo externo.
            if (conexion.GestorPropietarioId is not null && !esNotificacionAutomatica)
                clasificacionMensaje.CambiarMotivo(ClasificarCorreoBuzonPersonal(mensaje.Remitente, conexion.BuzonEmail));
        }

        // Sin Cliente asignado no hay Centros candidatos a los que asociar
        // una sugerencia — la conversación sigue en la cola de triage.
        if (conversacion.ClienteId is { } clienteId)
        {
            await relevanciaCae.ProcesarAsync(conversacion, cancellationToken);
            await sugerenciaVisita.ProcesarAsync(mensajeCreado, clienteId, cancellationToken);
            var resultadoGestion = await sugerenciaGestion.ProcesarAsync(mensajeCreado, clienteId, cancellationToken);

            if (resultadoGestion.Sugerencia is { } sugerenciaGestionCreada)
            {
                await clasificacionRuidoMensaje.ProcesarAsync(
                    sugerenciaGestionCreada, clienteId, esNotificacionAutomatica, cancellationToken);
            }
            else if (esNotificacionAutomatica && proveedorPlataformaCaeId is { } plataformaId
                && resultadoGestion.ResumenAgregado is { } resumenAgregado)
            {
                var sinCambios = await clasificacionRuidoMensaje.ProcesarResumenAsync(
                    clienteId, plataformaId, resumenAgregado, cancellationToken);
                if (sinCambios)
                    clasificacionMensaje?.CambiarMotivo(MotivoRuidoMensaje.ResumenSinCambios);
            }
        }
    }

    /// <summary>
    /// Toda conversación nace asignada al responsable de su buzón. La continuación de un hilo cerrado conserva
    /// a quien ya la llevaba. Un responsable que ya no es alcanzable en el Tenant no se asigna: una conversación
    /// en manos de alguien sin acceso quedaría oculta para el resto, y sin asignar sigue visible en la bandeja.
    /// </summary>
    private async Task AsignarAlResponsableDelBuzonAsync(
        Conversacion conversacion, ConexionIntegracion conexion, CancellationToken cancellationToken)
    {
        if (conexion.ResponsableEfectivoId is not { } responsableId)
            return;

        // Una cuenta desactivada no entra a atenderla, y asignada a ella tampoco saldría entre las sin asignar.
        if (await directorioDestinos.ObtenerAsync(responsableId, cancellationToken) is { Activa: true })
            conversacion.Asignar(responsableId);
        else
            logger.LogWarning(
                "La conversación nueva de la conexión {ConexionId} queda sin asignar: su responsable {UsuarioId} está desactivado o ya no es alcanzable en el Tenant.",
                conexion.Id, responsableId);
    }

    /// <summary>
    /// Correo interno cuando el remitente comparte dominio (o subdominio) con el propio buzón
    /// personal conectado — determinista, sin IA, mismo mecanismo de comparación de host que
    /// <see cref="ResolucionProveedorPlataformaCaeService.CoincideHost"/>. Cualquier otra cosa se
    /// marca como posible phishing: heurística deliberadamente simple (§ "Preguntas de menor
    /// impacto" del plan) — nunca se oculta ni se bloquea el correo, solo es una advertencia visual,
    /// la decisión de qué hacer siempre es del gestor.
    /// </summary>
    public static MotivoRuidoMensaje ClasificarCorreoBuzonPersonal(string? remitente, string? buzonPropio)
    {
        var dominioRemitente = ResolucionProveedorPlataformaCaeService.ExtraerDominioCorreo(remitente);
        var dominioPropio = ResolucionProveedorPlataformaCaeService.ExtraerDominioCorreo(buzonPropio);

        return dominioRemitente is not null && dominioPropio is not null
            && ResolucionProveedorPlataformaCaeService.CoincideHost(dominioRemitente, dominioPropio)
                ? MotivoRuidoMensaje.CorreoInterno
                : MotivoRuidoMensaje.PosiblePhishing;
    }

    /// <summary>
    /// Mejor esfuerzo por adjunto — un adjunto que falla al descargar no debe
    /// perder el mensaje entero (ya persistido con su cuerpo, que es lo
    /// esencial). Se registra y se sigue con el resto.
    /// </summary>
    private async Task DescargarYGuardarAdjuntoAsync(
        Mensaje mensaje, string accessToken, string mensajeExternoId, AdjuntoGraphDto adjunto, CancellationToken cancellationToken)
    {
        if (adjunto.TamanoBytes > LimitesAdjuntosCorreo.TamanoMaximoDescargaBytes)
        {
            logger.LogWarning(
                "Adjunto {AdjuntoId} del mensaje {MensajeId} omitido: {TamanoBytes} bytes supera el máximo de {Maximo}.",
                adjunto.AdjuntoExternoId, mensajeExternoId, adjunto.TamanoBytes, LimitesAdjuntosCorreo.TamanoMaximoDescargaBytes);
            return;
        }

        var contenidoResultado = await graphClient.ObtenerContenidoAdjuntoAsync(accessToken, mensajeExternoId, adjunto.AdjuntoExternoId, cancellationToken);
        if (contenidoResultado.EsFallido)
        {
            logger.LogWarning(
                "No se pudo descargar el adjunto {AdjuntoId} del mensaje {MensajeId}: {Error}",
                adjunto.AdjuntoExternoId, mensajeExternoId, contenidoResultado.Error.Mensaje);
            return;
        }

        using var flujo = new MemoryStream(contenidoResultado.Valor);
        var archivoUrl = await almacenamiento.GuardarAsync(flujo, adjunto.NombreArchivo, cancellationToken);
        _archivosGuardados.Add(archivoUrl);
        mensaje.AgregarAdjunto(adjunto.NombreArchivo, adjunto.TipoContenido, adjunto.TamanoBytes, archivoUrl);
    }
}
