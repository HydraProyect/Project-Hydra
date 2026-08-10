using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Siembra la bandeja de correo compartida con datos de prueba en vez de
/// ingesta real de Microsoft Graph — es la simplificación explícita de la
/// primera pieza del módulo Comunicaciones (ver
/// ARQUITECTURA-INTEGRACIONES.md § 12.6). Reutiliza los Clientes ya
/// sembrados por <see cref="DatosPruebaSeeder"/> (que debe ejecutarse antes)
/// en vez de crear datos maestros propios.
///
/// Mismo criterio de activación que <see cref="DatosPruebaSeeder"/>: gateado
/// por <c>DatosPrueba:Activo</c> e idempotente (si ya hay alguna
/// Conversacion, no vuelve a sembrar).
/// </summary>
public static class ComunicacionesDatosPruebaSeeder
{
    private const string RemitenteSimuladoTenant = "equipo-cae@buzon-simulado.local";

    private static readonly string[] AsuntosConversacion =
    [
        "Documentación pendiente de renovar", "Consulta sobre acceso al centro", "Alta de trabajador nuevo",
        "Vencimiento de formación PRL", "Duda sobre coordinación de actividades", "Incidencia con el acceso a planta",
        "Solicitud de certificado de empresa", "Revisión de evaluación de riesgos", "Confirmación de visita programada",
        "Baja de trabajador en el centro", "Actualización de datos de contacto", "Consulta sobre subcontrata autorizada",
        "Recordatorio de vencimiento de seguro", "Petición de copia de contrato", "Duda sobre requisito documental"
    ];

    private static readonly string[] FragmentosCuerpoEntrante =
    [
        "Buenos días, os escribo porque necesitamos revisar la documentación antes de la próxima visita.",
        "¿Podríais confirmarme si ya tenéis el certificado actualizado de este trabajador?",
        "Adjunto la información solicitada, quedamos a la espera de vuestra confirmación.",
        "Tenemos previsto el acceso al centro la semana que viene, ¿está todo en regla?",
        "Necesitamos dar de alta a un nuevo trabajador antes del lunes, ¿qué documentación hace falta?"
    ];

    private static readonly string[] FragmentosCuerpoSaliente =
    [
        "Buenos días, hemos revisado la documentación y está todo correcto.",
        "Gracias por el aviso, actualizamos el expediente y os confirmamos en breve.",
        "Adjuntamos el certificado solicitado, cualquier duda quedamos a vuestra disposición.",
        "Confirmado el acceso, el trabajador ya figura como autorizado en el centro.",
        "Hemos detectado un documento próximo a vencer, os lo señalamos para que lo renovéis con tiempo."
    ];

    private static readonly string[] TitulosMacro =
    [
        "Confirmación de documentación en regla", "Solicitud de renovación de certificado", "Aviso de vencimiento próximo",
        "Confirmación de alta de trabajador", "Recordatorio de visita programada", "Solicitud de documentación pendiente",
        "Aviso de acceso autorizado", "Cierre de incidencia resuelta", "Bienvenida a nuevo contacto", "Petición de datos de contacto"
    ];

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo"))
        {
            logger.LogInformation("DatosPrueba:Activo no está activado — no se siembra la bandeja de Comunicaciones.");
            return;
        }

        if (await dbContext.Conversaciones.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Ya hay Conversaciones sembradas — se omite la siembra de Comunicaciones.");
            return;
        }

        var clientes = await dbContext.Clientes.OrderBy(c => c.RazonSocial).Take(40).ToListAsync(cancellationToken);
        if (clientes.Count == 0)
        {
            logger.LogInformation("No hay Clientes sembrados todavía — se omite la siembra de Comunicaciones (ejecuta DatosPruebaSeeder primero).");
            return;
        }

        var trabajadores = await dbContext.Trabajadores.Take(60).ToListAsync(cancellationToken);
        var empresas = await dbContext.Empresas.Take(40).ToListAsync(cancellationToken);
        var subcontratas = await dbContext.Subcontratas.Take(40).ToListAsync(cancellationToken);
        var centros = await dbContext.Centros.Take(40).ToListAsync(cancellationToken);

        var gestoresPrueba = (await userManager.GetUsersInRoleAsync(Roles.GestorCae)).ToList();

        var aleatorio = new Random(20260730);
        var ahora = DateTime.UtcNow;

        const int totalConversaciones = 38;
        const int conversacionesTriage = 5;

