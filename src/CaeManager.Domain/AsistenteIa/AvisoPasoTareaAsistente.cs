namespace CaeManager.Domain.AsistenteIa;

/// <summary>
/// Aviso de una validación determinista sobre un paso («la letra del DNI no
/// corresponde», «esa fecha ya ha pasado»). Se guarda la gravedad y el texto
/// tal como se mostraron; <see cref="Campo"/> nombra, si procede, el dato del
/// paso al que se refiere.
/// </summary>
public sealed record AvisoPasoTareaAsistente(GravedadAvisoPasoTareaAsistente Gravedad, string Texto, string? Campo = null)
{
    public const int LongitudMaximaTexto = 500;
    public const int LongitudMaximaCampo = 64;

    internal void Validar()
    {
        if (!Enum.IsDefined(Gravedad))
            throw new ArgumentOutOfRangeException(nameof(Gravedad), Gravedad, "Gravedad de aviso desconocida.");
        if (string.IsNullOrWhiteSpace(Texto))
            throw new ArgumentException("Un aviso debe tener texto.", nameof(Texto));
        if (Texto.Length > LongitudMaximaTexto)
            throw new ArgumentException($"El texto de un aviso no puede superar {LongitudMaximaTexto} caracteres.", nameof(Texto));
        if (Campo is not null && (string.IsNullOrWhiteSpace(Campo) || Campo.Length > LongitudMaximaCampo))
            throw new ArgumentException($"El campo de un aviso, si se indica, no puede estar vacío ni superar {LongitudMaximaCampo} caracteres.", nameof(Campo));
    }
}

/// <summary>
/// Lo que una orden entendida aporta a un paso del plan, sin depender de la
/// forma exacta del puerto de interpretación: el identificador estable de la
/// orden del catálogo y sus datos como objeto JSON. Qué orden existe lo decide
/// el catálogo en Application; aquí solo se exige la forma.
/// </summary>
/// <param name="OrdenAsistenteId">Identificador estable de la orden del catálogo (p. ej. <c>alta_centro</c>).</param>
/// <param name="DatosJson">Los datos entendidos de la orden, como objeto JSON.</param>
/// <param name="Resumen">Frase que describe el paso a la persona («Alta de Juan Pérez»), opcional.</param>
/// <param name="CamposPendientes">Datos que faltan para que el paso esté listo.</param>
/// <param name="Avisos">Avisos de las validaciones deterministas.</param>
public sealed record DefinicionPasoTareaAsistente(
    string OrdenAsistenteId,
    string DatosJson,
    string? Resumen,
    IReadOnlyList<string> CamposPendientes,
    IReadOnlyList<AvisoPasoTareaAsistente> Avisos);
