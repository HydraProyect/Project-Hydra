using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.EvidenciasInbound;

/// <summary>
/// Copia de evidencia validada de Inbound M1 (PR 4; DN-4 (b) del 2026-10-23): lo que el Tenant del Cliente empresarial
/// valida de un Documento original del Tenant propietario de la Empresa contratista, leído por un contrato de acceso y una
/// autorización de lectura. Vive en el Tenant del Cliente empresarial, que es su Tenant propietario: la copia es suya y
/// no del Tenant de la Empresa contratista, que conserva su original.
///
/// <para>
/// <b>Procedencia sin FK hacia el otro Tenant.</b> <see cref="TenantOrigenId"/>, <see cref="DocumentoOrigenId"/>,
/// <see cref="AutorizacionId"/> y <see cref="TrabajadorOrigenId"/> son identificadores del Tenant de la Empresa contratista
/// copiados de la lectura, no claves foráneas: RLS no deja ver sus filas, y la evidencia tiene que sobrevivir a que el
/// original cambie, se sustituya o la autorización se revoque. Por lo mismo el sujeto es una instantánea
/// (<see cref="SujetoNombre"/>, <see cref="SujetoDocumentoIdentidad"/>). Solo <see cref="ContratoAccesoId"/> y
/// <see cref="TipoDocumentoId"/> (catálogo del Tenant del Cliente empresarial, elegido por el validador) tienen FK, las
/// dos compuestas con TenantId.
/// </para>
///
/// <para>
/// <b>Restricción médica.</b> Una evidencia <see cref="SoloConclusionAptitud"/> (el tipo elegido lleva la marca o el
/// original era de un tipo marcado) no lleva archivo ni huella y sí la conclusión transcrita; cualquier otra lleva
/// archivo (copiado al almacenamiento del Tenant del Cliente empresarial, nunca la referencia del original) y su huella
/// SHA-256, y ninguna conclusión. Lo garantiza <see cref="Registrar"/> y lo repite el CHECK
/// <c>CK_EvidenciasInbound_ArchivoSegunConclusion</c>.
/// </para>
///
/// <para>
/// <b>Estado.</b> Reutiliza <see cref="EstadoAcreditacion"/> y <see cref="CausaRechazoAcreditacion"/>: nace
/// <see cref="EstadoAcreditacion.Aceptada"/> (solo se registra lo validado), <see cref="Rechazar"/> lleva a
/// <see cref="EstadoAcreditacion.Rechazada"/> con causa y motivo obligatorios, como
/// <see cref="AcreditacionDocumentoPlataforma.Rechazar"/>, y <see cref="Validar"/> la devuelve a
/// <see cref="EstadoAcreditacion.Aceptada"/>, como <see cref="AcreditacionDocumentoPlataforma.MarcarAceptada"/>. Los demás
/// estados son de una plataforma destino y aquí no se usan. Revocar la autorización no toca la evidencia.
/// </para>
/// </summary>
public class EvidenciaInbound : EntidadBase
{
    public const int LongitudMaximaSujetoNombre = 300;
    public const int LongitudMaximaDocumentoIdentidad = 50;
    public const int LongitudMaximaArchivoUrl = 500;
    public const int LongitudHuellaSha256 = 64;
    public const int LongitudMaximaMotivoRechazo = 1000;