        for (var i = 0; i < totalConversaciones; i++)
        {
            var esTriage = i < conversacionesTriage;
            var cliente = esTriage ? null : ElementoAleatorio(aleatorio, clientes);
            var asunto = $"{ElementoAleatorio(aleatorio, AsuntosConversacion)} — {(cliente?.RazonSocial ?? "remitente sin identificar")}";
            var etiquetas = aleatorio.Next(4) == 0 ? "urgente" : null;

            var conversacion = new Conversacion(asunto, cliente?.Id, etiquetas);

            var totalMensajes = aleatorio.Next(2, 6);
            var fechaMensaje = ahora.AddDays(-aleatorio.Next(1, 45)).AddHours(-aleatorio.Next(0, 23));
            var emailExterno = cliente is not null ? EmailSimuladoDeCliente(cliente) : "contacto@dominio-desconocido.com";

            for (var m = 0; m < totalMensajes; m++)
            {
                fechaMensaje = fechaMensaje.AddHours(aleatorio.Next(2, 30));
                if (fechaMensaje > ahora) fechaMensaje = ahora;

                var esEntrante = m % 2 == 0;
                conversacion.AgregarMensaje(
                    esEntrante ? DireccionMensaje.Entrante : DireccionMensaje.Saliente,
                    conversacion.Canal,
                    esEntrante ? emailExterno : RemitenteSimuladoTenant,
                    $"<p>{ElementoAleatorio(aleatorio, esEntrante ? FragmentosCuerpoEntrante : FragmentosCuerpoSaliente)}</p>",
                    fechaMensaje);
            }

            conversacion.AgregarParticipante(emailExterno, RolParticipante.De, TipoParticipanteOrigen.UsuarioCliente);
            conversacion.AgregarParticipante(RemitenteSimuladoTenant, RolParticipante.Para, TipoParticipanteOrigen.Desconocido);

            if (aleatorio.Next(2) == 0)
                AgregarParticipanteRelacionadoAleatorio(conversacion, aleatorio, trabajadores, empresas, subcontratas, centros);

            var estado = aleatorio.Next(100) switch
            {
                < 45 => EstadoConversacion.Abierta,
                < 75 => EstadoConversacion.Pendiente,
                < 92 => EstadoConversacion.Resuelta,
                _ => EstadoConversacion.Cerrada
            };
            if (estado != EstadoConversacion.Abierta)
                conversacion.CambiarEstado(estado);

            if (!esTriage && gestoresPrueba.Count > 0 && aleatorio.Next(3) != 0)
                conversacion.Asignar(ElementoAleatorio(aleatorio, gestoresPrueba).Id);

            dbContext.Conversaciones.Add(conversacion);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var macros = new List<MacroRespuesta>();
        for (var i = 0; i < TitulosMacro.Length; i++)
        {
            var esGenerica = i % 3 != 0;
            var clienteId = esGenerica ? (Guid?)null : ElementoAleatorio(aleatorio, clientes).Id;
            macros.Add(new MacroRespuesta(TitulosMacro[i], $"<p>{TitulosMacro[i]}. Quedamos a vuestra disposición para cualquier duda.</p>", clienteId));
        }
        dbContext.MacrosRespuesta.AddRange(macros);

        await dbContext.SaveChangesAsync(cancellationToken);

        await SembrarWhatsAppAsync(dbContext, clientes, gestoresPrueba, ahora, cancellationToken);
        await SembrarInteligenciaBandejaAsync(dbContext, gestoresPrueba, ahora, cancellationToken);

        logger.LogInformation(
            "Comunicaciones sembradas: {Conversaciones} conversaciones de correo ({Triage} sin cliente asignado), " +
            "{Macros} macros, líneas y conversaciones WhatsApp, adjuntos, eventos y sugerencias de bandeja.",
            totalConversaciones, conversacionesTriage, macros.Count);
    }

