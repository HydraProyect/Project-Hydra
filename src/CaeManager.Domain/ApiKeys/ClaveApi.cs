using CaeManager.Domain.Common;

namespace CaeManager.Domain.ApiKeys;

/// <summary>
/// Credencial de acceso a la API pública (P3-29,
/// Project-Hydra-Negocio/MATURITY_REVIEW.md). Se hashea, no se cifra: a diferencia de
/// <c>CredencialAccesoEmpresa</c> (Data Protection, reversible porque hay que
/// volver a mostrarla), la clave en texto plano solo existe en el momento de
/// <see cref="Generar"/> — después solo hace falta compararla, nunca
/// recuperarla, igual que una contraseña.
/// </summary>
public class ClaveApi : EntidadBase
{
    public string NombreDescriptivo { get; private set; } = string.Empty;

    /// <summary>Primeros caracteres de la clave, en claro, solo para que quien la generó la reconozca en el listado.</summary>
    public string PrefijoVisible { get; private set; } = string.Empty;

    public string HashClave { get; private set; } = string.Empty;

    public Guid CreadaPorUsuarioId { get; private set; }
    public DateTime? UltimoUsoUtc { get; private set; }
    public DateTime? RevocadaEnUtc { get; private set; }
    public DateTime CaducaEnUtc { get; private set; }
    public string? MotivoEmision { get; private set; }

    /// <summary>
    /// La ventana de Soporte TALVEG en que se emitió: la delegación y su apertura exacta. Las dos
    /// juntas identifican la ventana, porque reabrir reutiliza la fila
    /// (<see cref="CriterioSoporteClaveApi.PuedeAutenticar"/>). Sin ellas la clave no autentica.
    /// </summary>
    public Guid? VentanaSoporteId { get; private set; }
    public DateTime? VentanaSoporteAbiertaEnUtc { get; private set; }

    public bool EstaActiva => RevocadaEnUtc is null;

    private ClaveApi()
    {
        // Requerido por EF Core.
    }

    public ClaveApi(string nombreDescriptivo, string prefijoVisible, string hashClave, Guid creadaPorUsuarioId,
        DateTime caducaEnUtc, string motivoEmision, Guid? ventanaSoporteId = null, DateTime? ventanaSoporteAbiertaEnUtc = null)
    {
        if (string.IsNullOrWhiteSpace(nombreDescriptivo))
            throw new ArgumentException("La clave necesita un nombre que identifique para qué se usa.", nameof(nombreDescriptivo));
        if (string.IsNullOrWhiteSpace(prefijoVisible))
            throw new ArgumentException("Falta el prefijo visible de la clave.", nameof(prefijoVisible));
        if (string.IsNullOrWhiteSpace(hashClave))
            throw new ArgumentException("Falta el hash de la clave.", nameof(hashClave));
        if (creadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La clave debe registrar quién la generó.", nameof(creadaPorUsuarioId));
        var ahora = DateTime.UtcNow;
        if (caducaEnUtc.Kind != DateTimeKind.Utc || caducaEnUtc <= ahora || caducaEnUtc > ahora.AddDays(365))
            throw new ArgumentException("La caducidad debe estar en el futuro y no superar 365 días.", nameof(caducaEnUtc));
        if (string.IsNullOrWhiteSpace(motivoEmision) || motivoEmision.Length > 500)
            throw new ArgumentException("Indica el motivo de la emisión, con un máximo de 500 caracteres.", nameof(motivoEmision));

        NombreDescriptivo = nombreDescriptivo.Trim();
        PrefijoVisible = prefijoVisible;
        HashClave = hashClave;
        CreadaPorUsuarioId = creadaPorUsuarioId;
        CaducaEnUtc = caducaEnUtc;
        MotivoEmision = motivoEmision.Trim();
        VentanaSoporteId = ventanaSoporteId;
        VentanaSoporteAbiertaEnUtc = ventanaSoporteAbiertaEnUtc;
    }

    public void Revocar()
    {
        if (!EstaActiva) return;
        RevocadaEnUtc = DateTime.UtcNow;
    }

    public void RegistrarUso() => UltimoUsoUtc = DateTime.UtcNow;

    public bool EstaVigente(DateTime ahoraUtc) => EstaActiva && ahoraUtc < CaducaEnUtc;
}