    public Guid ContratoAccesoId { get; private set; }
    public Guid TenantOrigenId { get; private set; }
    public Guid DocumentoOrigenId { get; private set; }
    public Guid AutorizacionId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }

    /// <summary>Null si el sujeto es la Empresa contratista; si no, el Trabajador en el Tenant de origen (sin FK).</summary>
    public Guid? TrabajadorOrigenId { get; private set; }

    public string SujetoNombre { get; private set; } = string.Empty;
    public string? SujetoDocumentoIdentidad { get; private set; }
    public DateOnly FechaEmision { get; private set; }
    public DateOnly? FechaVencimiento { get; private set; }

    /// <summary>Referencia del archivo copiado al almacenamiento del Tenant del Cliente empresarial. Null en una evidencia médica.</summary>
    public string? ArchivoUrl { get; private set; }

    /// <summary>SHA-256 en hexadecimal minúsculo del binario copiado. Null en una evidencia médica.</summary>
    public string? HuellaSha256 { get; private set; }

    public bool SoloConclusionAptitud { get; private set; }

    /// <summary>La conclusión transcrita del certificado. Solo en una evidencia médica.</summary>
    public ConclusionAptitud? ConclusionAptitud { get; private set; }

    public Guid ValidadaPorUsuarioId { get; private set; }
    public DateTime ValidadaEnUtc { get; private set; }

    public EstadoAcreditacion Estado { get; private set; }
    public CausaRechazoAcreditacion? CausaRechazo { get; private set; }
    public string? MotivoRechazo { get; private set; }
    public Guid? RechazadaPorUsuarioId { get; private set; }
    public DateTime? RechazadaEnUtc { get; private set; }

    private EvidenciaInbound()
    {
    }

    /// <summary>Procedencia de la lectura: de qué Tenant, Documento, autorización y contrato sale la evidencia.</summary>
    public sealed record Procedencia(Guid ContratoAccesoId, Guid TenantOrigenId, Guid DocumentoOrigenId, Guid AutorizacionId);

    /// <summary>Instantánea del sujeto tomada de la lectura.</summary>
    public sealed record Sujeto(Guid? TrabajadorOrigenId, string Nombre, string? DocumentoIdentidad);

    /// <summary>El archivo ya copiado al almacenamiento del Tenant del Cliente empresarial, con su huella.</summary>
    public sealed record ArchivoCopiado(string ArchivoUrl, string HuellaSha256);

    /// <summary>
    /// Registra lo validado. <paramref name="soloConclusionAptitud"/> exige <paramref name="conclusion"/> y prohíbe
    /// <paramref name="archivo"/>; lo contrario exige el archivo y prohíbe la conclusión. Una evidencia médica de un
    /// Trabajador exige además su documento de identidad: el reconocimiento médico solo lleva conclusión, nombre y NIE.
    /// </summary>
    public static EvidenciaInbound Registrar(
        Procedencia procedencia,
        Guid tipoDocumentoId,
        Sujeto sujeto,
        DateOnly fechaEmision,
        DateOnly? fechaVencimiento,
        bool soloConclusionAptitud,
        ConclusionAptitud? conclusion,
        ArchivoCopiado? archivo,
        Guid validadaPorUsuarioId,
        DateTime validadaEnUtc)
    {
        ArgumentNullException.ThrowIfNull(procedencia);
        ArgumentNullException.ThrowIfNull(sujeto);
        ExigirId(procedencia.ContratoAccesoId, "el contrato de acceso");
        ExigirId(procedencia.TenantOrigenId, "el Tenant de la Empresa contratista");
        ExigirId(procedencia.DocumentoOrigenId, "el Documento original");
        ExigirId(procedencia.AutorizacionId, "la autorización de lectura");
        ExigirId(tipoDocumentoId, "el tipo de documento");
        ExigirId(validadaPorUsuarioId, "el usuario que valida");
        if (string.IsNullOrWhiteSpace(sujeto.Nombre))
            throw new ArgumentException("La evidencia necesita el nombre de su sujeto.", nameof(sujeto));
        if (sujeto.Nombre.Length > LongitudMaximaSujetoNombre)
            throw new ArgumentException($"El nombre del sujeto no puede superar {LongitudMaximaSujetoNombre} caracteres.", nameof(sujeto));
        if (sujeto.DocumentoIdentidad?.Length > LongitudMaximaDocumentoIdentidad)
            throw new ArgumentException(
                $"El documento de identidad no puede superar {LongitudMaximaDocumentoIdentidad} caracteres.", nameof(sujeto));
        if (fechaVencimiento is not null && fechaVencimiento < fechaEmision)
            throw new ArgumentException("El vencimiento no puede ser anterior a la emisión.", nameof(fechaVencimiento));

        if (soloConclusionAptitud)
        {
            if (archivo is not null)
                throw new InvalidOperationException("Una evidencia de solo conclusión de aptitud no lleva archivo.");
            if (conclusion is null || !Enum.IsDefined(conclusion.Value))
                throw new InvalidOperationException("Una evidencia de solo conclusión de aptitud lleva la conclusión transcrita.");
            if (sujeto.TrabajadorOrigenId is null)
                throw new InvalidOperationException("Una evidencia de solo conclusión de aptitud es de un Trabajador.");
            if (string.IsNullOrWhiteSpace(sujeto.DocumentoIdentidad))
                throw new InvalidOperationException(
                    "Una evidencia de solo conclusión de aptitud lleva el documento de identidad del Trabajador.");
        }
        else
        {
            if (conclusion is not null)
                throw new InvalidOperationException("Solo una evidencia de solo conclusión de aptitud lleva conclusión.");
            if (archivo is null)
                throw new InvalidOperationException("La evidencia lleva el archivo copiado y su huella.");
            if (string.IsNullOrWhiteSpace(archivo.ArchivoUrl) || archivo.ArchivoUrl.Length > LongitudMaximaArchivoUrl)
                throw new ArgumentException("La referencia del archivo copiado no es válida.", nameof(archivo));
            if (!EsHuellaSha256(archivo.HuellaSha256))
                throw new ArgumentException("La huella debe ser un SHA-256 en hexadecimal minúsculo.", nameof(archivo));
        }

        return new EvidenciaInbound
        {
            ContratoAccesoId = procedencia.ContratoAccesoId,
            TenantOrigenId = procedencia.TenantOrigenId,
            DocumentoOrigenId = procedencia.DocumentoOrigenId,
            AutorizacionId = procedencia.AutorizacionId,
            TipoDocumentoId = tipoDocumentoId,
            TrabajadorOrigenId = sujeto.TrabajadorOrigenId,
            SujetoNombre = sujeto.Nombre.Trim(),
            SujetoDocumentoIdentidad = string.IsNullOrWhiteSpace(sujeto.DocumentoIdentidad) ? null : sujeto.DocumentoIdentidad.Trim(),
            FechaEmision = fechaEmision,
            FechaVencimiento = fechaVencimiento,
            SoloConclusionAptitud = soloConclusionAptitud,
            ConclusionAptitud = conclusion,
            ArchivoUrl = archivo?.ArchivoUrl,
            HuellaSha256 = archivo?.HuellaSha256,
            ValidadaPorUsuarioId = validadaPorUsuarioId,
            ValidadaEnUtc = validadaEnUtc,
            Estado = EstadoAcreditacion.Aceptada,
        };
    }

    /// <summary>
    /// Rechazo con causa tipificada y motivo literal, los dos obligatorios, como
    /// <see cref="AcreditacionDocumentoPlataforma.Rechazar"/>. Solo desde Aceptada: un segundo rechazo pisaría el
    /// primero, que se conserva en estas columnas y en la auditoría de la entidad.
    /// </summary>
    public void Rechazar(CausaRechazoAcreditacion causa, string motivoLiteral, Guid usuarioId, DateTime momentoUtc)
    {
        if (!Enum.IsDefined(causa))
            throw new ArgumentException("La causa del rechazo no es válida.", nameof(causa));
        if (string.IsNullOrWhiteSpace(motivoLiteral))
            throw new ArgumentException("El motivo del rechazo es obligatorio.", nameof(motivoLiteral));
        if (motivoLiteral.Length > LongitudMaximaMotivoRechazo)
            throw new ArgumentException($"El motivo no puede superar {LongitudMaximaMotivoRechazo} caracteres.", nameof(motivoLiteral));
        ExigirId(usuarioId, "el usuario que rechaza");
        if (Estado != EstadoAcreditacion.Aceptada)
            throw new InvalidOperationException("Solo una evidencia validada se rechaza.");

        Estado = EstadoAcreditacion.Rechazada;
        CausaRechazo = causa;
        MotivoRechazo = motivoLiteral.Trim();
        RechazadaPorUsuarioId = usuarioId;
        RechazadaEnUtc = momentoUtc;
    }

    /// <summary>Vuelve a validar una evidencia rechazada. El último rechazo se conserva como hecho pasado.</summary>
    public void Validar(Guid usuarioId, DateTime momentoUtc)
    {
        ExigirId(usuarioId, "el usuario que valida");
        if (Estado != EstadoAcreditacion.Rechazada)
            throw new InvalidOperationException("Solo una evidencia rechazada se vuelve a validar.");

        Estado = EstadoAcreditacion.Aceptada;
        ValidadaPorUsuarioId = usuarioId;
        ValidadaEnUtc = momentoUtc;
    }

    public static bool EsHuellaSha256(string? huella) =>
        huella is { Length: LongitudHuellaSha256 } && huella.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ExigirId(Guid id, string que)
    {
        if (id == Guid.Empty)
            throw new ArgumentException($"La evidencia necesita {que}.");
    }
}

/// <summary>Conclusión de un certificado de aptitud médica, la única parte que se transcribe.</summary>
public enum ConclusionAptitud
{
    Apto = 0,
    AptoConRestricciones = 1,
    NoApto = 2
}

public interface IEvidenciaInboundRepository
{
    Task<EvidenciaInbound?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(EvidenciaInbound evidencia);
}