    /// <summary>
    /// Canal WhatsApp completo: dos líneas simuladas (GestorFijo y
    /// PoolInbound), contactos aprendidos y conversaciones con estados de
    /// entrega variados. Los tokens son literales de demo, nunca credenciales
    /// reales — ninguna llamada sale hacia Meta sin webhook real configurado.
    /// </summary>
    private static async Task SembrarWhatsAppAsync(
        CaeManagerDbContext dbContext, IReadOnlyList<Cliente> clientes,
        IReadOnlyList<ApplicationUser> gestoresPrueba, DateTime ahora, CancellationToken cancellationToken)
    {
        if (gestoresPrueba.Count == 0 || clientes.Count < 2)
            return;

        var gestorTitular = gestoresPrueba.OrderBy(g => g.Email).First();

        var conexionFija = new ConexionIntegracion(
            "+34600000001", "Línea WhatsApp de demo (gestor fijo)", proveedor: ProveedorIntegracion.WhatsApp);
        dbContext.ConexionesIntegracion.Add(conexionFija);
        dbContext.LineasWhatsApp.Add(new LineaWhatsApp(
            conexionFija.Id, "PNID-DEMO-0001", "WABA-DEMO-0001", "+34600000001", "token-demo-no-valido",
            ModoAsignacionLinea.GestorFijo, gestorTitular.Id,
            "Hola, soy el buzón CAE de demo. Indícanos por favor de qué cliente y consulta se trata."));

        var conexionPool = new ConexionIntegracion(
            "+34600000002", "Línea WhatsApp de demo (pool)", proveedor: ProveedorIntegracion.WhatsApp);
        dbContext.ConexionesIntegracion.Add(conexionPool);
        var lineaPool = new LineaWhatsApp(
            conexionPool.Id, "PNID-DEMO-0002", "WABA-DEMO-0002", "+34600000002", "token-demo-no-valido",
            ModoAsignacionLinea.PoolInbound);
        lineaPool.ReemplazarMiembrosPool(gestoresPrueba.Select(g => g.Id));
        dbContext.LineasWhatsApp.Add(lineaPool);

        // Contactos ya aprendidos por el enrutamiento híbrido.
        dbContext.ContactosWhatsApp.Add(new ContactoWhatsApp("+34600111001", clientes[0].Id, "Vilma Picapiedra"));
        dbContext.ContactosWhatsApp.Add(new ContactoWhatsApp("+34600111002", clientes[1].Id, "Betty Marmol"));

        // Conversaciones: contacto conocido con ventana de servicio abierta,
        // otra con la ventana ya cerrada, y una de triage sin cliente.
        var abierta = Conversacion.CrearWhatsApp("+34600111001", conexionFija.Id, clientes[0].Id, gestorTitular.Id);
        abierta.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111001",
            "<p>Buenos días, ¿está ya validada la documentación del equipo que entra el lunes?</p>", ahora.AddHours(-3));
        var respuesta = abierta.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.WhatsApp, "+34600000001",
            "<p>Buenos días, lo estamos revisando y os confirmamos hoy mismo.</p>", ahora.AddHours(-2));
        respuesta.ActualizarEstadoEntrega(EstadoEntregaMensaje.Entregado);
        respuesta.ActualizarEstadoEntrega(EstadoEntregaMensaje.Leido);
        var seguimiento = abierta.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.WhatsApp, "+34600000001",
            "<p>Confirmado: todo en regla salvo un apto médico que vence esta semana.</p>", ahora.AddHours(-1));
        seguimiento.ActualizarEstadoEntrega(EstadoEntregaMensaje.Enviado);
        dbContext.Conversaciones.Add(abierta);

        var ventanaCerrada = Conversacion.CrearWhatsApp("+34600111002", conexionFija.Id, clientes[1].Id, gestorTitular.Id);
        ventanaCerrada.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111002",
            "<p>Os pasamos el listado de trabajadores para la parada de planta.</p>", ahora.AddDays(-3));
        var fallido = ventanaCerrada.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.WhatsApp, "+34600000001",
            "<p>Recibido, gracias. Os confirmamos mañana.</p>", ahora.AddDays(-1));
        fallido.ActualizarEstadoEntrega(EstadoEntregaMensaje.Fallido,
            "Message failed to send because more than 24 hours have passed since the customer last replied.");
        ventanaCerrada.CambiarEstado(EstadoConversacion.Pendiente);
        dbContext.Conversaciones.Add(ventanaCerrada);

        var triage = Conversacion.CrearWhatsApp("+34600111003", conexionPool.Id, clienteId: null,
            gestoresPrueba.Count > 1 ? gestoresPrueba.OrderBy(g => g.Email).ElementAt(1).Id : gestorTitular.Id);
        triage.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111003",
            "<p>Hola, llamo de parte de la contrata de climatización, ¿me podéis ayudar con un acceso?</p>", ahora.AddHours(-6));
        var autoTriage = triage.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.WhatsApp, "+34600000002",
            "<p>Hola, soy el buzón CAE de demo. Indícanos por favor de qué cliente y consulta se trata.</p>", ahora.AddHours(-6));
        autoTriage.ActualizarEstadoEntrega(EstadoEntregaMensaje.Entregado);
        dbContext.Conversaciones.Add(triage);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Lo que la bandeja muestra alrededor de los mensajes: adjuntos, eventos
    /// del timeline, sugerencias IA de visita y de gestión (pendientes y
    /// resueltas) y solicitudes de prioridad ya enviadas.
    /// </summary>
    private static async Task SembrarInteligenciaBandejaAsync(
        CaeManagerDbContext dbContext, IReadOnlyList<ApplicationUser> gestoresPrueba,
        DateTime ahora, CancellationToken cancellationToken)
    {
        var mensajesConCliente = await (
            from mensaje in dbContext.Mensajes
            join conversacion in dbContext.Conversaciones on mensaje.ConversacionId equals conversacion.Id
            where mensaje.Direccion == DireccionMensaje.Entrante
                  && mensaje.Canal == CanalConversacion.Correo
                  && conversacion.ClienteId != null
            orderby mensaje.FechaUtc, mensaje.Id
            select new { Mensaje = mensaje, ClienteId = conversacion.ClienteId!.Value })
            .Take(6)
            .ToListAsync(cancellationToken);

        if (mensajesConCliente.Count < 6)
            return;

        // Adjuntos entrantes — el contenido no existe en el storage (la
        // descarga fallará limpiamente), pero la bandeja los lista y el flujo
        // "Actualizar documentación" se puede iniciar.
        mensajesConCliente[0].Mensaje.AgregarAdjunto(
            "apto-medico-bart-simpson.pdf", "application/pdf", 245_760, "adjuntos-demo/apto-medico-bart-simpson.pdf");
        mensajesConCliente[0].Mensaje.AgregarAdjunto(
            "epis-firmadas.jpg", "image/jpeg", 812_040, "adjuntos-demo/epis-firmadas.jpg");
        mensajesConCliente[1].Mensaje.AgregarAdjunto(
            "ita-julio.pdf", "application/pdf", 1_310_720, "adjuntos-demo/ita-julio.pdf");

        // Eventos del timeline: los tres tipos, referenciando entidades reales.
        var visitaReferencia = await dbContext.Visitas.OrderBy(v => v.CreadoEnUtc).ThenBy(v => v.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var documentoReferencia = await dbContext.Documentos.OrderBy(d => d.CreadoEnUtc).ThenBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (visitaReferencia is not null)
            dbContext.EventosConversacion.Add(new EventoConversacion(
                mensajesConCliente[0].Mensaje.ConversacionId, TipoEventoConversacion.VisitaCreada,
                visitaReferencia.Id, ahora.AddDays(-2)));
        if (documentoReferencia is not null)
            dbContext.EventosConversacion.Add(new EventoConversacion(
                mensajesConCliente[1].Mensaje.ConversacionId, TipoEventoConversacion.DocumentoActualizado,
                documentoReferencia.Id, ahora.AddDays(-1)));
        dbContext.EventosConversacion.Add(new EventoConversacion(
            mensajesConCliente[2].Mensaje.ConversacionId, TipoEventoConversacion.ConversacionVinculada,
            mensajesConCliente[3].Mensaje.ConversacionId, ahora.AddHours(-20)));

        // Sugerencias de visita: con centro resuelto, sin centro, y resuelta.
        var hoy = DateOnly.FromDateTime(ahora);
        var centroDelCliente = await dbContext.Centros
            .Where(c => c.ClienteId == mensajesConCliente[2].ClienteId)
            .OrderBy(c => c.CreadoEnUtc).ThenBy(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        dbContext.SugerenciasVisitaCorreo.Add(new SugerenciaVisitaCorreo(
            mensajesConCliente[2].Mensaje.Id, centroDelCliente?.Id, hoy.AddDays(7), hoy.AddDays(8),
            "El correo pide agendar la entrada de dos operarios la semana que viene.", 92, 88, 90));
        dbContext.SugerenciasVisitaCorreo.Add(new SugerenciaVisitaCorreo(
            mensajesConCliente[3].Mensaje.Id, centroId: null, hoy.AddDays(10), null,
            "Se detecta intención de visita pero el correo no concreta a qué centro se refiere.", 74, 40, 70));
        var sugerenciaResuelta = new SugerenciaVisitaCorreo(
            mensajesConCliente[4].Mensaje.Id, centroDelCliente?.Id, hoy.AddDays(-3), hoy.AddDays(-3),
            "Solicitud de visita ya gestionada desde la bandeja.", 95, 93, 94);
        sugerenciaResuelta.Resolver();
        dbContext.SugerenciasVisitaCorreo.Add(sugerenciaResuelta);

        // Sugerencia de gestión con dos ítems: uno pendiente y uno resuelto.
        var trabajadorSugerido = await dbContext.Trabajadores.OrderBy(t => t.CreadoEnUtc).ThenBy(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var tipoEpis = await dbContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Nombre == "EPIS (firma)", cancellationToken);
        if (trabajadorSugerido is not null && tipoEpis is not null)
        {
            var sugerenciaGestion = new SugerenciaGestionCorreo(
                mensajesConCliente[5].Mensaje.Id,
                "El correo notifica en bloque la renovación de EPIs de varios trabajadores.", 86);
            sugerenciaGestion.AgregarDetalle(trabajadorSugerido.Id, tipoEpis.Id, 91, 88);
            var detalleResuelto = sugerenciaGestion.AgregarDetalle(trabajadorSugerido.Id, tipoEpis.Id, 79, 84);
            detalleResuelto.Resolver();
            dbContext.SugerenciasGestionCorreo.Add(sugerenciaGestion);
        }

        // Solicitudes de prioridad ya enviadas (rastro anti-duplicados).
        if (gestoresPrueba.Count > 0 && centroDelCliente is not null)
        {
            var gestor = gestoresPrueba.OrderBy(g => g.Email).First();
            dbContext.SolicitudesPrioridadDocumento.Add(new SolicitudPrioridadDocumento(
                centroDelCliente.Id, gestor.Id, ahora.AddHours(-30)));
            dbContext.SolicitudesPrioridadDocumento.Add(new SolicitudPrioridadDocumento(
                centroDelCliente.Id, gestor.Id, ahora.AddHours(-2)));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void AgregarParticipanteRelacionadoAleatorio(
        Conversacion conversacion, Random aleatorio,
        IReadOnlyList<Trabajador> trabajadores,
        IReadOnlyList<Empresa> empresas,
        IReadOnlyList<Subcontrata> subcontratas,
        IReadOnlyList<Centro> centros)
    {
        switch (aleatorio.Next(4))
        {
            case 0 when trabajadores.Count > 0:
                var trabajador = ElementoAleatorio(aleatorio, trabajadores);
                conversacion.AgregarParticipante($"{trabajador.NombreCompleto.Replace(" ", ".").ToLowerInvariant()}@trabajador.local", RolParticipante.Cc, TipoParticipanteOrigen.Trabajador, trabajador.Id);
                break;
            case 1 when empresas.Count > 0:
                var empresa = ElementoAleatorio(aleatorio, empresas);
                conversacion.AgregarParticipante($"contacto@{Slug(empresa.RazonSocial)}.com", RolParticipante.Cc, TipoParticipanteOrigen.Empresa, empresa.Id);
                break;
            case 2 when subcontratas.Count > 0:
                var subcontrata = ElementoAleatorio(aleatorio, subcontratas);
                conversacion.AgregarParticipante($"contacto@{Slug(subcontrata.RazonSocial)}.com", RolParticipante.Cc, TipoParticipanteOrigen.Subcontrata, subcontrata.Id);
                break;
            case 3 when centros.Count > 0:
                var centro = ElementoAleatorio(aleatorio, centros);
                conversacion.AgregarParticipante($"{Slug(centro.Nombre)}@centro.local", RolParticipante.Cc, TipoParticipanteOrigen.Centro, centro.Id);
                break;
            default:
                conversacion.AgregarParticipante("desconocido@sin-resolver.local", RolParticipante.Cc, TipoParticipanteOrigen.Desconocido);
                break;
        }
    }

    private static string EmailSimuladoDeCliente(Cliente cliente) => $"contacto@{Slug(cliente.RazonSocial)}.com";

    private static string Slug(string texto)
    {
        var normalizado = new string(texto.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return normalizado.Length > 20 ? normalizado[..20] : normalizado;
    }

    private static T ElementoAleatorio<T>(Random aleatorio, IReadOnlyList<T> lista) => lista[aleatorio.Next(lista.Count)];
}
