using CaeManager.Domain.Common;

namespace CaeManager.Domain.VigilanciaNormativa;

/// <summary>
/// Aviso de que una publicación del BOE toca una norma de la lista vigilada
/// (PLAN_MVP1_FORMATOS.md tramo 1 bis, corte mínimo). Extiende
/// <see cref="Entity"/>, no <see cref="EntidadConTenant"/>: el BOE es el
/// mismo para todos los tenants, y el aviso es para quien mantiene el
/// catálogo de formatos (el propietario del producto), no para un cliente —
/// mismo tratamiento que <see cref="Tenants.DelegacionTenant"/>.
///
/// Deliberadamente no interpreta nada: solo detecta una coincidencia de
/// texto entre el título de una publicación y el nombre/patrón de una norma
/// vigilada. La revisión —si la publicación afecta de verdad al contenido
/// de algún formato— la hace una persona (<see cref="MarcarRevisado"/>).
/// </summary>
public class AvisoRevisionNormativa : Entity
{
    public const int LongitudMaximaTitulo = 500;
    public const int LongitudMaximaNormaVigilada = 100;
    public const int LongitudMaximaNotasRevision = 1000;

    public string IdentificadorBoe { get; private set; } = string.Empty;
    public DateOnly FechaPublicacion { get; private set; }
    public string Titulo { get; private set; } = string.Empty;
    public string UrlHtml { get; private set; } = string.Empty;

    /// <summary>Nombre de la norma vigilada con la que coincidió (p. ej. "LPRL", "RD 171/2004").</summary>
    public string NormaVigilada { get; private set; } = string.Empty;

    public DateTime DetectadoEnUtc { get; private set; }

    public bool Revisado { get; private set; }
    public DateTime? RevisadoEnUtc { get; private set; }
    public Guid? RevisadoPorUsuarioId { get; private set; }
    public string? NotasRevision { get; private set; }

    private AvisoRevisionNormativa()
    {
        // Requerido por EF Core.
    }

    public AvisoRevisionNormativa(
        string identificadorBoe, DateOnly fechaPublicacion, string titulo, string urlHtml,
        string normaVigilada, DateTime detectadoEnUtc)
    {
        if (string.IsNullOrWhiteSpace(identificadorBoe))
            throw new ArgumentException("El identificador de la publicación del BOE es obligatorio.", nameof(identificadorBoe));

        if (string.IsNullOrWhiteSpace(titulo))
            throw new ArgumentException("El título de la publicación es obligatorio.", nameof(titulo));
        var tituloNormalizado = titulo.Trim();
        if (tituloNormalizado.Length > LongitudMaximaTitulo)
            throw new ArgumentException($"El título no puede superar {LongitudMaximaTitulo} caracteres.", nameof(titulo));

        if (string.IsNullOrWhiteSpace(urlHtml))
            throw new ArgumentException("La URL de la publicación es obligatoria.", nameof(urlHtml));

        if (string.IsNullOrWhiteSpace(normaVigilada))
            throw new ArgumentException("La norma vigilada con la que coincidió es obligatoria.", nameof(normaVigilada));
        var normaNormalizada = normaVigilada.Trim();
        if (normaNormalizada.Length > LongitudMaximaNormaVigilada)
            throw new ArgumentException($"La norma vigilada no puede superar {LongitudMaximaNormaVigilada} caracteres.", nameof(normaVigilada));

        IdentificadorBoe = identificadorBoe.Trim();
        FechaPublicacion = fechaPublicacion;
        Titulo = tituloNormalizado;
        UrlHtml = urlHtml.Trim();
        NormaVigilada = normaNormalizada;
        DetectadoEnUtc = detectadoEnUtc;
    }

    /// <summary>
    /// Registra que una persona revisó el aviso y decidió qué hacer con él.
    /// No traslada ninguna conclusión al catálogo de formatos — eso, si
    /// procede, es un cambio aparte hecho a mano en CATALOGO_FORMATOS_PRL.md.
    /// </summary>
    public void MarcarRevisado(Guid usuarioId, string? notas, DateTime ahoraUtc)
    {
        if (Revisado) return;

        if (notas is { Length: > LongitudMaximaNotasRevision })
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotasRevision} caracteres.", nameof(notas));

        Revisado = true;
        RevisadoEnUtc = ahoraUtc;
        RevisadoPorUsuarioId = usuarioId;
        NotasRevision = string.IsNullOrWhiteSpace(notas) ? null : notas.Trim();
    }
}
