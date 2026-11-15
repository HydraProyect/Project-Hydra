using System.Linq.Expressions;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Origen del texto de lo que envía una persona (issue #2007, D-5 del plan de cierre de issues legales; regla A de la
/// coordinación, 2026-11-15). Lo resuelve <see cref="OrigenDelEnvioAsync"/> antes de enviar, para que la marca de IA vaya
/// ya en el correo:
///
/// <list type="number">
/// <item>Si el redactor dice qué sugerencia insertó (<c>RespuestaSugeridaId</c> del comando), el servidor exige que esté
/// <see cref="EstadoRespuestaSugerida.Revisada"/>, sea de <see cref="OrigenRespuestaSugerida.Vega"/> y pertenezca a un hilo
/// que quien envía puede ver. Si no cumple, el envío se rechaza: no se degrada a Persona en silencio. El origen es
/// <see cref="OrigenContenidoMensaje.VegaRevisada"/> aunque la persona haya rehecho el texto: lo tenía en el redactor.</item>
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
/// Persona y sin la cabecera. Una macro no se atribuye: lo que la persona envía tras aplicarla cuenta como Persona.
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
        "La respuesta sugerida que insertaste ya no se puede enviar. Vuelve a pedirla o escribe la respuesta.");

    private const int LoteTextos = 50;

    /// <summary>
    /// La sugerencia de Vega de la que parte <paramref name="cuerpo"/>, o null si lo escribió una persona; fallo si
    /// <paramref name="respuestaSugeridaId"/> no es una sugerencia de Vega revisada de un hilo visible. Se llama después
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

        var ids = await IdsDeVegaRevisadasAsync(comunicaciones, alcanceDatos, conversacionId, conexionIntegracionId, cancellationToken);
        var coincidentes = await CoincidentesAsync(comunicaciones, ids, cuerpo, cancellationToken);
        return Result.Exito(coincidentes
            .OrderByDescending(r => r.RevisadaEnUtc)
            .ThenByDescending(r => r.Id)
            .FirstOrDefault());
    }

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
                where r.Id == id && r.Estado == EstadoRespuestaSugerida.Revisada && r.Origen == OrigenRespuestaSugerida.Vega
                select new { Sugerencia = r, Conversacion = c })
            .FirstOrDefaultAsync(cancellationToken);

        return candidata is not null && await alcanceDatos.ConversacionVisibleAsync(candidata.Conversacion, cancellationToken)
            ? candidata.Sugerencia
            : null;
    }

    /// <summary>
    /// Las sugerencias de Vega revisadas del hilo del envío y de los hilos del buzón que quien envía puede ver
    /// (<see cref="BloqueoRespuestaSugerida.HilosVisiblesDelBuzonAsync"/>). Solo los identificadores: los textos se leen
    /// después por lotes.
    /// </summary>
    private static async Task<List<Guid>> IdsDeVegaRevisadasAsync(
        IComunicacionesQueryContext comunicaciones, IAlcanceDatosService alcanceDatos, Guid? conversacionId,
        Guid? conexionIntegracionId, CancellationToken cancellationToken)
    {
        var vegaRevisadas = comunicaciones.RespuestasSugeridas
            .Where(r => r.Estado == EstadoRespuestaSugerida.Revisada && r.Origen == OrigenRespuestaSugerida.Vega);

        var ids = new HashSet<Guid>();
        if (conversacionId is { } hilo)
            ids.UnionWith(await vegaRevisadas.Where(r => r.ConversacionId == hilo).Select(r => r.Id).ToListAsync(cancellationToken));

        if (conexionIntegracionId is { } conexion &&
            await BloqueoRespuestaSugerida.HilosVisiblesDelBuzonAsync(alcanceDatos, conexion, cancellationToken) is { } hilosVisibles)
        {
            ids.UnionWith(await DelBuzon(comunicaciones, vegaRevisadas, hilosVisibles).ToListAsync(cancellationToken));
        }

        return ids.OrderBy(i => i).ToList();
    }

    private static IQueryable<Guid> DelBuzon(
        IComunicacionesQueryContext comunicaciones, IQueryable<RespuestaSugerida> sugerencias,
        Expression<Func<Conversacion, bool>> hilosVisibles) =>
        from r in sugerencias
        join c in comunicaciones.Conversaciones.Where(hilosVisibles) on r.ConversacionId equals c.Id
        select r.Id;

    /// <summary>
    /// De <paramref name="ids"/>, las sugerencias cuyo texto coincide con <paramref name="cuerpo"/>, leídas por lotes de
    /// <see cref="LoteTextos"/> y sin seguimiento (cada texto puede ocupar hasta 1 MiB): las que no coinciden se descartan
    /// tras compararlas.
    /// </summary>
    private static async Task<List<RespuestaSugerida>> CoincidentesAsync(
        IComunicacionesQueryContext comunicaciones, IReadOnlyList<Guid> ids, string cuerpo, CancellationToken cancellationToken)
    {
        var hash = RespuestaSugerida.CalcularTextoHash(cuerpo);
        var cuerpoNormalizado = RespuestaSugerida.NormalizarTexto(cuerpo);
        var coincidentes = new List<RespuestaSugerida>();
        foreach (var lote in ids.Chunk(LoteTextos))
        {
            var sugerencias = await comunicaciones.RespuestasSugeridas
                .AsNoTracking()
                .Where(r => lote.Contains(r.Id))
                .ToListAsync(cancellationToken);
            coincidentes.AddRange(sugerencias.Where(r =>
                r.TextoHash == hash || BloqueoRespuestaSugerida.CoincideConElTexto(cuerpoNormalizado, r.Texto)));
        }

        return coincidentes;
    }
}
