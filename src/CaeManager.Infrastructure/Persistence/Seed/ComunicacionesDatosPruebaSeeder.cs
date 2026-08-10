using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
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

        var conversacionesConCliente = await dbContext.Conversaciones
            .Where(c => c.ClienteId != null)
            .OrderBy(c => c.Asunto)
            .ToListAsync(cancellationToken);

        var resumenBpo = await SembrarMetricasBpoAsync(
            dbContext, conversacionesConCliente, centros, trabajadores,
            gestoresPrueba.Select(g => g.Id).ToList(), aleatorio, ahora, cancellationToken);

        logger.LogInformation(
            "Comunicaciones sembradas: {Conversaciones} conversaciones ({Triage} sin cliente asignado), {Macros} macros de respuesta, " +
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
