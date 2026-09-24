namespace CaeManager.Domain.Documentos;

/// <summary>
/// Lo que se sabe de la vigencia de un <see cref="Documento"/>: un estado de
/// <see cref="EstadoVigenciaDocumento"/> y, solo cuando ese estado es
/// <see cref="EstadoVigenciaDocumento.VenceEnFecha"/>, la fecha.
///
/// <para>
/// Existe como tipo, y no como una fecha nullable, para que «no caduca» y «sin
/// confirmar» no puedan volver a representarse igual, y para que el estado
/// incoherente —«vence en fecha» sin fecha, o una fecha colgando de otro
/// estado— no se pueda ni construir. Misma forma que
/// <see cref="VigenciaEnPlataforma"/>.
/// </para>
/// </summary>
public readonly record struct VigenciaDocumento
{
    private VigenciaDocumento(EstadoVigenciaDocumento estado, DateOnly? fechaVencimiento)
    {
        Estado = estado;
        FechaVencimiento = fechaVencimiento;
    }

    public EstadoVigenciaDocumento Estado { get; }

    /// <summary>Solo tiene valor cuando <see cref="Estado"/> es <c>VenceEnFecha</c>.</summary>
    public DateOnly? FechaVencimiento { get; }

    /// <summary>
    /// Nadie la ha confirmado. Es también el valor por defecto del struct: un
    /// <c>default(VigenciaDocumento)</c> olvidado no puede pasar por «no caduca».
    /// </summary>
    public static VigenciaDocumento SinConfirmar { get; } =
        new(EstadoVigenciaDocumento.SinConfirmar, null);

    /// <summary>Confirmado que el documento no caduca.</summary>
    public static VigenciaDocumento NoCaduca { get; } =
        new(EstadoVigenciaDocumento.NoCaduca, null);

    /// <summary>Vale hasta esa fecha, inclusive.</summary>
    public static VigenciaDocumento VenceEl(DateOnly fecha) =>
        new(EstadoVigenciaDocumento.VenceEnFecha, fecha);

    /// <summary>
    /// Una fecha si la hay; si no, <see cref="SinConfirmar"/>. Es la traducción
    /// honesta de una fecha opcional que llega de un cálculo, de la IA o de un
    /// formulario: la ausencia de fecha nunca se lee como «no caduca».
    /// </summary>
    public static VigenciaDocumento DesdeFechaOpcional(DateOnly? fecha) =>
        fecha is { } f ? VenceEl(f) : SinConfirmar;

    /// <summary>
    /// Reconstruye el valor desde las dos columnas de la base de datos. Una fila
    /// incoherente tiene que reventar aquí, donde se lee, y no convertirse en un
    /// semáforo verde tres capas más arriba (la restricción
    /// <c>CK_Documentos_EstadoVigenciaCoherente</c> impide que llegue a existir).
    /// </summary>
    public static VigenciaDocumento Rehidratar(EstadoVigenciaDocumento estado, DateOnly? fechaVencimiento) =>
        estado switch
        {
            EstadoVigenciaDocumento.VenceEnFecha when fechaVencimiento is null =>
                throw new InvalidOperationException(
                    "Una vigencia 'vence en fecha' sin fecha no es interpretable."),
            EstadoVigenciaDocumento.VenceEnFecha => VenceEl(fechaVencimiento!.Value),
            EstadoVigenciaDocumento.NoCaduca when fechaVencimiento is not null =>
                throw new InvalidOperationException(
                    "Una vigencia 'no caduca' con fecha es contradictoria."),
            EstadoVigenciaDocumento.NoCaduca => NoCaduca,
            EstadoVigenciaDocumento.SinConfirmar when fechaVencimiento is not null =>
                throw new InvalidOperationException(
                    "Una vigencia 'sin confirmar' con fecha es contradictoria: si hay fecha, alguien la confirmó."),
            EstadoVigenciaDocumento.SinConfirmar => SinConfirmar,
            _ => throw new InvalidOperationException(
                $"Estado de vigencia de documento no reconocido: {(int)estado}.")
        };
}
