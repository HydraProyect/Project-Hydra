using CaeManager.Domain.Common;

namespace CaeManager.Domain.AsistenteIa;

/// <summary>
/// Un turno de la conversación de una <see cref="TareaAsistente"/>. Inmutable.
///
/// <para>
/// <b>Dos textos, y cada uno dice lo que es.</b> <see cref="TextoOriginal"/> es
/// el texto en claro: lo que escribió la persona —la evidencia de su
/// solicitud— o lo que el asistente le mostró. <see cref="TextoEnmascarado"/>,
/// opcional, es la versión con los identificadores sustituidos por marcadores
/// (<c>[DOC_1]</c>) que cruzó hacia o desde el proveedor de IA. Nunca se guarda
/// uno en el campo del otro.
/// </para>
/// </summary>
public class TurnoTareaAsistente : EntidadConTenant
{
    public const int LongitudMaximaTexto = 8000;

    public Guid TareaAsistenteId { get; private set; }

    /// <summary>Orden del turno en la conversación, desde 1.</summary>
    public int Numero { get; private set; }

    public AutorTurnoTareaAsistente Autor { get; private set; }

    /// <summary>Texto en claro, tal como lo escribió la persona o como se le mostró.</summary>
    public string TextoOriginal { get; private set; } = string.Empty;

    /// <summary>Versión enmascarada que cruzó hacia o desde el proveedor de IA, si la hubo.</summary>
    public string? TextoEnmascarado { get; private set; }

    public DateTime FechaUtc { get; private set; }

    private TurnoTareaAsistente()
    {
    }

    internal TurnoTareaAsistente(
        Guid tareaAsistenteId,
        int numero,
        AutorTurnoTareaAsistente autor,
        string textoOriginal,
        string? textoEnmascarado,
        DateTime fechaUtc)
    {
        if (!Enum.IsDefined(autor))
            throw new ArgumentOutOfRangeException(nameof(autor), autor, "Autor de turno desconocido.");
        if (string.IsNullOrWhiteSpace(textoOriginal))
            throw new ArgumentException("Un turno no puede estar vacío.", nameof(textoOriginal));
        if (textoOriginal.Length > LongitudMaximaTexto)
            throw new ArgumentException($"Un turno no puede superar {LongitudMaximaTexto} caracteres.", nameof(textoOriginal));
        if (textoEnmascarado is not null)
        {
            if (string.IsNullOrWhiteSpace(textoEnmascarado))
                throw new ArgumentException("El texto enmascarado, si se guarda, no puede estar vacío.", nameof(textoEnmascarado));
            if (textoEnmascarado.Length > LongitudMaximaTexto)
                throw new ArgumentException($"El texto enmascarado no puede superar {LongitudMaximaTexto} caracteres.", nameof(textoEnmascarado));
        }

        TareaAsistenteId = tareaAsistenteId;
        Numero = numero;
        Autor = autor;
        // Sin Trim: es evidencia de lo que se escribió, y se guarda tal cual.
        TextoOriginal = textoOriginal;
        TextoEnmascarado = textoEnmascarado;
        FechaUtc = fechaUtc;
    }
}
