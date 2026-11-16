using System.Linq.Expressions;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Origen del texto de lo que envía una persona (issue #2007, D-5 del plan de cierre de issues legales; regla A de la
/// coordinación, 2026-11-15). Lo resuelve <see cref="OrigenDelEnvioAsync"/> antes de enviar, para que la marca de IA vaya
/// ya en el correo:
///
/// <list type="number">
/// <item>Si el redactor dice qué sugerencia insertó (<c>RespuestaSugeridaId</c> del comando), el servidor exige que esté
/// <see cref="EstadoRespuestaSugerida.Revisada"/> o <see cref="EstadoRespuestaSugerida.Descartada"/>, sea de
/// <see cref="OrigenRespuestaSugerida.Vega"/> y pertenezca a un hilo que quien envía puede ver. Si no cumple, el envío se
/// rechaza: no se degrada a Persona en silencio. El origen es <see cref="OrigenContenidoMensaje.VegaRevisada"/> aunque la
/// persona haya rehecho el texto: lo tenía en el redactor. Descartada cuenta porque el origen de IA se conserva mientras
/// el texto siga en el redactor, aunque la sugerencia se descarte, se sustituya por otra generación o se rechace un envío
/// anterior (decisión de Chris del 2026-11-16); el bloqueo de la sugerencia ya impide enviar ese texto sin editarlo.</item>
/// <item>Sin id, se compara el cuerpo con las sugerencias de Vega revisadas de los hilos visibles del buzón (y del propio
/// hilo): el mismo texto, el texto entero contenido en el cuerpo (desde
/// <see cref="BloqueoRespuestaSugerida.LongitudMinimaTextoContenido"/> caracteres normalizados) o casi el mismo (a lo sumo
/// <see cref="RevisionRespuestaSugerida.UmbralEdicion"/> caracteres cambiados), la comparación de
/// <see cref="BloqueoRespuestaSugerida"/>. Si coincide alguna, el origen es VegaRevisada.</item>
/// <item>Si no coincide nada, el origen es <see cref="OrigenContenidoMensaje.Persona"/>.</item>
/// </list>
///
/// <para>
/// Límite declarado: un texto de Vega editado más allá de ese umbral y pegado fuera del redactor (sin el id) sale como
/// Persona y sin la cabecera. Dentro del redactor no hay límite por descarte ni por rechazo: el redactor conserva el id
/// mientras conserve el texto. Una macro no se atribuye: lo que la persona envía tras aplicarla cuenta como Persona.
/// </para>
/// </summary>
public static class OrigenContenidoEnvio
{
    /// <summary>
    /// Cabecera del correo saliente cuyo texto redactó Vega y revisó una persona: legible por máquina y sin texto visible
    /// añadido, porque Vega ya firma (D-5). Graph solo admite cabeceras propias que empiecen por «x-».
    /// </summary>
    public const string NombreCabecera = "X-TALVEG-Origen-Contenido";

    public const string ValorCabeceraIaRevisada = "ia-revisada";

    public static readonly Error SugerenciaNoUtilizable = Error.Crear(
        "RespuestaSugerida.NoUtilizable",
        "La respuesta sugerida que insertaste ya no se puede enviar. Vuelve a pedirla, o vacía el redactor y escribe la respuesta.");

    private const int LoteTextos = 50;

    /// <summary>
    /// La sugerencia de Vega de la que parte <paramref name="cuerpo"/>, o null si lo escribió una persona; fallo si
    /// <paramref name="respuestaSugeridaId"/> no es una sugerencia de Vega revisada o descartada de un hilo visible. Se llama después
    /// del bloqueo de la sugerencia y antes de enviar. <paramref name="conversacionId"/> es el hilo en el que se envía
    /// (null en un mensaje nuevo) y <paramref name="conexionIntegracionId"/>, el buzón o la línea cuyos hilos se miran.
    /// </summary>
    public static async Task<Result<RespuestaSugerida?>> OrigenDelEnvioAsync(
        this IComunicacionesQueryContext comunicaciones, IAlcanceDatosService alcanceDatos, Guid? respuestaSugeridaId,
        Guid? conversacionId, Guid? conexionIntegracionId, string cuerpo, CancellationToken cancellationToken)
    {
        if (respuestaSugeridaId is { } id)
        {
            var insertada = await SugerenciaInsertadaAsync(comunicaciones, alcanceDatos, id, cancellationToken);
            return insertada is null ? Result.Fallo<RespuestaSugerida?>(SugerenciaNoUtilizable) : Result.Exito<RespuestaSugerida?>(insertada);
        }

        Expression<Func<Conversacion, bool>>? hilosVisibles = null;
        if (conexionIntegracionId is { } conexion)
            hilosVisibles = await BloqueoRespuestaSugerida.HilosVisiblesDelBuzonAsync(alcanceDatos, conexion, cancellationToken);
        if (conversacionId is null && hilosVisibles is null)
            return Result.Exito<RespuestaSugerida?>(null);

        // Cada lectura (la de los ids y la de cada lote de textos) vuelve a aplicar estado, origen y alcance: un hilo que
        // sale de la cartera de quien envía, o una sugerencia descartada, entre una lectura y otra no se compara.
        IQueryable<RespuestaSugerida> Candidatas() => VegaRevisadasVisibles(comunicaciones, conversacionId, hilosVisibles);
        var ids = await Candidatas().Select(r => r.Id).OrderBy(i => i).ToListAsync(cancellationToken);
        return Result.Exito(await MejorCoincidenteAsync(Candidatas, ids, cuerpo, cancellationToken));
    }

    /// <summary>
    /// Deja en el log, en cuanto el canal acepta el envío, qué origen lleva (revisión Codex r3 de la #2007): si después falla
    /// el registro local (guardar adjuntos, el SaveChanges, un hilo de otro titular), el correo ya salió y este es el rastro
    /// que permite reconciliar su origen y su sugerencia. Nivel Warning a propósito (revisión Codex r4): es un registro de
    /// auditoría y no debe perderse con la configuración de log admitida, que filtra Information.
    /// </summary>
    public static void TrazarEnvioAceptado(
        ILogger logger, string canal, Guid? conversacionId, string? mensajeExternoId, RespuestaSugerida? sugerencia) =>
        logger.LogWarning(
            "Envío aceptado por {Canal}: mensaje externo {MensajeExternoId}, conversación {ConversacionId}, origen del contenido {OrigenContenido}, respuesta sugerida {RespuestaSugeridaId}.",
            canal, mensajeExternoId, conversacionId, OrigenDe(sugerencia), sugerencia?.Id);

    private static OrigenContenidoMensaje OrigenDe(RespuestaSugerida? sugerencia) => sugerencia switch
    {
        null => OrigenContenidoMensaje.Persona,
        { Origen: OrigenRespuestaSugerida.Vega } => OrigenContenidoMensaje.VegaRevisada,
        _ => OrigenContenidoMensaje.MacroRevisada,
    };

    /// <summary>Fija en <paramref name="mensaje"/> el origen resuelto por <see cref="OrigenDelEnvioAsync"/>.</summary>
    public static void RegistrarEn(Mensaje mensaje, RespuestaSugerida? sugerencia)
    {
        if (sugerencia is null)
            mensaje.RegistrarOrigenPersona();
        else
            mensaje.RegistrarOrigenDesde(sugerencia);
    }

    private static async Task<RespuestaSugerida?> SugerenciaInsertadaAsync(
        IComunicacionesQueryContext comunicaciones, IAlcanceDatosService alcanceDatos, Guid id, CancellationToken cancellationToken)
    {
        var candidata = await (
                from r in comunicaciones.RespuestasSugeridas.AsNoTracking()
                join c in comunicaciones.Conversaciones on r.ConversacionId equals c.Id
                where r.Id == id &&
                      (r.Estado == EstadoRespuestaSugerida.Revisada || r.Estado == EstadoRespuestaSugerida.Descartada) &&
                      r.Origen == OrigenRespuestaSugerida.Vega
                select new { Sugerencia = r, Conversacion = c })
            .FirstOrDefaultAsync(cancellationToken);

        return candidata is not null && await alcanceDatos.ConversacionVisibleAsync(candidata.Conversacion, cancellationToken)
            ? candidata.Sugerencia
            : null;
    }

    /// <summary>
    /// Las sugerencias de Vega revisadas del hilo del envío y de los hilos del buzón que quien envía puede ver
    /// (<see cref="BloqueoRespuestaSugerida.HilosVisiblesDelBuzonAsync"/>), como consulta nueva en cada llamada: se usa
    /// para leer los ids y otra vez para cada lote de textos, de modo que el filtro se evalúa contra la base en cada lectura.
    /// </summary>
    private static IQueryable<RespuestaSugerida> VegaRevisadasVisibles(
        IComunicacionesQueryContext comunicaciones, Guid? conversacionId, Expression<Func<Conversacion, bool>>? hilosVisibles)
    {
        var vegaRevisadas = comunicaciones.RespuestasSugeridas
            .Where(r => r.Estado == EstadoRespuestaSugerida.Revisada && r.Origen == OrigenRespuestaSugerida.Vega);
        var hilo = conversacionId ?? Guid.Empty;
        if (hilosVisibles is null)
            return vegaRevisadas.Where(r => r.ConversacionId == hilo);

        var delBuzon = comunicaciones.Conversaciones.Where(hilosVisibles);
        return conversacionId is null
            ? vegaRevisadas.Where(r => delBuzon.Any(c => c.Id == r.ConversacionId))
            : vegaRevisadas.Where(r => r.ConversacionId == hilo || delBuzon.Any(c => c.Id == r.ConversacionId));
    }

    /// <summary>
    /// De <paramref name="ids"/>, la sugerencia cuyo texto coincide con <paramref name="cuerpo"/> revisada más tarde (y a
    /// igualdad, la de mayor id), leídas por lotes de <see cref="LoteTextos"/> y sin seguimiento (cada texto puede ocupar
    /// hasta 1 MiB). Solo se conserva la mejor hasta ahora: la memoria no crece con el número de coincidencias (revisión
    /// Codex r3 de la #2007). Cada lote se lee de <paramref name="candidatas"/>, que vuelve a aplicar el alcance (r4).
    /// </summary>
    private static async Task<RespuestaSugerida?> MejorCoincidenteAsync(
        Func<IQueryable<RespuestaSugerida>> candidatas, IReadOnlyList<Guid> ids, string cuerpo, CancellationToken cancellationToken)
    {
        var hash = RespuestaSugerida.CalcularTextoHash(cuerpo);
        var cuerpoNormalizado = RespuestaSugerida.NormalizarTexto(cuerpo);
        RespuestaSugerida? mejor = null;
        foreach (var lote in ids.Chunk(LoteTextos))
        {
            var sugerencias = await candidatas()
                .AsNoTracking()
                .Where(r => lote.Contains(r.Id))
                .ToListAsync(cancellationToken);
            foreach (var candidata in sugerencias)
            {
                if (EsMejor(candidata, mejor) &&
                    (candidata.TextoHash == hash || BloqueoRespuestaSugerida.CoincideConElTexto(cuerpoNormalizado, candidata.Texto)))
                    mejor = candidata;
            }
        }

        return mejor;
    }

    private static bool EsMejor(RespuestaSugerida candidata, RespuestaSugerida? mejor) =>
        mejor is null ||
        candidata.RevisadaEnUtc > mejor.RevisadaEnUtc ||
        (candidata.RevisadaEnUtc == mejor.RevisadaEnUtc && candidata.Id.CompareTo(mejor.Id) > 0);
}
