using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Telemetria;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
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

        // F3b — Empresas, no la tabla legacy Clientes: DatosPruebaSeeder ya
        // crea los clientes de demo ahí.
        var clientes = await dbContext.Empresas
            .Where(e => e.EsCritico != null)
            .OrderBy(e => e.RazonSocial).Take(40).ToListAsync(cancellationToken);
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
        await SembrarReduccionRuidoAsync(dbContext, cancellationToken);
        await SembrarConexionesMicrosoft365Async(dbContext, gestoresPrueba, ahora, cancellationToken);

        var conversacionesConCliente = await dbContext.Conversaciones
            .Where(c => c.ClienteId != null)
            .OrderBy(c => c.Asunto)
            .ToListAsync(cancellationToken);

        var resumenBpo = await SembrarMetricasBpoAsync(
            dbContext, conversacionesConCliente, centros, trabajadores,
            gestoresPrueba.Select(g => g.Id).ToList(), aleatorio, ahora, cancellationToken);

        logger.LogInformation(
            "Comunicaciones sembradas: {Conversaciones} conversaciones de correo ({Triage} sin cliente asignado), " +
            "{Macros} macros, líneas y conversaciones WhatsApp, adjuntos, eventos y sugerencias de bandeja, " +
            "{Visitas} visitas con antelación medida, {Tramos} tramos de tiempo de gestión, {Sugerencias} sugerencias resueltas.",
            totalConversaciones, conversacionesTriage, macros.Count,
            resumenBpo.Visitas, resumenBpo.Tramos, resumenBpo.Sugerencias);
    }

    private record ResumenBpo(int Visitas, int Tramos, int Sugerencias);

    /// <summary>
    /// Datos del Dashboard BPO (requerimiento global nº 1: toda variante ejercitable de
    /// inicio a fin). Cubre a propósito <b>todos</b> los valores de cada enumeración
    /// implicada, no una muestra: los tres <see cref="TramoAntelacion"/>, las tres
    /// <see cref="AtribucionUrgencia"/> que hoy tienen emisor, los cinco
    /// <see cref="MotivoCierreSesionGestion"/> y las tres <see cref="ResolucionSugerencia"/>.
    /// Así ningún KPI sale vacío, ni al 0 %, ni al 100 % por falta de datos.
    /// </summary>
    private static async Task<ResumenBpo> SembrarMetricasBpoAsync(
        CaeManagerDbContext dbContext,
        IReadOnlyList<Conversacion> conversaciones,
        IReadOnlyList<Centro> centros,
        IReadOnlyList<Trabajador> trabajadores,
        IReadOnlyList<Guid> gestorIds,
        Random aleatorio,
        DateTime ahora,
        CancellationToken cancellationToken)
    {
        if (conversaciones.Count == 0 || centros.Count == 0 || trabajadores.Count == 0)
            return new ResumenBpo(0, 0, 0);

        // Dentro del mes en curso: es el período que agregan los KPIs. Se ancla al día 2
        // para que las restas de horas no se salgan del mes en los primeros días.
        var inicioMes = new DateTime(ahora.Year, ahora.Month, 2, 8, 0, 0, DateTimeKind.Utc);
        var visitasSembradas = 0;

        // (horas de aviso del cliente, horas que realmente tuvo el gestor, hora de entrada)
        // Los tres primeros son los tres tramos con su atribución natural; el cuarto es el
        // caso de la propuesta: aviso el lunes 09:00 para el jueves 08:00 con el último
        // documento entrando el miércoles a las 17:00 — 71 h nominales, 15 h efectivas.
        (int Nominal, int Efectiva, TimeOnly? Hora)[] casos =
        [
            (168, 120, new TimeOnly(9, 0)),   // Estándar   · sin urgencia
            (168, 36, new TimeOnly(9, 0)),    // Urgente    · documentación tardía
            (20, 18, new TimeOnly(9, 0)),     // Exprés     · solicitud tardía
            (71, 15, new TimeOnly(8, 0)),     // Exprés     · documentación tardía (caso Refrielectric)
            (168, 120, new TimeOnly(6, 30)),  // Exprés por entrada fuera de jornada
            (168, 120, null)                  // Sin hora: ejercita el respaldo de la apertura de jornada
        ];

        for (var i = 0; i < casos.Length && i < conversaciones.Count; i++)
        {
            var (nominal, efectiva, hora) = casos[i];
            var conversacion = conversaciones[i];
            var centro = centros.FirstOrDefault(c => c.ClienteId == conversacion.ClienteId) ?? centros[i % centros.Count];

            var fechaEntrada = inicioMes.AddDays(i + 1);
            var visita = new Visita(
                centro.Id, DateOnly.FromDateTime(fechaEntrada), DateOnly.FromDateTime(fechaEntrada),
                "Visita de prueba con antelación medida.", OrigenVisita.Correo, hora);

            var momentoEntrada = visita.ObtenerFechaHoraEntrada(ParametroSistemaSeedData.HoraInicioJornada);
            var solicitud = momentoEntrada.AddHours(-nominal);
            var expedienteCompleto = momentoEntrada.AddHours(-efectiva);

            visita.RegistrarOrigenSolicitud(conversacion.Id, solicitud);
            visita.MarcarExpedienteCompleto(
                expedienteCompleto,
                CalculadoraAntelacionVisita.Calcular(
                    momentoEntrada, solicitud, expedienteCompleto,
                    ParametroSistemaSeedData.HorasAvisoVisita, ParametroSistemaSeedData.HorasCriticasVisita,
                    ParametroSistemaSeedData.HoraInicioJornada, ParametroSistemaSeedData.HoraFinJornada));

            dbContext.Visitas.Add(visita);
            dbContext.VisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, trabajadores[i % trabajadores.Count].Id));
            visitasSembradas++;
        }

        var tramos = SembrarTiempoDeGestion(dbContext, conversaciones, gestorIds, aleatorio, inicioMes, ahora);
        var sugerencias = await SembrarResolucionesDeSugerenciaAsync(dbContext, conversaciones, centros, ahora, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new ResumenBpo(visitasSembradas, tramos, sugerencias);
    }

    /// <summary>
    /// Reparto deliberadamente desigual entre gestores: con todos al mismo nivel, el KPI
    /// de ocupación no enseñaría lo único que se le pide — quién está por encima de su
    /// jornada y quién por debajo.
    ///
    /// Los tramos se esparcen por lo ya transcurrido del mes en vez de encadenarse uno
    /// tras otro: encadenándolos, la suma de duraciones más las pausas desbordaba el mes
    /// y dejaba tramos con fecha futura, que es justo lo que un KPI acotado al mes en
    /// curso no debe contener.
    /// </summary>
    private static int SembrarTiempoDeGestion(
        CaeManagerDbContext dbContext,
        IReadOnlyList<Conversacion> conversaciones,
        IReadOnlyList<Guid> gestorIds,
        Random aleatorio,
        DateTime inicioMes,
        DateTime ahora)
    {
        if (gestorIds.Count == 0) return 0;

        var motivos = Enum.GetValues<MotivoCierreSesionGestion>();

        // Minutos de dedicación en lo que va de mes por gestor: el primero desbordado
        // (por encima de las 160 h de jornada), el segundo en carga razonable, el
        // tercero infrautilizado.
        int[] minutosObjetivo = [11_000, 6_000, 1_200];

        // Margen para que ningún tramo acabe en el futuro ni antes del inicio del mes.
        var minutosDeMargen = (int)(ahora - inicioMes).TotalMinutes;
        if (minutosDeMargen <= 120) return 0;

        var tramos = 0;

        for (var g = 0; g < gestorIds.Count; g++)
        {
            var restantes = minutosObjetivo[g % minutosObjetivo.Length];

            while (restantes > 0)
            {
                var minutos = Math.Min(restantes, aleatorio.Next(20, 90));
                restantes -= minutos;

                var inicio = inicioMes.AddMinutes(aleatorio.Next(0, minutosDeMargen - minutos));
                var fin = inicio.AddMinutes(minutos);

                var conversacion = conversaciones[tramos % conversaciones.Count];

                dbContext.RegistrosTiempoGestion.Add(new RegistroTiempoGestion(
                    conversacion.Id, gestorIds[g], conversacion.ClienteId,
                    inicio, fin, minutos * 60,
                    motivos[tramos % motivos.Length]));

                tramos++;
            }
        }

        return tramos;
    }

    /// <summary>
    /// Sugerencias ya resueltas en las tres formas posibles, para que el índice de palanca
    /// IA tenga numerador y denominador distintos de cero desde el primer arranque.
    /// </summary>
    private static async Task<int> SembrarResolucionesDeSugerenciaAsync(
        CaeManagerDbContext dbContext,
        IReadOnlyList<Conversacion> conversaciones,
        IReadOnlyList<Centro> centros,
        DateTime ahora,
        CancellationToken cancellationToken)
    {
        var mensajesEntrantes = await dbContext.Mensajes
            .Where(m => m.Direccion == DireccionMensaje.Entrante)
            .OrderBy(m => m.FechaUtc)
            .Take(24)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        if (mensajesEntrantes.Count == 0 || centros.Count == 0) return 0;

        // Proporción a ojo pero deliberada: mayoría confirmadas de un clic, unas cuantas
        // con edición y unas pocas descartadas — un índice de palanca creíble (~67 %), ni
        // perfecto ni catastrófico.
        ResolucionSugerencia[] reparto =
        [
            ResolucionSugerencia.Confirmada, ResolucionSugerencia.Confirmada, ResolucionSugerencia.Confirmada,
            ResolucionSugerencia.ConfirmadaConEdicion, ResolucionSugerencia.Descartada, ResolucionSugerencia.Confirmada
        ];

        var sembradas = 0;
        var fechaBase = new DateTime(ahora.Year, ahora.Month, 3, 10, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < mensajesEntrantes.Count; i++)
        {
            var fechaVisita = DateOnly.FromDateTime(fechaBase.AddDays(i + 1));
            var sugerencia = new SugerenciaVisitaCorreo(
                mensajesEntrantes[i], centros[i % centros.Count].Id, fechaVisita, fechaVisita,
                "Detección de prueba: el correo parece pedir agendar una entrada al centro.", 88, 90, 85);

            sugerencia.Resolver(reparto[i % reparto.Length]);
            dbContext.SugerenciasVisitaCorreo.Add(sugerencia);
            sembradas++;
        }

        return sembradas;
    }

    /// <summary>
    /// Ronda de reducción de ruido (Fases 6-8): una conversación "pre-CAE"
    /// (con Cliente pero sin gestión CAE accionable — se minimiza) y una ya
    /// congelada como accionable; más clasificaciones de ruido por mensaje —
    /// un resumen sin cambios de plataforma (minimizado), el mismo caso
    /// rescatado manualmente por el gestor, y una notificación automática
    /// que sí trae cambios (motivo Ninguno). Los motivos CorreoInterno y
    /// PosiblePhishing no se siembran: solo se calculan sobre mensajes de un
    /// buzón personal de gestor (ConexionIntegracion.GestorPropietarioId),
    /// que depende de la decisión abierta nº 2 de PLAN-DATOS-PRUEBA.md.
    /// </summary>
    private static async Task SembrarReduccionRuidoAsync(
        CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var conversacionesConCliente = await dbContext.Conversaciones
            .Where(c => c.ClienteId != null && c.Canal == CanalConversacion.Correo)
            .OrderBy(c => c.CreadoEnUtc).ThenBy(c => c.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (conversacionesConCliente.Count < 2)
            return;

        dbContext.ClasificacionesRelevanciaCae.Add(new ClasificacionRelevanciaCae(
            conversacionesConCliente[0].Id, esAccionableCae: false,
            "El cliente copia al gestor en una negociación comercial — todavía no hay ninguna gestión CAE que hacer.", 84));
        dbContext.ClasificacionesRelevanciaCae.Add(new ClasificacionRelevanciaCae(
            conversacionesConCliente[1].Id, esAccionableCae: true,
            "El último mensaje pide renovar la documentación de un trabajador.", 91));

        var mensajesEntrantes = await dbContext.Mensajes
            .Where(m => m.Direccion == DireccionMensaje.Entrante && m.Canal == CanalConversacion.Correo)
            .OrderByDescending(m => m.FechaUtc).ThenBy(m => m.Id)
            .Take(3)
            .ToListAsync(cancellationToken);
        if (mensajesEntrantes.Count < 3)
            return;

        var proveedorCae = await dbContext.ProveedoresPlataformaCae
            .Where(p => p.Activo).OrderBy(p => p.Codigo)
            .FirstOrDefaultAsync(cancellationToken);

        // Resumen periódico de plataforma sin cambios — se minimiza.
        dbContext.ClasificacionesRuidoMensaje.Add(new ClasificacionRuidoMensaje(
            mensajesEntrantes[0].Id, esNotificacionAutomatica: true, proveedorCae?.Id,
            MotivoRuidoMensaje.ResumenSinCambios));

        // El mismo patrón, pero el gestor confirmó que sí importa.
        var rescatada = new ClasificacionRuidoMensaje(
            mensajesEntrantes[1].Id, esNotificacionAutomatica: true, proveedorCae?.Id,
            MotivoRuidoMensaje.ResumenSinCambios);
        rescatada.ConfirmarManualmente();
        dbContext.ClasificacionesRuidoMensaje.Add(rescatada);

        // Notificación automática con cambios reales — no se minimiza.
        dbContext.ClasificacionesRuidoMensaje.Add(new ClasificacionRuidoMensaje(
            mensajesEntrantes[2].Id, esNotificacionAutomatica: true, proveedorCae?.Id));

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Conexiones Microsoft 365 simuladas — buzones ficticios sin credencial
    /// real (no hay CredencialIntegracion, así que ningún worker puede
    /// llamar a Graph con ellas): los 3 estados de conexión para
    /// /integraciones, más el buzón personal de un gestor
    /// (GestorPropietarioId) con una conversación cuyos dos mensajes cubren
    /// los motivos de ruido exclusivos de ese patrón — CorreoInterno (mismo
    /// dominio que el buzón propio) y PosiblePhishing (dominio ajeno).
    /// </summary>
    private static async Task SembrarConexionesMicrosoft365Async(
        CaeManagerDbContext dbContext, IReadOnlyList<ApplicationUser> gestoresPrueba,
        DateTime ahora, CancellationToken cancellationToken)
    {
        dbContext.ConexionesIntegracion.Add(new ConexionIntegracion(
            "equipo-cae@buzon-simulado.local", "Buzón compartido CAE (demo)"));

        var deshabilitada = new ConexionIntegracion("altas@buzon-simulado.local", "Buzón de altas (demo)");
        deshabilitada.Deshabilitar();
        dbContext.ConexionesIntegracion.Add(deshabilitada);

        var conError = new ConexionIntegracion("avisos@buzon-simulado.local", "Buzón de avisos (demo)");
        conError.MarcarConError("El token de Microsoft Graph caducó — hay que reconectar el buzón.");
        dbContext.ConexionesIntegracion.Add(conError);

        var gestorTitular = gestoresPrueba.OrderBy(g => g.Email).FirstOrDefault();
        if (gestorTitular is null)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        dbContext.ConexionesIntegracion.Add(new ConexionIntegracion(
            "gestor.uno@buzon-simulado.local", "Buzón personal de Prueba GestorCae 1",
            clienteId: null, gestorPropietarioId: gestorTitular.Id));

        // Conversación llegada por el buzón personal: un correo de un
        // compañero (mismo dominio → CorreoInterno) y uno de un dominio
        // ajeno sospechoso (→ PosiblePhishing). Ninguno se oculta — son
        // advertencias visuales, la decisión es del gestor.
        var conversacionPersonal = new Conversacion("Consulta interna y aviso sospechoso — buzón personal");
        conversacionPersonal.Asignar(gestorTitular.Id);

        var interno = conversacionPersonal.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, "companero@buzon-simulado.local",
            "<p>¿Me pasas el estado de la documentación de la contrata de climatización?</p>", ahora.AddHours(-8));
        var sospechoso = conversacionPersonal.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, "premios@sorteo-sospechoso.example",
            "<p>Ha sido seleccionado para un premio. Confirme sus credenciales en este enlace.</p>", ahora.AddHours(-5));

        conversacionPersonal.AgregarParticipante("companero@buzon-simulado.local", RolParticipante.De, TipoParticipanteOrigen.Desconocido);
        dbContext.Conversaciones.Add(conversacionPersonal);

        dbContext.ClasificacionesRuidoMensaje.Add(new ClasificacionRuidoMensaje(
            interno.Id, esNotificacionAutomatica: false, proveedorPlataformaCaeId: null,
            MotivoRuidoMensaje.CorreoInterno));
        dbContext.ClasificacionesRuidoMensaje.Add(new ClasificacionRuidoMensaje(
            sospechoso.Id, esNotificacionAutomatica: false, proveedorPlataformaCaeId: null,
            MotivoRuidoMensaje.PosiblePhishing));

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Canal WhatsApp completo: dos líneas simuladas (GestorFijo y
    /// PoolInbound), contactos aprendidos y conversaciones con estados de
    /// entrega variados. Los tokens son literales de demo, nunca credenciales
    /// reales — ninguna llamada sale hacia Meta sin webhook real configurado.
    /// </summary>
    private static async Task SembrarWhatsAppAsync(
        CaeManagerDbContext dbContext, IReadOnlyList<Empresa> clientes,
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
        // "Ya gestionada desde la bandeja" es una confirmación, no un descarte.
        sugerenciaResuelta.Resolver(ResolucionSugerencia.Confirmada);
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
            // Confianza baja en ambos campos: el ítem se aceptó tras corregir algo.
            detalleResuelto.Resolver(ResolucionSugerencia.ConfirmadaConEdicion);
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

    private static string EmailSimuladoDeCliente(Empresa cliente) => $"contacto@{Slug(cliente.RazonSocial)}.com";

    private static string Slug(string texto)
    {
        var normalizado = new string(texto.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return normalizado.Length > 20 ? normalizado[..20] : normalizado;
    }

    private static T ElementoAleatorio<T>(Random aleatorio, IReadOnlyList<T> lista) => lista[aleatorio.Next(lista.Count)];
}
