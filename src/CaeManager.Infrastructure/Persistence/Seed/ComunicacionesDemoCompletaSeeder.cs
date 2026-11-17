using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Siembra, en el Tenant propietario de la demo (el de los Gestores CAE <c>prueba.gestorcae&lt;n&gt;</c>, donde
/// <see cref="DelegacionDemoSeeder"/> la llama dentro de su ámbito), un conjunto curado de Comunicaciones para revisar en el
/// navegador toda la pantalla de Comunicaciones y la de Buzones: 16 conversaciones (13 de correo y 3 de WhatsApp) de varias
/// Empresas cliente, en los seis estados y las tres prioridades, con plazos vencidos, por vencer y con tiempo, asignadas al
/// Gestor CAE de la demo, a otro y a nadie, con notas internas, adjuntos en cada estado de reparto (uno, un PDF de seis páginas
/// que se puede dividir) y dos reclamaciones en el historial.
///
/// <para>
/// <b>Por qué existe aparte de <see cref="ComunicacionesDatosPruebaSeeder"/>.</b> Esa siembra se omite entera si el Tenant ya
/// tiene alguna Conversación, y <see cref="DatosPruebaSeeder"/> siembra antes dos (las de las reclamaciones documentales): en el
/// Tenant de la demo nunca llegaba a correr. Esta no mira «si hay alguna» sino cada conversación por su asunto (único y
/// determinista): un arranque nuevo las crea todas, una base ya sembrada completa las que falten y un segundo arranque no
/// duplica nada. No toca lo que la otra siembra deje.
/// </para>
///
/// <para>
/// <b>Nada sale hacia fuera.</b> Los tres canales sembrados (buzón compartido, buzón personal y línea de WhatsApp) quedan
/// <see cref="EstadoConexionIntegracion.Deshabilitada"/> y sin credencial ni suscripción: ninguna pasada de ingesta, renovación,
/// verificación de identidad ni recuperación del hueco toca una Deshabilitada (ver cada una en el informe de la PR), y enviar
/// o leer desde ella se rechaza. Todos los mensajes salientes son históricos: no hay cola de envío que los recoja. Las
/// conversaciones Críticas nacen con su aviso ya confirmado, así que el reintento de avisos tampoco tiene nada pendiente.
/// </para>
///
/// <para>
/// <b>Guarda.</b> La misma que el resto de siembras de demo: solo con <c>DatosPrueba:Activo</c>. Respeta RLS: escribe con el
/// contexto y el ámbito de Tenant del llamador, sin abrir ningún otro.
/// </para>
/// </summary>
public static class ComunicacionesDemoCompletaSeeder
{
    private const string DominioContactos = "example";
    private const string BuzonCompartidoEmail = "equipo-cae.demo@buzon-simulado.example";
    private const string BuzonPersonalEmail = "gestor.uno.demo@buzon-simulado.example";
    private const string LineaWhatsAppNumero = "+34600000101";
    private const string DirectorioDemo = "11111111-2222-3333-4444-555555555555";
    private const string NombrePdfDivisible = "expediente-trabajadores-escaneado.pdf";

    private enum Asignacion { GestorDeLaDemo, OtroGestor, SinAsignar }

    private enum Buzon { Compartido, Personal, WhatsApp }

    private enum Reparto { PdfDivisible, ListoParaAsignar, SinDestino, Asignado, Rechazado, Descartado, Dividido }

    /// <param name="Empresa">Índice de la Empresa cliente (módulo el número que haya); −1 si el remitente no está identificado.</param>
    /// <param name="PlazoHoras">
    /// Dónde debe caer el vencimiento del plazo respecto de ahora, en horas (negativo, ya vencido); null si el hilo no espera
    /// respuesta nuestra (su último mensaje es saliente) y por tanto no corre plazo. El primer entrante se elige con la regla real.
    /// </param>
    /// <param name="HorasDesdeUltimo">Solo sin plazo: horas desde el último mensaje.</param>
    private sealed record Hilo(
        string Asunto, int Empresa, CanalConversacion Canal, EstadoConversacion Estado, PrioridadConversacion Prioridad,
        Asignacion Asignado, Buzon Buzon, double? PlazoHoras, double HorasDesdeUltimo = 6, string? Nota = null,
        Reparto[]? Adjuntos = null, bool ReclamacionEnviada = false);

    // La lista es el contrato de la siembra: cada asunto es la marca de idempotencia de su conversación.
    private static readonly Hilo[] Hilos =
    [
        new("Accidente en obra: acceso suspendido hasta aportar la documentación", 0, CanalConversacion.Correo, EstadoConversacion.Abierta,
            PrioridadConversacion.Critica, Asignacion.GestorDeLaDemo, Buzon.Compartido, -3,
            Nota: "He llamado al Centro: mantienen el acceso suspendido hasta recibir el certificado de aptitud."),
        new("Entrada de maquinaria pesada mañana a primera hora", 1, CanalConversacion.Correo, EstadoConversacion.Abierta,
            PrioridadConversacion.Critica, Asignacion.OtroGestor, Buzon.Compartido, 1.25),
        new("Cambio de turno de cuatro operarios esta tarde", 2, CanalConversacion.Correo, EstadoConversacion.Abierta,
            PrioridadConversacion.Urgente, Asignacion.GestorDeLaDemo, Buzon.Personal, 1.5),
        new("Expedientes escaneados de la subcontrata para revisar", 0, CanalConversacion.Correo, EstadoConversacion.Pendiente,
            PrioridadConversacion.Urgente, Asignacion.GestorDeLaDemo, Buzon.Compartido, 3,
            Adjuntos: [Reparto.PdfDivisible, Reparto.ListoParaAsignar]),
        new("Documentación pendiente de renovar antes de la parada", 3, CanalConversacion.Correo, EstadoConversacion.Abierta,
            PrioridadConversacion.Normal, Asignacion.SinAsignar, Buzon.Compartido, -20,
            Adjuntos: [Reparto.SinDestino]),
        new("Duda sobre el requisito de formación PRL del puesto", 1, CanalConversacion.Correo, EstadoConversacion.Abierta,
            PrioridadConversacion.Normal, Asignacion.OtroGestor, Buzon.Compartido, 24,
            Nota: "Consulta de criterio: lo hablo con el equipo de prevención antes de contestar."),
        new("Solicitud de certificado de empresa", 2, CanalConversacion.Correo, EstadoConversacion.EsperandoRespuestaExterna,
            PrioridadConversacion.Normal, Asignacion.GestorDeLaDemo, Buzon.Compartido, null, 30, ReclamacionEnviada: true),
        new("Pendiente de la visita del Centro para validar el acceso", 0, CanalConversacion.Correo, EstadoConversacion.EnEsperaDeTercero,
            PrioridadConversacion.Normal, Asignacion.GestorDeLaDemo, Buzon.Compartido, null, 52,
            Nota: "Esperando al responsable del Centro; le insisto el jueves."),
        new("Alta de trabajador nuevo, documentación completa", 3, CanalConversacion.Correo, EstadoConversacion.Resuelta,
            PrioridadConversacion.Normal, Asignacion.OtroGestor, Buzon.Compartido, null, 20,
            Adjuntos: [Reparto.Asignado]),
        new("Baja de trabajador en el Centro", 1, CanalConversacion.Correo, EstadoConversacion.Cerrada,
            PrioridadConversacion.Normal, Asignacion.GestorDeLaDemo, Buzon.Compartido, null, 150,
            Adjuntos: [Reparto.Rechazado, Reparto.Descartado]),
        new("Confirmación de la visita programada del viernes", 2, CanalConversacion.Correo, EstadoConversacion.EsperandoRespuestaExterna,
            PrioridadConversacion.Urgente, Asignacion.OtroGestor, Buzon.Compartido, null, 9, ReclamacionEnviada: true),
        new("Contrato de la subcontrata autorizada", 0, CanalConversacion.Correo, EstadoConversacion.Pendiente,
            PrioridadConversacion.Normal, Asignacion.SinAsignar, Buzon.Personal, 30,
            Adjuntos: [Reparto.Dividido]),
        new("Consulta de un remitente que no figura en la cartera", -1, CanalConversacion.Correo, EstadoConversacion.Abierta,
            PrioridadConversacion.Normal, Asignacion.SinAsignar, Buzon.Compartido, 5),
        new("WhatsApp · Validación del equipo que entra el lunes", 0, CanalConversacion.WhatsApp, EstadoConversacion.Abierta,
            PrioridadConversacion.Normal, Asignacion.GestorDeLaDemo, Buzon.WhatsApp, 12),
        new("WhatsApp · Listado de trabajadores para la parada", 1, CanalConversacion.WhatsApp, EstadoConversacion.Pendiente,
            PrioridadConversacion.Urgente, Asignacion.OtroGestor, Buzon.WhatsApp, null, 40),
        new("WhatsApp · Contacto sin identificar pide un acceso", -1, CanalConversacion.WhatsApp, EstadoConversacion.Abierta,
            PrioridadConversacion.Normal, Asignacion.SinAsignar, Buzon.WhatsApp, 8),
    ];

    private static readonly string[] CuerposEntrantes =
    [
        "Buenos días, necesitamos que nos confirméis cuanto antes cómo queda este asunto.",
        "Os escribimos para aclarar la documentación que nos habéis pedido; adjuntamos lo que tenemos.",
        "¿Podéis indicarnos qué falta para que el acceso quede autorizado?",
    ];

    private static readonly string[] CuerposSalientes =
    [
        "Buenos días, lo hemos revisado y os confirmamos los siguientes pasos en breve.",
        "Gracias por el aviso. Hemos actualizado el expediente; cualquier duda, quedamos a vuestra disposición.",
    ];

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IFileStorageService? almacen,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo"))
        {
            logger.LogInformation("DatosPrueba:Activo no está activado — no se siembra el conjunto completo de Comunicaciones.");
            return;
        }

        // Solo los Gestores CAE del Tenant propietario de la demo: GetUsersInRoleAsync devuelve todas las cuentas que la RLS deja ver
        // (también las de un Operador CAE externo con asignación), y esas no son «el Gestor CAE de la demo».
        var tenantDeLaDemo = AmbitoTenantExplicito.TenantIdActual;
        var gestores = (await userManager.GetUsersInRoleAsync(Roles.GestorCae))
            .Where(g => tenantDeLaDemo is null || g.TenantId == tenantDeLaDemo)
            .OrderBy(g => g.Email).ToList();
        await SembrarEnTenantActualAsync(dbContext, gestores, almacen, logger, cancellationToken);
    }

    /// <summary>Siembra en el ámbito de Tenant actual (el llamador lo fija) lo que falte; no duplica nada.</summary>
    internal static async Task SembrarEnTenantActualAsync(
        CaeManagerDbContext dbContext, IReadOnlyList<ApplicationUser> gestores, IFileStorageService? almacen,
        ILogger logger, CancellationToken cancellationToken)
    {
        var empresas = await ComunicacionesDatosPruebaSeeder.EmpresasClienteDeDemoAsync(dbContext, cancellationToken);
        if (gestores.Count == 0 || empresas.Count < 3)
        {
            logger.LogInformation(
                "Faltan Gestores CAE o Empresas cliente de la demo — se omite el conjunto completo de Comunicaciones (ejecuta DatosPruebaSeeder primero).");
            return;
        }

        // Marca de idempotencia de cada conversación: su asunto (correo) o el teléfono del contacto (WhatsApp, cuyo asunto fija el
        // dominio). Ambos son deterministas y propios de esta siembra.
        var asuntos = Hilos.Where(h => h.Canal == CanalConversacion.Correo).Select(h => h.Asunto).ToList();
        var telefonos = Hilos.Where(h => h.Canal == CanalConversacion.WhatsApp).Select(h => TelefonoDe(h)).ToList();
        var asuntosExistentes = (await dbContext.Conversaciones.Where(c => asuntos.Contains(c.Asunto))
            .Select(c => c.Asunto).ToListAsync(cancellationToken)).ToHashSet();
        var telefonosExistentes = (await dbContext.Conversaciones
            .Where(c => c.Canal == CanalConversacion.WhatsApp && c.TelefonoContacto != null && telefonos.Contains(c.TelefonoContacto))
            .Select(c => c.TelefonoContacto!).ToListAsync(cancellationToken)).ToHashSet();
        var pendientes = Hilos
            .Where(h => h.Canal == CanalConversacion.Correo ? !asuntosExistentes.Contains(h.Asunto) : !telefonosExistentes.Contains(TelefonoDe(h)))
            .ToList();

        var conexiones = await SembrarCanalesAsync(dbContext, gestores[0], cancellationToken);
        if (pendientes.Count == 0)
        {
            logger.LogInformation("El conjunto completo de Comunicaciones ya estaba sembrado.");
            return;
        }

        var ahora = DateTime.UtcNow;
        var calendario = new CalendarioLaboralOperadorCae([]);
        var contexto = await CargarReferenciasAsync(dbContext, cancellationToken);
        var gestorDeLaDemo = gestores[0];
        var otroGestor = gestores.Count > 1 ? gestores[1] : gestores[0];

        var claveSubida = new List<string>();
        try
        {
            foreach (var hilo in pendientes)
            {
                var empresa = hilo.Empresa < 0 ? null : empresas[hilo.Empresa % empresas.Count];
                var conversacion = await ConstruirAsync(
                    hilo, empresa, conexiones, gestorDeLaDemo, otroGestor, contexto, almacen, claveSubida, ahora, calendario, cancellationToken);
                dbContext.Conversaciones.Add(conversacion);

                if (hilo.Nota is not null)
                {
                    var fechaNota = conversacion.FechaUltimoMensajeUtc.AddMinutes(20);
                    dbContext.NotasInternasConversacion.Add(new NotaInternaConversacion(
                        conversacion.Id, gestorDeLaDemo.Id, hilo.Nota, fechaNota < ahora ? fechaNota : ahora));
                }

                if (hilo.ReclamacionEnviada && empresa is not null && contexto.DocumentoIds.Count > 0)
                {
                    var fechaEnvio = conversacion.Mensajes.Min(m => m.FechaUtc);
                    var reclamacion = ReclamacionDocumental.ParaCliente(
                        empresa.Id, gestorDeLaDemo.Id, EmailDe(empresa), fechaEnvio, contexto.DocumentoIds, conversacion.Id);
                    dbContext.ReclamacionesDocumentales.Add(reclamacion);
                    // La entrada del timeline que en producción registra el manejador del envío (como en DatosPruebaSeeder).
                    dbContext.EventosConversacion.Add(new EventoConversacion(
                        conversacion.Id, TipoEventoConversacion.ReclamacionEnviada, reclamacion.Id, fechaEnvio));
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (almacen is not null)
                foreach (var clave in claveSubida)
                {
                    try
                    {
                        await almacen.EliminarAsync(clave, CancellationToken.None);
                    }
                    catch (Exception limpieza)
                    {
                        // La limpieza no debe esconder el error original de la siembra.
                        logger.LogWarning(limpieza, "No se pudo borrar del almacén {Clave} tras el fallo de la siembra.", clave);
                    }
                }
            throw;
        }

        logger.LogInformation(
            "Comunicaciones de la demo sembradas: {Conversaciones} conversaciones nuevas (de {Total}), con buzones Deshabilitados y sin envío alguno.",
            pendientes.Count, Hilos.Length);
    }

    /// <param name="Personal">null si el Gestor CAE de la demo ya tiene otro buzón personal: los hilos personales van al compartido.</param>
    private sealed record Conexiones(Guid Compartido, Guid? Personal, Guid WhatsApp);

    private sealed record Referencias(Guid? TrabajadorId, Guid? TipoDocumentoId, IReadOnlyList<Guid> DocumentoIds, Guid? DocumentoId);

    private static async Task<Referencias> CargarReferenciasAsync(CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var trabajador = await dbContext.Trabajadores.OrderBy(t => t.Dni).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(cancellationToken);
        var tipo = await dbContext.TiposDocumento.Where(t => t.Nombre == "Entrega de EPI")
            .Select(t => (Guid?)t.Id).FirstOrDefaultAsync(cancellationToken);
        var documentos = await dbContext.Documentos.OrderBy(d => d.CreadoEnUtc).ThenBy(d => d.Id)
            .Select(d => d.Id).Take(3).ToListAsync(cancellationToken);
        return new Referencias(trabajador, tipo, documentos, documentos.Count > 0 ? documentos[0] : null);
    }

    /// <summary>
    /// Los tres canales, siempre Deshabilitados: sin credencial, sin suscripción y sin marca de hueco. Se buscan por su
    /// dirección, así que un segundo arranque (o una base ya sembrada) los reutiliza.
    /// </summary>
    private static async Task<Conexiones> SembrarCanalesAsync(
        CaeManagerDbContext dbContext, ApplicationUser gestorTitular, CancellationToken cancellationToken)
    {
        async Task<ConexionIntegracion> AsegurarAsync(string direccion, Func<ConexionIntegracion> crear)
        {
            var existente = await dbContext.ConexionesIntegracion.FirstOrDefaultAsync(c => c.BuzonEmail == direccion, cancellationToken);
            if (existente is not null)
            {
                // Una dirección propia de esta siembra nunca debe quedar habilitada (por ejemplo, si alguien pulsó Reactivar).
                if (existente.Estado != EstadoConexionIntegracion.Deshabilitada)
                    existente.Deshabilitar();
                return existente;
            }

            var nueva = crear();
            nueva.Deshabilitar();
            dbContext.ConexionesIntegracion.Add(nueva);
            return nueva;
        }

        var compartido = await AsegurarAsync(BuzonCompartidoEmail, () =>
        {
            var buzon = ConexionIntegracion.CrearBuzonCompartido(
                BuzonCompartidoEmail, "Buzón compartido del equipo CAE (demo)", DirectorioDemo, gestorTitular.Id);
            buzon.FijarResponsable(gestorTitular.Id);
            return buzon;
        });
        // Hay un único buzón personal por Gestor CAE (índice único): si el de la demo ya tiene otro (el de la otra siembra, o uno
        // conectado a mano), no se crea un segundo —tumbaría el arranque— ni se cuelgan conversaciones de un buzón que no es
        // nuestro: los hilos «personales» van entonces al buzón compartido.
        var personalExistente = await dbContext.ConexionesIntegracion
            .Where(c => c.GestorPropietarioId == gestorTitular.Id && c.BuzonEmail != BuzonPersonalEmail)
            .AnyAsync(cancellationToken);
        var personal = personalExistente
            ? null
            : await AsegurarAsync(BuzonPersonalEmail, () => new ConexionIntegracion(
                BuzonPersonalEmail, "Buzón personal de Prueba GestorCae 1 (demo)", null, ProveedorIntegracion.Microsoft365, gestorTitular.Id));
        var whatsApp = await AsegurarAsync(LineaWhatsAppNumero, () => new ConexionIntegracion(
            LineaWhatsAppNumero, "Línea WhatsApp de demo (sin conectar)", null, ProveedorIntegracion.WhatsApp));

        await dbContext.SaveChangesAsync(cancellationToken);
        return new Conexiones(compartido.Id, personal?.Id, whatsApp.Id);
    }

    private static async Task<Conversacion> ConstruirAsync(
        Hilo hilo, Empresa? empresa, Conexiones conexiones, ApplicationUser gestorDeLaDemo, ApplicationUser otroGestor,
        Referencias referencias, IFileStorageService? almacen, List<string> claveSubida, DateTime ahora,
        CalendarioLaboralOperadorCae calendario, CancellationToken cancellationToken)
    {
        var posicion = Array.IndexOf(Hilos, hilo);
        var esWhatsApp = hilo.Canal == CanalConversacion.WhatsApp;
        var interlocutor = esWhatsApp ? TelefonoDe(hilo) : empresa is null ? $"contacto@dominio-desconocido.{DominioContactos}" : EmailDe(empresa);
        var enPersonal = hilo.Buzon == Buzon.Personal && conexiones.Personal is not null;
        var propio = esWhatsApp ? LineaWhatsAppNumero : enPersonal ? BuzonPersonalEmail : BuzonCompartidoEmail;
        var etiquetas = hilo.Prioridad == PrioridadConversacion.Normal ? null : "urgente";

        var conversacion = esWhatsApp
            ? Conversacion.CrearWhatsApp(interlocutor, conexiones.WhatsApp, empresa?.Id, null)
            : new Conversacion(hilo.Asunto, empresa?.Id, etiquetas);
        if (esWhatsApp)
            ConGestor(conversacion, hilo, gestorDeLaDemo, otroGestor);

        var entrantePrimero = hilo.PlazoHoras is { } horas
            ? FechaEntranteParaVencer(hilo.Prioridad, ahora, TimeSpan.FromHours(horas), calendario)
            : ahora.AddHours(-hilo.HorasDesdeUltimo - 2);

        var fechas = new List<(DireccionMensaje Direccion, DateTime Fecha)>();
        if (hilo.PlazoHoras is not null)
        {
            // Un intercambio previo ya contestado y, al final, el entrante que corre el plazo.
            fechas.Add((DireccionMensaje.Entrante, entrantePrimero.AddDays(-2)));
            fechas.Add((DireccionMensaje.Saliente, entrantePrimero.AddDays(-2).AddHours(3)));
            fechas.Add((DireccionMensaje.Entrante, entrantePrimero));
        }
        else
        {
            fechas.Add((DireccionMensaje.Entrante, entrantePrimero));
            fechas.Add((DireccionMensaje.Saliente, entrantePrimero.AddHours(2)));
        }

        Mensaje? primerEntrante = null;
        for (var m = 0; m < fechas.Count; m++)
        {
            var (direccion, fecha) = fechas[m];
            var entrante = direccion == DireccionMensaje.Entrante;
            var mensaje = conversacion.AgregarMensaje(
                direccion, hilo.Canal, entrante ? interlocutor : propio,
                $"<p>{(entrante ? CuerposEntrantes[m % CuerposEntrantes.Length] : CuerposSalientes[m % CuerposSalientes.Length])}</p>", fecha);
            if (entrante)
                primerEntrante ??= mensaje;
            else if (esWhatsApp)
                mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Leido);
            else
                mensaje.ActualizarEstadoEntrega(EstadoEntregaMensaje.Enviado);
        }

        conversacion.AlinearFechaConLosMensajes();
        if (!esWhatsApp)
        {
            ConGestor(conversacion, hilo, gestorDeLaDemo, otroGestor);
            conversacion.AgregarParticipante(interlocutor, RolParticipante.De, TipoParticipanteOrigen.UsuarioCliente);
            conversacion.AgregarParticipante(propio, RolParticipante.Para, TipoParticipanteOrigen.Desconocido);
            conversacion.AsociarConexion(enPersonal ? conexiones.Personal!.Value : conexiones.Compartido, $"demo-comunicaciones-{posicion:D2}");
        }

        if (hilo.Prioridad != PrioridadConversacion.Normal)
        {
            conversacion.CambiarPrioridad(hilo.Prioridad);
            // El aviso de una Crítica ya está dado: el reintento de avisos no encuentra nada pendiente.
            if (conversacion.AvisoCriticaPendienteId is { } aviso)
                conversacion.ConfirmarAvisoCritica(aviso);
        }

        if (hilo.Estado != EstadoConversacion.Abierta)
        {
            var momento = conversacion.FechaUltimoMensajeUtc.AddMinutes(10);
            var resultado = conversacion.CambiarEstado(hilo.Estado, momento < ahora ? momento : ahora);
            if (!resultado.EsExitoso)
                throw new InvalidOperationException($"La siembra de Comunicaciones pidió una transición de estado no permitida: {hilo.Estado}.");
        }

        if (primerEntrante is not null && hilo.Adjuntos is { } reparto)
            foreach (var tipo in reparto)
                await AgregarAdjuntoAsync(primerEntrante, tipo, gestorDeLaDemo, referencias, almacen, claveSubida, ahora, cancellationToken);

        return conversacion;
    }

    private static void ConGestor(Conversacion conversacion, Hilo hilo, ApplicationUser gestorDeLaDemo, ApplicationUser otroGestor) =>
        conversacion.Asignar(hilo.Asignado switch
        {
            Asignacion.GestorDeLaDemo => gestorDeLaDemo.Id,
            Asignacion.OtroGestor => otroGestor.Id,
            _ => (Guid?)null,
        });

    private static async Task AgregarAdjuntoAsync(
        Mensaje mensaje, Reparto reparto, ApplicationUser gestor, Referencias referencias, IFileStorageService? almacen,
        List<string> claveSubida, DateTime ahora, CancellationToken cancellationToken)
    {
        var (nombre, tipoContenido) = reparto switch
        {
            Reparto.PdfDivisible => (NombrePdfDivisible, "application/pdf"),
            Reparto.ListoParaAsignar => ("epis-firmadas.jpg", "image/jpeg"),
            Reparto.SinDestino => ("documento-sin-identificar.pdf", "application/pdf"),
            Reparto.Asignado => ("apto-medico.pdf", "application/pdf"),
            Reparto.Rechazado => ("seguro-caducado.pdf", "application/pdf"),
            Reparto.Descartado => ("firma-del-correo.png", "image/png"),
            _ => ("contrato-subcontrata.pdf", "application/pdf"),
        };

        // Cada adjunto tiene su contenido en el almacén (mínimo pero real): asignarlo o descargarlo desde la pantalla no falla. Sin
        // almacén (un test que no lo pasa) queda solo la referencia.
        var contenido = reparto == Reparto.PdfDivisible
            ? ComunicacionesDemoPdf.Generar(Enumerable.Range(1, 6).Select(i => $"Expediente escaneado · página {i} de 6 · demo").ToList())
            : ContenidoMinimo(nombre);
        var clave = $"adjuntos-demo/{nombre}";
        if (almacen is not null)
        {
            using var flujo = new MemoryStream(contenido, writable: false);
            clave = await almacen.GuardarAsync(flujo, nombre, cancellationToken);
            claveSubida.Add(clave);
        }

        var adjunto = mensaje.AgregarAdjunto(nombre, tipoContenido, contenido.Length, clave);
        if (reparto == Reparto.PdfDivisible)
            return;

        switch (reparto)
        {
            case Reparto.ListoParaAsignar when referencias.TrabajadorId is not null && referencias.TipoDocumentoId is not null:
                adjunto.FijarDestino(referencias.TrabajadorId, null, referencias.TipoDocumentoId);
                break;
            case Reparto.Asignado when referencias.DocumentoId is { } documento:
                adjunto.MarcarAsignado(documento, gestor.Id, ahora);
                break;
            case Reparto.Rechazado:
                adjunto.Rechazar("El seguro está caducado y no se puede aceptar.", gestor.Id, ahora);
                break;
            case Reparto.Descartado:
                adjunto.Descartar(gestor.Id, ahora);
                break;
            case Reparto.Dividido:
                adjunto.MarcarDividido(gestor.Id, ahora);
                break;
        }
    }

    /// <summary>
    /// El instante del primer entrante sin respuesta para que, con la regla real del plazo
    /// (<see cref="PlazosConversacionService.Vencimiento"/>, calendario sin festivos), el vencimiento caiga lo más cerca posible
    /// de ahora más <paramref name="objetivo"/>. Se busca hacia atrás de cinco en cinco minutos hasta dos semanas.
    /// </summary>
    // shortcut: fuera de jornada laboral el vencimiento más próximo a «por vencer en menos de 2 h» puede quedar más lejos del
    // objetivo (o ya vencido); el día que la demo deba garantizarlo a cualquier hora, fijar el reloj en la siembra.
    internal static DateTime FechaEntranteParaVencer(
        PrioridadConversacion prioridad, DateTime ahora, TimeSpan objetivo, CalendarioLaboralOperadorCae calendario)
    {
        var meta = new DateTimeOffset(DateTime.SpecifyKind(ahora, DateTimeKind.Utc)) + objetivo;
        var mejor = ahora.AddMinutes(-5);
        var mejorDistancia = TimeSpan.MaxValue;
        for (var minutos = 5; minutos <= 14 * 24 * 60; minutos += 5)
        {
            var candidato = DateTime.SpecifyKind(ahora.AddMinutes(-minutos), DateTimeKind.Utc);
            var distancia = (PlazosConversacionService.Vencimiento(prioridad, new DateTimeOffset(candidato), calendario) - meta).Duration();
            if (distancia < mejorDistancia)
            {
                mejor = candidato;
                mejorDistancia = distancia;
            }
        }

        return mejor;
    }

    private const string JpegMinimoBase64 =
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=";

    private const string PngMinimoBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    /// <summary>Un archivo válido y mínimo del tipo que dice su nombre: un PDF de una página, un JPEG o un PNG de un píxel.</summary>
    private static byte[] ContenidoMinimo(string nombre) => Path.GetExtension(nombre).ToLowerInvariant() switch
    {
        ".jpg" => Convert.FromBase64String(JpegMinimoBase64),
        ".png" => Convert.FromBase64String(PngMinimoBase64),
        _ => ComunicacionesDemoPdf.Generar([$"{nombre} · documento de demostración"]),
    };

    /// <summary>El teléfono del contacto de un hilo de WhatsApp: propio de la siembra y distinto por hilo (es su marca de idempotencia).</summary>
    private static string TelefonoDe(Hilo hilo) => $"+3460022200{Array.IndexOf(Hilos, hilo) % 10}";

    private static string EmailDe(Empresa empresa)
    {
        var slug = new string(empresa.RazonSocial.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return $"contacto@{(slug.Length > 20 ? slug[..20] : slug)}.{DominioContactos}";
    }
}
