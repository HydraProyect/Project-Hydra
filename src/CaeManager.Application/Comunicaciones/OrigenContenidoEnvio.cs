using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Origen del texto de lo que envía una persona (issue #2007, D-5 del plan de cierre de issues legales). Lo resuelve el
/// servidor, no el cliente: el comando de envío no recibe ningún identificador de sugerencia, así que una interfaz que se lo
/// saltara no podría hacer pasar un texto de Vega por uno escrito a mano.
///
/// <para>
/// Regla de una conversación (<see cref="SugerenciaDelEnvioAsync"/>): candidatas son las sugerencias
/// <see cref="EstadoRespuestaSugerida.Revisada"/> del hilo, de cualquier entrante, que coinciden con el cuerpo (el mismo
/// texto, contenido entero o casi igual: la misma comparación que <see cref="BloqueoRespuestaSugerida"/>) o que todavía no
/// son el origen de ningún <see cref="Mensaje"/>. Gana una de Vega sobre una de macro, después la que coincide con el
/// cuerpo y después la revisada más reciente. Sin candidatas, el origen es <see cref="OrigenContenidoMensaje.Persona"/>.
/// No depende del último entrante: una propuesta revisada sigue en el redactor aunque llegue otro mensaje.
/// </para>
///
/// <para>
/// El error que admite es el conservador: marca como de IA un texto que la persona rehízo entero tras confirmar una
/// propuesta de Vega que no llegó a enviar, una vez (ese envío la consume). Lo que no hace es dejar sin marca un texto que
/// sale de una propuesta de Vega revisada del hilo, se haya enviado antes o no, ni dejar que una macro revisada después la
/// tape. Límite: un texto de Vega reescrito más allá del umbral de parecido después de enviarlo una vez ya no se reconoce.
/// </para>
///
/// <para>
/// Un mensaje nuevo (<see cref="SugerenciaDelMensajeNuevoAsync"/>) no contesta a ningún entrante, así que solo cuenta el
/// texto: una sugerencia revisada de un hilo de ese buzón con alguno de los destinatarios, visible para quien envía, cuyo
/// texto coincide con el cuerpo. Son los hilos en los que el proveedor puede agrupar el mensaje, los mismos que mira el
/// bloqueo por buzón.
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

    private const int LoteTextosPorBuzon = 50;

    /// <summary>
    /// La sugerencia revisada de la que parte <paramref name="cuerpo"/>, que una persona envía ahora en
    /// <paramref name="conversacionId"/>, o null si lo escribió a mano. Se llama después de
    /// <see cref="BloqueoRespuestaSugerida.ComprobarEnvioAsync"/> y antes de enviar, para que la marca vaya ya en el correo.
    /// </summary>
    public static async Task<RespuestaSugerida?> SugerenciaDelEnvioAsync(
        this IComunicacionesQueryContext comunicaciones, Guid conversacionId, string cuerpo, CancellationToken cancellationToken)
    {
        var revisadas = await comunicaciones.RespuestasSugeridas
            .Where(r => r.ConversacionId == conversacionId && r.Estado == EstadoRespuestaSugerida.Revisada)
            .Select(r => new { Sugerencia = r, Consumida = comunicaciones.Mensajes.Any(m => m.RespuestaSugeridaId == r.Id) })
            .ToListAsync(cancellationToken);

        var coincide = Comparador(cuerpo);
        return revisadas
            .Select(r => (r.Sugerencia, Coincide: coincide(r.Sugerencia), r.Consumida))
            .Where(r => r.Coincide || !r.Consumida)
            .OrderByDescending(r => r.Sugerencia.Origen == OrigenRespuestaSugerida.Vega)
            .ThenByDescending(r => r.Coincide)
            .ThenByDescending(r => r.Sugerencia.RevisadaEnUtc)
            .ThenByDescending(r => r.Sugerencia.GeneradaEnUtc)
            .ThenByDescending(r => r.Sugerencia.Id)
            .Select(r => r.Sugerencia)
            .FirstOrDefault();
    }

    /// <summary>
    /// La sugerencia revisada cuyo texto coincide con <paramref name="cuerpo"/> en un hilo del buzón
    /// <paramref name="conexionIntegracionId"/> con alguno de <paramref name="destinatarios"/> que quien envía puede ver, o
    /// null. Se llama después de <see cref="BloqueoRespuestaSugerida.ComprobarEnvioPorBuzonAsync"/> y antes de enviar. Los
    /// textos se leen por lotes, como en el bloqueo, y un hilo no visible no llega nunca a memoria.
    /// </summary>
    public static async Task<RespuestaSugerida?> SugerenciaDelMensajeNuevoAsync(
        this IComunicacionesQueryContext comunicaciones, IAlcanceDatosService alcanceDatos, Guid conexionIntegracionId,
        IEnumerable<string> destinatarios, string cuerpo, CancellationToken cancellationToken)
    {
        var hilosVisibles = await BloqueoRespuestaSugerida.HilosVisiblesDelBuzonAsync(alcanceDatos, conexionIntegracionId, cancellationToken);
        if (hilosVisibles is null)
            return null;

        var hilos = BloqueoRespuestaSugerida.HilosConElDestinatario(
            comunicaciones, hilosVisibles, BloqueoRespuestaSugerida.DireccionesNormalizadas(destinatarios));
        var ids = await (
                from r in comunicaciones.RespuestasSugeridas
                join c in hilos on r.ConversacionId equals c.Id
                where r.Estado == EstadoRespuestaSugerida.Revisada
                orderby r.Id
                select r.Id)
            .ToListAsync(cancellationToken);

        var coincide = Comparador(cuerpo);
        var coincidentes = new List<RespuestaSugerida>();
        foreach (var lote in ids.Chunk(LoteTextosPorBuzon))
        {
            var sugerencias = await comunicaciones.RespuestasSugeridas
                .Where(r => lote.Contains(r.Id))
                .ToListAsync(cancellationToken);
            coincidentes.AddRange(sugerencias.Where(coincide));
        }

        return coincidentes
            .OrderByDescending(r => r.Origen == OrigenRespuestaSugerida.Vega)
            .ThenByDescending(r => r.RevisadaEnUtc)
            .ThenByDescending(r => r.Id)
            .FirstOrDefault();
    }

    /// <summary>Fija en <paramref name="mensaje"/> el origen resuelto por las consultas de arriba.</summary>
    public static void RegistrarEn(Mensaje mensaje, RespuestaSugerida? sugerencia)
    {
        if (sugerencia is null)
            mensaje.RegistrarOrigenPersona();
        else
            mensaje.RegistrarOrigenDesde(sugerencia);
    }

    private static Func<RespuestaSugerida, bool> Comparador(string cuerpo)
    {
        var hash = RespuestaSugerida.CalcularTextoHash(cuerpo);
        var cuerpoNormalizado = RespuestaSugerida.NormalizarTexto(cuerpo);
        return r => r.TextoHash == hash || BloqueoRespuestaSugerida.CoincideConElTexto(cuerpoNormalizado, r.Texto);
    }
}
