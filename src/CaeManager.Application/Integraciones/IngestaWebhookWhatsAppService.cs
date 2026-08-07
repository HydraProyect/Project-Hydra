using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Consume la cola de <see cref="EventoWebhook"/> del proveedor WhatsApp —
/// invocado desde <c>IngestaWebhookWhatsAppHostedService</c>, nunca desde el
/// endpoint HTTP (§ 6.4). Clase servicio y no Command por el mismo criterio
/// documentado en <see cref="IngestaWebhookService"/>.
///
/// Enrutamiento híbrido de conversaciones nuevas (decisión 2026-08-04):
/// 1) teléfono→Cliente vía <see cref="ContactoWhatsApp"/> (catálogo
///    autoalimentado) → gestor de cartera (Cliente.EjecutivoUsuarioId);
/// 2) si no, el modo de la línea: GestorFijo → su comercial dueño;
///    PoolInbound → el miembro con menos conversaciones WhatsApp
///    abiertas/pendientes asignadas (reparto equitativo por carga);
/// 3) si tras eso el Cliente sigue sin identificarse y la línea tiene
///    MensajeAutoTriage, se envía el auto-mensaje pidiendo cliente y motivo
///    (gratis: el entrante acaba de abrir la ventana de 24 h) y la
///    conversación queda en la cola de triage (ClienteId null).
///
/// Devuelve los avisos de mensaje nuevo para que el hosted service los
/// publique DESPUÉS de SaveChanges — la ingesta no publica ni conoce a los
/// suscriptores (§ 6.5).
/// </summary>
public class IngestaWebhookWhatsAppService(
    IConexionIntegracionRepository conexionRepositorio,
    ILineaWhatsAppRepository lineaRepositorio,
    IConversacionRepository conversacionRepositorio,
    IContactoWhatsAppRepository contactoRepositorio,
    IClienteRepository clienteRepositorio,
    IWhatsAppCloudApiClient whatsAppClient,
    IFileStorageService almacenamiento,
    ILogger<IngestaWebhookWhatsAppService> logger)
{
    public async Task<IReadOnlyList<MensajeWhatsAppRecibidoEvent>> ProcesarAsync(
        EventoWebhook evento, CancellationToken cancellationToken)
    {
        try
        {
            var avisos = await IngerirNotificacionAsync(evento, cancellationToken);
            evento.MarcarProcesado();
            return avisos;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo procesando el evento de webhook de WhatsApp {EventoId} (intento {Intento}).",
                evento.Id, evento.Intentos + 1);
            evento.RegistrarFallo(ex.Message);
            return [];
        }
    }

    private async Task<IReadOnlyList<MensajeWhatsAppRecibidoEvent>> IngerirNotificacionAsync(
        EventoWebhook evento, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(evento.ConexionIntegracionId, cancellationToken);
        if (conexion is null || conexion.Estado != EstadoConexionIntegracion.Habilitada)
            return []; // la línea se deshabilitó entre medias — se descarta sin reintentar.

        var linea = await lineaRepositorio.ObtenerPorConexionAsync(conexion.Id, cancellationToken);
        if (linea is null)
            return [];

        var notificacion = whatsAppClient.ExtraerNotificacion(evento.PayloadCrudo, linea.PhoneNumberId);
        var avisos = new List<MensajeWhatsAppRecibidoEvent>();

        foreach (var mensaje in notificacion.Mensajes)
        {
            var conversacionId = await IngerirMensajeAsync(conexion, linea, mensaje, cancellationToken);
            if (conversacionId is { } id)
                avisos.Add(new MensajeWhatsAppRecibidoEvent(evento.TenantId, id));
        }

        foreach (var estado in notificacion.Estados)
            await AplicarEstadoAsync(estado, cancellationToken);

        return avisos;
    }

    /// <summary>Devuelve el Id de la conversación tocada, o null si el mensaje se descartó (duplicado).</summary>
    private async Task<Guid?> IngerirMensajeAsync(
        ConexionIntegracion conexion, LineaWhatsApp linea, MensajeEntranteWhatsAppDto mensaje, CancellationToken cancellationToken)
    {
        // Meta reintenta webhooks agresivamente — el wamid es la clave de
        // idempotencia (índice único {TenantId, MensajeExternoId}).
        if (await conversacionRepositorio.ExisteMensajeExternoAsync(mensaje.Wamid, cancellationToken))
            return null;

        var conversacion = await conversacionRepositorio.ObtenerAbiertaPorTelefonoAsync(
            conexion.Id, mensaje.Telefono, cancellationToken);

        var esNueva = conversacion is null;
        if (conversacion is null)
        {
            var destino = await ResolverAsignacionAsync(conexion, linea, mensaje.Telefono, cancellationToken);
            conversacion = Conversacion.CrearWhatsApp(
                mensaje.Telefono, conexion.Id, destino.ClienteId, destino.EjecutivoId);
            conversacionRepositorio.Agregar(conversacion);
        }

        var mensajeCreado = conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, mensaje.Telefono, CuerpoParaPersistir(mensaje), mensaje.FechaUtc, mensaje.Wamid);

        if (mensaje.MediaId is not null)
            await DescargarYGuardarMediaAsync(linea, mensajeCreado, mensaje, cancellationToken);

        if (esNueva && conversacion.ClienteId is null && linea.MensajeAutoTriage is { } autoMensaje)
            await EnviarAutoTriageAsync(linea, conversacion, autoMensaje, cancellationToken);

        return conversacion.Id;
    }

    private sealed record DestinoAsignacion(Guid? ClienteId, Guid? EjecutivoId);

    private async Task<DestinoAsignacion> ResolverAsignacionAsync(
        ConexionIntegracion conexion, LineaWhatsApp linea, string telefono, CancellationToken cancellationToken)
    {
        // Leg 1: el teléfono ya se resolvió alguna vez contra un Cliente →
        // directo al gestor de cartera (modelo outbound).
        var contacto = await contactoRepositorio.ObtenerPorTelefonoAsync(telefono, cancellationToken);
        if (contacto is not null)
        {
            var cliente = await clienteRepositorio.ObtenerPorIdAsync(contacto.ClienteId, cancellationToken);
            if (cliente is not null)
                return new DestinoAsignacion(cliente.Id, cliente.EjecutivoUsuarioId ?? await ElegirDestinoDeLineaAsync(linea, cancellationToken));
        }

        // Leg 2: el modo de la línea decide quién la atiende. El ClienteId de
        // la conexión (línea dedicada a un cliente, § 12.2) resuelve el
        // cliente aunque el contacto sea desconocido.
        return new DestinoAsignacion(conexion.ClienteId, await ElegirDestinoDeLineaAsync(linea, cancellationToken));
    }

    private async Task<Guid?> ElegirDestinoDeLineaAsync(LineaWhatsApp linea, CancellationToken cancellationToken)
    {
        if (linea.Modo == ModoAsignacionLinea.GestorFijo)
            return linea.ComercialAsignadoId;

        var miembros = linea.MiembrosPool.Select(m => m.UsuarioId).ToList();
        if (miembros.Count == 0)
        {
            logger.LogWarning("La línea {LineaId} está en modo pool sin miembros — la conversación queda sin asignar.", linea.Id);
            return null;
        }

        // Reparto equitativo por carga: el miembro con menos conversaciones
        // WhatsApp vivas asignadas; empate → orden estable por Id.
        var cargas = await conversacionRepositorio.ContarWhatsAppVivasPorEjecutivoAsync(miembros, cancellationToken);

        return miembros
            .OrderBy(m => cargas.GetValueOrDefault(m, 0))
            .ThenBy(m => m)
            .First();
    }

    /// <summary>Mensaje exige cuerpo no vacío — los tipos sin texto persisten un placeholder para no perder el hilo (ni la ventana de 24 h).</summary>
    private static string CuerpoParaPersistir(MensajeEntranteWhatsAppDto mensaje)
    {
        if (!string.IsNullOrWhiteSpace(mensaje.Texto)) return mensaje.Texto;

        return mensaje.Tipo switch
        {
            "image" => "[Imagen]",
            "document" => $"[Documento{(mensaje.NombreArchivo is null ? null : $": {mensaje.NombreArchivo}")}]",
            _ => $"[Contenido no soportado: {mensaje.Tipo}]",
        };
    }

    /// <summary>Mejor esfuerzo — un media que falla no pierde el mensaje (ya persistido con su placeholder/caption).</summary>
    private async Task DescargarYGuardarMediaAsync(
        LineaWhatsApp linea, Mensaje mensajeCreado, MensajeEntranteWhatsAppDto mensaje, CancellationToken cancellationToken)
    {
        var descarga = await whatsAppClient.DescargarMediaAsync(linea.TokenAcceso, mensaje.MediaId!, cancellationToken);
        if (descarga.EsFallido)
        {
            logger.LogWarning("No se pudo descargar el media {MediaId} del mensaje {Wamid}: {Error}",
                mensaje.MediaId, mensaje.Wamid, descarga.Error.Mensaje);
            return;
        }

        var nombreArchivo = mensaje.NombreArchivo
                            ?? $"whatsapp-{mensaje.Tipo}-{DateTime.UtcNow:yyyyMMddHHmmss}{ExtensionPorMime(descarga.Valor.TipoContenido)}";

        using var flujo = new MemoryStream(descarga.Valor.Contenido);
        var archivoUrl = await almacenamiento.GuardarAsync(flujo, nombreArchivo, cancellationToken);
        mensajeCreado.AgregarAdjunto(
            nombreArchivo, mensaje.TipoContenido ?? descarga.Valor.TipoContenido, descarga.Valor.Contenido.LongLength, archivoUrl);
    }

    private static string ExtensionPorMime(string mime) => mime switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "application/pdf" => ".pdf",
        _ => string.Empty,
    };

    /// <summary>Mejor esfuerzo — si el auto-mensaje falla, la conversación queda en triage igualmente y un gestor la atenderá a mano.</summary>
    private async Task EnviarAutoTriageAsync(
        LineaWhatsApp linea, Conversacion conversacion, string autoMensaje, CancellationToken cancellationToken)
    {
        var envio = await whatsAppClient.EnviarTextoAsync(
            linea.TokenAcceso, linea.PhoneNumberId, conversacion.TelefonoContacto!, autoMensaje, cancellationToken);
        if (envio.EsFallido)
        {
            logger.LogWarning("No se pudo enviar el auto-mensaje de triage por la línea {LineaId}: {Error}",
                linea.Id, envio.Error.Mensaje);
            return;
        }

        var mensajeAuto = conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, linea.NumeroTelefono, autoMensaje, mensajeExternoId: envio.Valor);
        mensajeAuto.ActualizarEstadoEntrega(EstadoEntregaMensaje.Enviado);
    }

    private async Task AplicarEstadoAsync(EstadoMensajeWhatsAppDto estado, CancellationToken cancellationToken)
    {
        var estadoEntrega = estado.Estado switch
        {
            "sent" => EstadoEntregaMensaje.Enviado,
            "delivered" => EstadoEntregaMensaje.Entregado,
            "read" => EstadoEntregaMensaje.Leido,
            "failed" => EstadoEntregaMensaje.Fallido,
            _ => (EstadoEntregaMensaje?)null,
        };
        if (estadoEntrega is null) return;

        var mensaje = await conversacionRepositorio.ObtenerMensajePorExternoIdAsync(estado.Wamid, cancellationToken);
        // Un status de un mensaje que no tenemos (p. ej. enviado desde otra
        // herramienta con la misma línea) no es un error — se ignora.
        mensaje?.ActualizarEstadoEntrega(estadoEntrega.Value, estado.Error);
    }
}
