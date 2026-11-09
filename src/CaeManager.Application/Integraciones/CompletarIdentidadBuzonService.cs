using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Comprueba la identidad estable (<see cref="ConexionIntegracion.IdentidadBuzon"/>) de las conexiones de Microsoft 365
/// del Tenant actual, con el mismo token que las abre. Dos entradas, las dos desde servicios de fondo que ya recorren
/// cada Tenant con su ámbito explícito:
/// <list type="bullet">
/// <item><see cref="CompletarAsync"/>: el ciclo diario de la renovación de suscripciones, antes de renovar, sobre las que
/// no están Deshabilitadas. Se salta las Habilitadas verificadas hace menos de
/// <see cref="ConexionIntegracion.FrescuraSuficienteCicloDiario"/>: el reintento ya las cubre, y una conexión sana no se
/// verifica dos veces al día. La renovación de su suscripción no se salta.</item>
/// <item><see cref="ReintentarAsync"/>: el reintento acotado, cada pocos minutos, solo sobre las Habilitadas con la
/// verificación de más de <see cref="ConexionIntegracion.UmbralReintentoVerificacion"/> o sin verificar, para que un
/// fallo transitorio del ciclo diario no deje la conexión sin verificar hasta el día siguiente. Cada conexión se
/// reintenta como mucho cada <see cref="ConexionIntegracion.IntervaloReintentoVerificacion"/> más un margen aleatorio, y
/// nunca antes de lo que pidió Microsoft con un Retry-After.</item>
/// </list>
/// Las dos, y Reactivar, comprueban una conexión dentro de <see cref="ICerrojoVerificacionBuzon"/>: nunca hay dos
/// verificaciones simultáneas de la misma conexión, en ninguna réplica.
/// <list type="bullet">
/// <item>Coincide (o se acaba de completar): fija <see cref="ConexionIntegracion.IdentidadVerificadaUtc"/>. Si la
/// conexión estaba en ConError, se rehabilita aquí mismo, verificada en este ciclo; sus eventos
/// <see cref="EstadoEventoWebhook.Retenido"/> vuelven a la cola. El hueco que dejó la caída queda declarado en
/// <see cref="ConexionIntegracion.HuecoPendienteDesdeUtc"/>, que Conexiones avisa; aquí no se lee ni se limpia.</item>
/// <item>La verificación caducada no marca hueco: mientras la conexión está sin verificar, su correo espera en la cola.
/// Solo bloquea, y Conexiones lo muestra como «sin verificar».</item>
/// <item>Sin identidad (dada de alta antes de que existiera): se completa en la conexión y en su
/// <see cref="ReclamacionBuzonIntegracion"/>. Si choca con la de otra reclamación, el mismo buzón está conectado
/// en otro Tenant con otra dirección: esta conexión pasa a ConError (<see cref="MensajeBuzonDuplicado"/>), su
/// identidad queda vacía y Reactivar no la levanta: hay que volver a conectarla, y ahí decide el índice único. La
/// del otro Tenant no se toca: queda fuera del alcance de esta sesión por RLS.</item>
/// <item>Con identidad distinta de la que responde hoy a su dirección (el directorio reasignó la dirección a otro
/// buzón): pasa a ConError (<see cref="MensajeBuzonCambiado"/>).</item>
/// <item>Sin acceso al buzón (403/404, <see cref="CodigosErrorMicrosoft365.SinAccesoBuzon"/>): pasa a ConError
/// (<see cref="MensajeSinAccesoBuzon"/>).</item>
/// <item>Cualquier otro fallo (429, 5xx, 401, tiempo agotado, red, token, excepción) es transitorio y no cambia
/// el estado: la verificación anterior envejece y, pasada <see cref="ConexionIntegracion.VentanaVerificacionIdentidad"/>,
/// la ingesta deja el correo en espera y no se envía nada desde el buzón; nada se descarta. El reintento lo vuelve a
/// intentar.</item>
/// </list>
/// ConError queda así solo para resultados definitivos, y "sin verificar" es una frescura, no un estado.
/// </summary>
public class CompletarIdentidadBuzonService(
    IIntegracionesQueryContext queryContext,
    IConexionIntegracionRepository conexionRepositorio,
    IReclamacionBuzonIntegracionRepository reclamacionRepositorio,
    IEventoWebhookRepository eventoRepositorio,
    AccesoGraphService accesoGraph,
    IMicrosoft365GraphClient graphClient,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios,
    ICerrojoVerificacionBuzon cerrojo,
    SuscripcionAlRehabilitarService suscripciones,
    ILogger<CompletarIdentidadBuzonService> logger)
{
    public const string MensajeBuzonDuplicado =
        "Otra organización tiene conectado este mismo buzón con otra dirección. Desconéctalo en una de las dos.";

    public const string MensajeBuzonCambiado =
        "La dirección de esta conexión corresponde ahora a otro buzón. Desconéctala y vuelve a conectarla desde Conexiones.";

    public const string MensajeSinAccesoBuzon =
        "No podemos abrir este buzón: comprueba que la dirección sigue existiendo y que la aplicación TALVEG sigue teniendo acceso a él.";

    /// <summary>Tope del margen aleatorio que se suma a <see cref="ConexionIntegracion.IntervaloReintentoVerificacion"/>.</summary>
    public static readonly TimeSpan MargenAleatorioMaximo = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Motivos de ConError que pone esta comprobación. La renovación de la suscripción no los sobrescribe con el suyo,
    /// que es menos concreto: el que hay que resolver es este.
    /// </summary>
    public static bool EsMotivoDeIdentidad(string? ultimoError) =>
        ultimoError is MensajeBuzonDuplicado or MensajeBuzonCambiado or MensajeSinAccesoBuzon;

    /// <summary>
    /// Fallo al pedir el token o al renovar la suscripción de una conexión.
    /// <list type="bullet">
    /// <item>Transitorio (<see cref="CodigosErrorMicrosoft365.EsTransitorio"/>: 408, 429, 5xx, red, tiempo agotado, cuerpo
    /// ilegible): no cambia el estado y se reintenta en el siguiente ciclo. Si la suscripción ya expiró, se perdió igual:
    /// disparador (b), marca en la expiración.</item>
    /// <item>Definitivo: la suscripción se pierde, disparador (b), marca en min(ahora, expiración) aunque la conexión ya
    /// esté en ConError por un motivo de identidad. Después pasa a ConError (c), sin sobrescribir el motivo de identidad
    /// (buzón cambiado, duplicado o sin acceso) que ya tenga: ese es más concreto, y es el que hay que resolver.</item>
    /// </list>
    /// Ninguna rama se salta la marca y ninguna la adelanta.
    /// </summary>
    /// <returns>true si la conexión pasó a ConError.</returns>
    public static bool MarcarConErrorDeRenovacion(
        ConexionIntegracion conexion, Error error, DateTime expiracionSuscripcionUtc, DateTime ahoraUtc)
    {
        if (CodigosErrorMicrosoft365.EsTransitorio(error))
        {
            if (expiracionSuscripcionUtc <= ahoraUtc)
                conexion.RegistrarPerdidaSuscripcion(expiracionSuscripcionUtc, ahoraUtc);
            return false;
        }

        conexion.RegistrarPerdidaSuscripcion(expiracionSuscripcionUtc, ahoraUtc);

        if (conexion.Estado == EstadoConexionIntegracion.ConError && EsMotivoDeIdentidad(conexion.UltimoError))
            return false;

        // Una Deshabilitada no se mueve (ver ConexionIntegracion.MarcarConError).
        return conexion.MarcarConError(error.Mensaje, ahoraUtc);
    }

    /// <returns>Las conexiones cuya identidad se leyó y coincide (o se acaba de completar) en este ciclo.</returns>
    public async Task<IReadOnlySet<Guid>> CompletarAsync(CancellationToken cancellationToken)
    {
        var verificadas = new HashSet<Guid>();
        var activas = await queryContext.ConexionesIntegracion
            .Where(c => c.Proveedor == ProveedorIntegracion.Microsoft365 && c.Estado != EstadoConexionIntegracion.Deshabilitada)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        foreach (var conexionId in activas)
        {
            if (await ComprobarAisladaAsync(conexionId, cancellationToken))
                verificadas.Add(conexionId);
        }

        await SacarTerminalesDeGuardaTransitoriaAsync(tenantActivo: true, cancellationToken);
        return verificadas;
    }

    /// <summary>
    /// Reintento acotado: las Habilitadas de Microsoft 365 con la verificación de más de
    /// <see cref="ConexionIntegracion.UmbralReintentoVerificacion"/> o sin verificar, cuya espera
    /// (<see cref="ConexionIntegracion.ReintentoVerificacionNoAntesDeUtc"/>) ya pasó. Una que entra por primera vez no se
    /// intenta ya: se le programa el primer intento tras un retraso aleatorio de hasta
    /// <see cref="ConexionIntegracion.IntervaloReintentoVerificacion"/>, para que las que el ciclo diario verificó en el
    /// mismo minuto no reintenten todas a la vez. Las ConError no entran: las levanta el ciclo diario o Reactivar.
    /// </summary>
    /// <returns>Cuántas conexiones quedaron verificadas.</returns>
    public async Task<int> ReintentarAsync(CancellationToken cancellationToken)
    {
        var ahoraUtc = DateTime.UtcNow;
        var umbral = ahoraUtc - ConexionIntegracion.UmbralReintentoVerificacion;
        var candidatas = await queryContext.ConexionesIntegracion
            .Where(c => c.Proveedor == ProveedorIntegracion.Microsoft365 && c.Estado == EstadoConexionIntegracion.Habilitada)
            .Where(c => c.IdentidadVerificadaUtc == null || c.IdentidadVerificadaUtc < umbral)
            .Where(c => c.ReintentoVerificacionNoAntesDeUtc == null || c.ReintentoVerificacionNoAntesDeUtc <= ahoraUtc)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var verificadas = 0;
        foreach (var conexionId in candidatas)
        {
            if (await ComprobarAisladaAsync(conexionId, cancellationToken, esReintento: true))
                verificadas++;
        }

        return verificadas;
    }

    /// <summary>
    /// Una conexión que lanza no deja sin comprobar a las siguientes del Tenant: no sale verificada y se reintenta más
    /// tarde; su estado no cambia. La cancelación pedida sí se propaga.
    /// </summary>
    private async Task<bool> ComprobarAisladaAsync(Guid conexionId, CancellationToken cancellationToken, bool esReintento = false)
    {
        try
        {
            var verificada = false;
            var ejecutada = await cerrojo.IntentarEjecutarAsync(
                conexionId,
                async ct => verificada = await ComprobarAsync(conexionId, esReintento, ct),
                cancellationToken);

            if (!ejecutada)
                logger.LogInformation(
                    "Otra verificación de la conexión {ConexionId} está en curso: no se comprueba ahora.", conexionId);

            return verificada;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "No se pudo comprobar la identidad del buzón de la conexión {ConexionId}.", conexionId);

            // Un guardado fallido deja sus cambios rastreados: sin esto, el guardado de la conexión siguiente
            // reintentaría la mutación ajena (o volvería a fallar por ella).
            descarteCambios.DescartarCambiosPendientes();
            return false;
        }
    }

    /// <summary>
    /// El estado releído dentro del cerrojo sigue siendo uno que este recorrido debe tocar: el reintento, solo
    /// Habilitada; el ciclo diario, Habilitada o ConError. Una Deshabilitada nunca: Desconectar liberó su reclamación
    /// entre la selección y ahora, y solo Reactivar la saca de ahí, reclamándola de nuevo.
    /// </summary>
    internal static bool EstadoAComprobar(EstadoConexionIntegracion estado, bool esReintento) =>
        esReintento
            ? estado == EstadoConexionIntegracion.Habilitada
            : estado is EstadoConexionIntegracion.Habilitada or EstadoConexionIntegracion.ConError;

    /// <returns>true si la identidad se leyó y coincide con la guardada, o se acaba de completar.</returns>
    private async Task<bool> ComprobarAsync(Guid conexionId, bool esReintento, CancellationToken cancellationToken)
    {
        // Ya dentro del cerrojo: lo que se lee aquí no lo cambia otra verificación a la vez.
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        if (conexion is null)
            return false;

        // Lo que cambió desde la selección (Desconectar, un ConError) decide aquí: sin leer Graph ni mutar nada.
        if (!EstadoAComprobar(conexion.Estado, esReintento))
        {
            logger.LogInformation(
                "La conexión {ConexionId} pasó a {Estado} desde que se seleccionó: no se comprueba.", conexionId, conexion.Estado);
            return false;
        }

        var ahoraUtc = DateTime.UtcNow;

        // Una Habilitada verificada hace poco (por el reintento o por Reactivar) no se vuelve a leer en el ciclo diario:
        // el reintento la cubre cuando envejezca. Una ConError sí: esta lectura es su vía de vuelta.
        if (!esReintento && conexion.Estado == EstadoConexionIntegracion.Habilitada && conexion.VerificadaRecientemente(ahoraUtc))
            return false;

        // Ningún intento automático antes de lo programado: ni el ciclo diario se salta un Retry-After, ni dos intentos
        // de la misma conexión van a menos de IntervaloReintentoVerificacion.
        if (!conexion.PuedeReintentarVerificacion(ahoraUtc))
            return false;

        if (esReintento && conexion.ReintentoVerificacionNoAntesDeUtc is null)
        {
            conexion.ProgramarPrimerReintentoVerificacion(
                ahoraUtc, TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * ConexionIntegracion.IntervaloReintentoVerificacion.Ticks)));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return false;
        }

        // Antes de llamar a Microsoft y guardado ya: el siguiente intento automático no va antes de ~2 h aunque este
        // proceso se caiga a mitad o la comprobación lance.
        conexion.ProgramarReintentoVerificacion(
            ahoraUtc, TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * MargenAleatorioMaximo.Ticks)));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (token.EsFallido)
        {
            // Transitorio: no cambia el estado, la verificación anterior solo envejece.
            logger.LogWarning(
                "No se pudo obtener el token de la conexión {ConexionId} para comprobar su buzón: {Error}", conexionId, token.Error.Mensaje);
            if (AplazarSiMicrosoftPideEsperar(conexion, token.Error))
                await unitOfWork.SaveChangesAsync(cancellationToken);
            return false;
        }

        var identidad = await graphClient.ObtenerIdentidadBuzonAsync(token.Valor, BuzonGraph.De(conexion), cancellationToken);

        if (identidad.EsFallido)
        {
            logger.LogWarning(
                "No se pudo leer la identidad del buzón de la conexión {ConexionId}: {Error}", conexionId, identidad.Error.Mensaje);
            AplazarSiMicrosoftPideEsperar(conexion, identidad.Error);

            // Solo un 403/404 es definitivo. El resto (429, 5xx, 401, tiempo agotado, red) no dice nada del buzón.
            // Un motivo de identidad más concreto que ya tuviera la conexión se conserva.
            if (identidad.Error.Codigo == CodigosErrorMicrosoft365.SinAccesoBuzon && !EsMotivoDeIdentidad(conexion.UltimoError))
                conexion.MarcarConError(MensajeSinAccesoBuzon);

            // También guarda el token refrescado, si lo hubo, para no volver a rotarlo en la renovación.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (conexion.IdentidadBuzon is { } guardada)
        {
            if (!string.Equals(guardada, identidad.Valor, StringComparison.Ordinal))
            {
                logger.LogError(
                    "La dirección de la conexión {ConexionId} responde ahora con otro buzón: pasa a ConError.", conexionId);
                conexion.MarcarConError(MensajeBuzonCambiado);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return false;
            }

            await MarcarVerificadaAsync(conexion, token.Valor, ahoraUtc, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }

        conexion.FijarIdentidadBuzon(identidad.Valor);
        var reclamacion = await reclamacionRepositorio.ObtenerPorConexionAsync(conexionId, cancellationToken);
        reclamacion?.FijarIdentidadBuzon(identidad.Valor);
        conexion.MarcarIdentidadVerificada(ahoraUtc);

        var resultadoReclamacion = await reclamacionRepositorio.GuardarCambiosSiBuzonLibreAsync(cancellationToken);
        if (resultadoReclamacion == ResultadoReclamacionBuzon.Guardada)
        {
            // Ya confirmada la identidad: la rehabilitación y la cola van en un segundo guardado, para no tocar nada de
            // un buzón que acaba de chocar con otro.
            await MarcarVerificadaAsync(conexion, token.Valor, ahoraUtc, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (resultadoReclamacion == ResultadoReclamacionBuzon.AltaCompartidaEnPausa)
        {
            // Completar la identidad saca la fila del predicado de la guarda, así que no debería chocar: si choca, no es
            // un defecto del buzón y no se marca ConError; el siguiente ciclo lo vuelve a intentar.
            logger.LogWarning("Completar la identidad de la conexión {ConexionId} chocó con la guarda transitoria.", conexionId);
            return false;
        }

        // El guardado fallido descartó los cambios en memoria: se vuelve a leer la conexión para marcarla.
        logger.LogError(
            "La conexión {ConexionId} tiene el mismo buzón que una reclamación de otro Tenant con otra dirección: pasa a ConError.",
            conexionId);
        var duplicada = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        if (duplicada is null)
            return false;

        duplicada.MarcarConError(MensajeBuzonDuplicado);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return false;
    }

    /// <summary>
    /// Microsoft respondió 429 con Retry-After: ninguna verificación automática de esta conexión vuelve antes.
    /// </summary>
    /// <returns>true si la espera cambió.</returns>
    internal static bool AplazarSiMicrosoftPideEsperar(ConexionIntegracion conexion, Error error)
    {
        if (error.ReintentarTras is not { } espera)
            return false;

        var antes = conexion.ReintentoVerificacionNoAntesDeUtc;
        conexion.AplazarReintentoVerificacion(DateTime.UtcNow, espera);
        return conexion.ReintentoVerificacionNoAntesDeUtc != antes;
    }

    /// <summary>
    /// Verificada en este ciclo: se rehabilita si estaba en ConError y sus eventos retenidos vuelven a la cola. La marca
    /// del hueco, si la hay, se conserva: el correo de ese intervalo pudo no entrar y Conexiones lo sigue avisando.
    /// </summary>
    /// <para>
    /// Desde ConError, antes de rehabilitar se asegura la suscripción de Graph (<see cref="SuscripcionAlRehabilitarService"/>):
    /// si falla de forma transitoria, sigue en ConError y sus retenidos esperan al siguiente intento; si falla de forma
    /// definitiva, sigue en ConError con el motivo de la suscripción. Nunca vuelve a Habilitada sin suscripción vigente.
    /// </para>
    private async Task MarcarVerificadaAsync(
        ConexionIntegracion conexion, string accessToken, DateTime ahoraUtc, CancellationToken cancellationToken)
    {
        conexion.MarcarIdentidadVerificada(ahoraUtc);

        if (conexion.Estado == EstadoConexionIntegracion.ConError)
        {
            var suscripcion = await suscripciones.AsegurarAsync(conexion, accessToken, cancellationToken);
            if (suscripcion.EsFallido)
            {
                logger.LogWarning(
                    "La conexión {ConexionId} está verificada pero su suscripción no se pudo asegurar ({Error}): sigue en ConError.",
                    conexion.Id, suscripcion.Error.Mensaje);
                AplazarSiMicrosoftPideEsperar(conexion, suscripcion.Error);
                if (!CodigosErrorMicrosoft365.EsTransitorio(suscripcion.Error))
                    conexion.MarcarConError(suscripcion.Error.Mensaje, ahoraUtc);
                return;
            }

            logger.LogInformation("La conexión {ConexionId} vuelve a estar verificada: se rehabilita.", conexion.Id);
            conexion.Rehabilitar();
        }

        foreach (var retenido in await eventoRepositorio.ObtenerRetenidosAsync(conexion.Id, cancellationToken))
            retenido.Liberar();
    }

    /// <summary>
    /// Condición de salida de la guarda transitoria (<see cref="ReclamacionBuzonIntegracion.CuentaParaGuardaTransitoria"/>):
    /// una reclamación heredada sin identidad cuya conexión es terminal deja de contar, para que la guarda nunca bloquee
    /// para siempre. Es terminal la conexión que no existe, no es de Microsoft 365 o no está Habilitada (ConError,
    /// también la que dejó un 404, o Deshabilitada), y toda conexión de un Tenant propietario que no está Activo: la
    /// renovación de suscripciones y la verificación de identidad solo recorren los Tenants activos, así que su
    /// suscripción caduca y la conexión no es operativa de hecho, aunque siga Habilitada.
    /// <para>
    /// Solo lee las reclamaciones del Tenant del ámbito (RLS). El ciclo diario lo llama tras <see cref="CompletarAsync"/>
    /// en cada Tenant activo, y lo llama solo, sin verificar ni renovar nada, en cada Tenant no activo, con
    /// <paramref name="tenantActivo"/> a false. Una reclamación que sale no vuelve a contar aunque el Tenant se
    /// reactive: si su conexión completa después la identidad, decide el índice único por identidad.
    /// </para>
    /// </summary>
    /// <param name="tenantActivo">El Tenant propietario del ámbito está Activo.</param>
    public async Task SacarTerminalesDeGuardaTransitoriaAsync(bool tenantActivo, CancellationToken cancellationToken)
    {
        var ahoraUtc = DateTime.UtcNow;
        var cambiadas = 0;
        foreach (var reclamacion in await reclamacionRepositorio.ObtenerQueCuentanParaGuardaTransitoriaAsync(cancellationToken))
        {
            var conexion = tenantActivo
                ? await conexionRepositorio.ObtenerPorIdAsync(reclamacion.ConexionIntegracionId, cancellationToken)
                : null;
            var terminal = !tenantActivo
                || conexion is null
                || conexion.Proveedor != ProveedorIntegracion.Microsoft365
                || conexion.Estado != EstadoConexionIntegracion.Habilitada;
            if (terminal && reclamacion.SacarDeGuardaTransitoria(ahoraUtc))
                cambiadas++;
        }

        if (cambiadas > 0)
        {
            logger.LogInformation("{Cambiadas} reclamaciones de conexiones terminales salen de la guarda transitoria.", cambiadas);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}
