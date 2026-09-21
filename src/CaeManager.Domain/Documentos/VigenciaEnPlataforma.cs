namespace CaeManager.Domain.Documentos;

/// <summary>
/// Lo que se sabe de la vigencia de un Documento en una plataforma CAE: un
/// estado de <see cref="EstadoVigenciaEnPlataforma"/> y, solo cuando ese estado
/// es <see cref="EstadoVigenciaEnPlataforma.VenceEnFecha"/>, la fecha.
///
/// <para>
/// Existe como tipo, y no como dos campos sueltos, para que el estado
/// incoherente —«vence en fecha» sin fecha, o una fecha colgando de un «no
/// vence aquí»— no se pueda ni construir. Los tres constructores estáticos son
/// la única forma de obtener uno.
/// </para>
/// </summary>
public readonly record struct VigenciaEnPlataforma
{
    private VigenciaEnPlataforma(EstadoVigenciaEnPlataforma estado, DateOnly? fechaVencimiento)
    {
        Estado = estado;
        FechaVencimiento = fechaVencimiento;
    }

    public EstadoVigenciaEnPlataforma Estado { get; }

    /// <summary>Solo tiene valor cuando <see cref="Estado"/> es <c>VenceEnFecha</c>.</summary>
    public DateOnly? FechaVencimiento { get; }

    /// <summary>Nadie lo ha confirmado todavía. Es el estado en que nace toda acreditación.</summary>
    public static VigenciaEnPlataforma SinConfirmar { get; } =
        new(EstadoVigenciaEnPlataforma.SinConfirmar, null);

    /// <summary>La plataforma no exige vigencia para este documento.</summary>
    public static VigenciaEnPlataforma NoVenceAqui { get; } =
        new(EstadoVigenciaEnPlataforma.NoVenceAqui, null);

    /// <summary>Vale hasta esa fecha, inclusive.</summary>
    public static VigenciaEnPlataforma VenceEl(DateOnly fecha) =>
        new(EstadoVigenciaEnPlataforma.VenceEnFecha, fecha);

    /// <summary>
    /// Reconstruye el valor desde las dos columnas de la base de datos. Solo lo
    /// usa la capa de persistencia: una fila incoherente —escrita por una
    /// migración a medias o a mano— tiene que reventar aquí, donde se lee, y no
    /// convertirse en un semáforo verde tres capas más arriba.
    /// </summary>
    public static VigenciaEnPlataforma Rehidratar(EstadoVigenciaEnPlataforma estado, DateOnly? fechaVencimiento) =>
        estado switch
        {
            EstadoVigenciaEnPlataforma.VenceEnFecha when fechaVencimiento is null =>
                throw new InvalidOperationException(
                    "Una vigencia 'vence en fecha' sin fecha no es interpretable: no se puede decidir si el Trabajador entra."),
            EstadoVigenciaEnPlataforma.VenceEnFecha => VenceEl(fechaVencimiento!.Value),
            EstadoVigenciaEnPlataforma.NoVenceAqui when fechaVencimiento is not null =>
                throw new InvalidOperationException(
                    "Una vigencia 'no vence aquí' con fecha es contradictoria: una de las dos cosas está mal."),
            EstadoVigenciaEnPlataforma.NoVenceAqui => NoVenceAqui,
            EstadoVigenciaEnPlataforma.SinConfirmar when fechaVencimiento is not null =>
                throw new InvalidOperationException(
                    "Una vigencia 'sin confirmar' con fecha es contradictoria: si hay fecha, alguien la confirmó."),
            EstadoVigenciaEnPlataforma.SinConfirmar => SinConfirmar,
            // Un entero que no es ninguno de los tres estados no es "sin
            // confirmar": es una fila corrupta. Degradarla en silencio la haría
            // además NO bloqueante en el semáforo del Centro, que es el peor
            // sitio posible para tragarse un dato ininteligible.
            _ => throw new InvalidOperationException(
                $"Estado de vigencia en plataforma no reconocido: {(int)estado}.")
        };

    /// <summary>
    /// ¿Está vencida a día de hoy <b>en esta plataforma</b>? Solo dice que sí
    /// cuando hay una fecha y ya pasó.
    ///
    /// <para>
    /// «Sin confirmar» NO es estar vencida: es no saberlo, y eso se responde
    /// aparte con <see cref="EstaSinConfirmar"/>. Meter las dos cosas en el
    /// mismo booleano volvería a perder la distinción que este tipo existe para
    /// conservar.
    /// </para>
    /// </summary>
    public bool EstaVencidaEl(DateOnly hoy) =>
        FechaVencimiento is { } fecha && fecha.DayNumber < hoy.DayNumber;

    public bool EstaSinConfirmar => Estado == EstadoVigenciaEnPlataforma.SinConfirmar;
}
