using System.Globalization;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRepartoAdjuntos;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Web.Features.Comunicaciones;

/// <summary>
/// En qué punto del reparto está un adjunto, tal como lo ve quien lo reparte. Lo guardado es solo <see cref="EstadoRepartoAdjunto"/>;
/// lo que está en curso se deduce del destino, del tipo y de las fechas que la persona ha revisado.
/// </summary>
public enum SituacionAdjunto
{
    /// <summary>Destino y tipo identificados por su nombre y fechas revisadas: solo falta asignarlo.</summary>
    Listo,

    /// <summary>
    /// Tiene destino, pero falta el tipo de documento o las fechas, o el destino o el tipo no se pueden mostrar por su nombre:
    /// lo decide una persona.
    /// </summary>
    PorRevisar,

    SinDestino,
    Asignado,
    Rechazado,
    Descartado,
    Dividido
}

/// <summary>
/// Lo que la persona escribe en la fila y aún no está en el servidor: las fechas con las que se dará de alta el Documento, el
/// aviso de la última acción y el estado de los desplegables. Las fechas las revisa siempre una persona
/// (<c>AdjuntoAAsignar</c>): el reparto no las inventa. Se conserva entre recargas de la lista, por adjunto.
/// </summary>
public sealed class EdicionAdjunto
{
    private const string FormatoFecha = "yyyy-MM-dd";

    private string _fechaEmision = string.Empty;
    private string _fechaVencimiento = string.Empty;
    private bool _noCaduca;

    /// <summary>
    /// Cuenta cada cambio de las fechas o de «No caduca». Una asignación que sale con unas fechas anota su revisión; si al volver
    /// la persona ya las ha cambiado, el motivo con que el servidor rechazó las fechas viejas ya no habla de lo que hay escrito y no se pinta.
    /// </summary>
    public int Revision { get; private set; }

    public string FechaEmision
    {
        get => _fechaEmision;
        set { if (_fechaEmision != value) Revision++; _fechaEmision = value; }
    }

    public string FechaVencimiento
    {
        get => _fechaVencimiento;
        set { if (_fechaVencimiento != value) Revision++; _fechaVencimiento = value; }
    }

    public bool NoCaduca
    {
        get => _noCaduca;
        set { if (_noCaduca != value) Revision++; _noCaduca = value; }
    }

    /// <summary>Por qué falló la última acción sobre este adjunto, o lo que dijo el servidor al no asignarlo.</summary>
    public string? Aviso { get; set; }

    public bool MenuAbierto { get; set; }
    public bool PideMotivo { get; set; }
    public string Motivo { get; set; } = string.Empty;

    public DateOnly? Emision => Leer(FechaEmision);
    public DateOnly? Vencimiento => Leer(FechaVencimiento);

    /// <summary>La emisión es una fecha; el vencimiento, si se escribe, también, y no va con «No caduca».</summary>
    public bool FechasValidas =>
        Emision is not null
        && (string.IsNullOrWhiteSpace(FechaVencimiento) || (Vencimiento is not null && !NoCaduca));

    /// <summary>Hay algo escrito en las fechas que no se entiende o que se contradice: se avisa, no se calla.</summary>
    public bool FechasContradictorias =>
        (!string.IsNullOrWhiteSpace(FechaEmision) && !FechasValidas)
        || (NoCaduca && !string.IsNullOrWhiteSpace(FechaVencimiento));

    private static DateOnly? Leer(string texto) =>
        DateOnly.TryParseExact(texto?.Trim(), FormatoFecha, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha) ? fecha : null;
}

