using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Plantilla de respuesta reutilizable en la bandeja de correo compartida.
/// ClienteId null = macro genérica del tenant, visible al responder
/// cualquier conversación; poblado = específica de ese Cliente (ver
/// ObtenerMacrosQuery para el criterio de qué macros se ofrecen).
/// </summary>
public class MacroRespuesta : EntidadBase
{
    public const int LongitudMaximaTitulo = 150;

    public Guid? ClienteId { get; private set; }
    public string Titulo { get; private set; } = string.Empty;
    public string CuerpoHtml { get; private set; } = string.Empty;

    private MacroRespuesta()
    {
    }

    public MacroRespuesta(string titulo, string cuerpoHtml, Guid? clienteId = null)
    {
        EstablecerTitulo(titulo);
        EstablecerCuerpo(cuerpoHtml);
        ClienteId = clienteId;
    }

    public void Actualizar(string titulo, string cuerpoHtml, Guid? clienteId)
    {
        EstablecerTitulo(titulo);
        EstablecerCuerpo(cuerpoHtml);
        ClienteId = clienteId;
    }

    private void EstablecerTitulo(string titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo))
            throw new ArgumentException("La macro debe tener un título.", nameof(titulo));

        var normalizado = titulo.Trim();
        if (normalizado.Length > LongitudMaximaTitulo)
            throw new ArgumentException($"El título no puede superar {LongitudMaximaTitulo} caracteres.", nameof(titulo));

        Titulo = normalizado;
    }

    private void EstablecerCuerpo(string cuerpoHtml)
    {
        if (string.IsNullOrWhiteSpace(cuerpoHtml))
            throw new ArgumentException("La macro no puede tener el contenido vacío.", nameof(cuerpoHtml));

        CuerpoHtml = cuerpoHtml;
    }
}
