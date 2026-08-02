using CaeManager.Domain.Common;

namespace CaeManager.Domain.ApiKeys;

/// <summary>
/// Credencial de acceso a la API pública (P3-29,
/// docs/business/MATURITY_REVIEW.md). Se hashea, no se cifra: a diferencia de
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

    public bool EstaActiva => RevocadaEnUtc is null;

    private ClaveApi()
    {
        // Requerido por EF Core.
    }

    public ClaveApi(string nombreDescriptivo, string prefijoVisible, string hashClave, Guid creadaPorUsuarioId)
    {
        if (string.IsNullOrWhiteSpace(nombreDescriptivo))
            throw new ArgumentException("La clave necesita un nombre que identifique para qué se usa.", nameof(nombreDescriptivo));
        if (string.IsNullOrWhiteSpace(prefijoVisible))
            throw new ArgumentException("Falta el prefijo visible de la clave.", nameof(prefijoVisible));
        if (string.IsNullOrWhiteSpace(hashClave))
            throw new ArgumentException("Falta el hash de la clave.", nameof(hashClave));
        if (creadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La clave debe registrar quién la generó.", nameof(creadaPorUsuarioId));

        NombreDescriptivo = nombreDescriptivo.Trim();
        PrefijoVisible = prefijoVisible;
        HashClave = hashClave;
        CreadaPorUsuarioId = creadaPorUsuarioId;
    }

    public void Revocar()
    {
        if (!EstaActiva) return;
        RevocadaEnUtc = DateTime.UtcNow;
    }

    public void RegistrarUso() => UltimoUsoUtc = DateTime.UtcNow;
}