/// <summary>
/// Un adjunto del hilo con lo que la persona lleva escrito sobre él. <paramref name="NombresConocidos"/> dice si el destino y el
/// tipo que tiene se pueden mostrar por su nombre (están en los selectores que la pantalla leyó): si no, la persona no sabría a
/// quién ni como qué se daría de alta el Documento, y el adjunto no puede estar «Listo». Es obligatorio a propósito: un valor por
/// defecto dejaría la guarda abierta a quien construyera la fila sin pensar en ello.
/// </summary>
public sealed record FilaReparto(AdjuntoRepartoDto Adjunto, EdicionAdjunto Edicion, bool NombresConocidos)
{
    public bool TieneDestino => Adjunto.DestinoTrabajadorId is not null || Adjunto.DestinoEmpresaId is not null;
    public bool Pendiente => Adjunto.Estado == EstadoRepartoAdjunto.Pendiente;
    public SituacionAdjunto Situacion => ClasificacionReparto.Situacion(Adjunto, Edicion, NombresConocidos);
}

/// <summary>Los recuentos de la cabecera, del pie y de los botones salen todos de la misma lista de filas y de la misma clasificación.</summary>
public sealed record ResumenReparto(
    int Listos, int PorRevisar, int SinDestino, int Asignados, int Rechazados, int Descartados, int Divididos)
{
    public int EnCurso => Listos + PorRevisar + SinDestino;
    public int Hechos => Asignados + Rechazados + Descartados + Divididos;
    public int Total => EnCurso + Hechos;

    /// <summary>Lo que pide una decisión: los adjuntos que no están listos.</summary>
    public int ParaRevisarUnoAUno => PorRevisar + SinDestino;
}

public static class ClasificacionReparto
{
    /// <param name="nombresConocidos">
    /// Falso si el destino o el tipo no se pueden mostrar por su nombre (un selector no se pudo leer, o el valor no está en él):
    /// lo que no se puede identificar no se aprueba, así que el adjunto se queda «Revisar» aunque tenga todo lo demás.
    /// </param>
    public static SituacionAdjunto Situacion(AdjuntoRepartoDto adjunto, EdicionAdjunto edicion, bool nombresConocidos) => adjunto.Estado switch
    {
        EstadoRepartoAdjunto.Asignado => SituacionAdjunto.Asignado,
        EstadoRepartoAdjunto.Rechazado => SituacionAdjunto.Rechazado,
        EstadoRepartoAdjunto.Descartado => SituacionAdjunto.Descartado,
        EstadoRepartoAdjunto.Dividido => SituacionAdjunto.Dividido,
        _ when adjunto.DestinoTrabajadorId is null && adjunto.DestinoEmpresaId is null => SituacionAdjunto.SinDestino,
        _ when adjunto.TipoDocumentoId is null || !edicion.FechasValidas || !nombresConocidos => SituacionAdjunto.PorRevisar,
        _ => SituacionAdjunto.Listo
    };

    public static ResumenReparto Resumir(IEnumerable<FilaReparto> filas)
    {
        var porSituacion = filas.GroupBy(f => f.Situacion).ToDictionary(g => g.Key, g => g.Count());
        int N(SituacionAdjunto s) => porSituacion.GetValueOrDefault(s);
        return new ResumenReparto(
            N(SituacionAdjunto.Listo), N(SituacionAdjunto.PorRevisar), N(SituacionAdjunto.SinDestino),
            N(SituacionAdjunto.Asignado), N(SituacionAdjunto.Rechazado), N(SituacionAdjunto.Descartado), N(SituacionAdjunto.Dividido));
    }

    /// <summary>
    /// La cola de «Revisar uno a uno»: primero lo que pide una decisión con destino, después lo que no lo tiene; lo listo no
    /// entra salvo que se abra desde su fila (<paramref name="abiertoDesde"/>), que va el primero.
    /// </summary>
    public static IReadOnlyList<Guid> Cola(IReadOnlyList<FilaReparto> filas, Guid? abiertoDesde = null)
    {
        var cola = filas.Where(f => f.Situacion == SituacionAdjunto.PorRevisar).Concat(filas.Where(f => f.Situacion == SituacionAdjunto.SinDestino))
            .Select(f => f.Adjunto.Id).ToList();
        if (abiertoDesde is { } id && !cola.Contains(id) && filas.Any(f => f.Adjunto.Id == id))
            cola.Insert(0, id);
        return cola;
    }
}
