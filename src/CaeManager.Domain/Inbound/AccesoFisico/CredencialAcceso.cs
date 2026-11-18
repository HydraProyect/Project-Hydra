using CaeManager.Domain.Common;

namespace CaeManager.Domain.Inbound.AccesoFisico;

/// <summary>
/// Credencial de acceso físico de un Trabajador de una Empresa contratista (plan de control de acceso físico, AF-02;
/// decisión M8: una por Trabajador y Tenant TCE). Vive en el Tenant TCE, que es su Tenant propietario: la emite el Gestor
/// CAE para que el Trabajador entre a los Centros de ese Tenant, y otro Tenant TCE que acredite al mismo Trabajador tiene la
/// suya, que este no ve.
///
/// <para>
/// <b>Procedencia sin FK hacia el otro Tenant.</b> <see cref="TenantOrigenId"/> (el Tenant TEC de la Empresa contratista) y
/// <see cref="TrabajadorOrigenId"/> son identificadores del otro Tenant, no claves foráneas, como en
/// <c>EvidenciaInbound</c>: RLS no deja ver sus filas y la credencial tiene que sobrevivir a que el Trabajador cambie o
/// desaparezca en su Tenant. Por lo mismo el sujeto es una instantánea (<see cref="SujetoNombre"/>,
/// <see cref="SujetoDocumentoIdentidad"/>).
/// </para>
///
/// <para>
/// <b>Serie.</b> Nace en 1 y sube en cada <see cref="Regenerar"/>: el QR firmado de AF-03 lleva la serie, y una serie
/// anterior deja de valer al instante. Una credencial <see cref="EstadoCredencialAcceso.Revocada"/> o
/// <see cref="EstadoCredencialAcceso.Caducada"/> no se reactiva: se emite otra (fila nueva, serie 1, otro Id), y el índice
/// único parcial de la tabla solo admite una <see cref="EstadoCredencialAcceso.Emitida"/> por Trabajador y Tenant TCE.
/// </para>
///
/// <para>
/// <b>Sin texto libre.</b> El motivo de revocación es un enum cerrado (<see cref="MotivoRevocacionCredencial"/>): la
/// credencial no guarda razones escritas, que acabarían siendo datos personales o de salud en una tabla que lee la
/// portería. Esta entidad no firma ni genera QR (AF-03) ni registra entradas (AF-06).
/// </para>
/// </summary>
public class CredencialAcceso : EntidadBase
{
    public const int LongitudMaximaSujetoNombre = 300;
    public const int LongitudMaximaDocumentoIdentidad = 50;

    public Guid TenantOrigenId { get; private set; }
    public Guid TrabajadorOrigenId { get; private set; }

    public string SujetoNombre { get; private set; } = string.Empty;
    public string? SujetoDocumentoIdentidad { get; private set; }

    /// <summary>Empieza en 1 y sube al regenerar. Lo que firma el QR junto con el Id.</summary>
    public int Serie { get; private set; }

    public EstadoCredencialAcceso Estado { get; private set; }

    public Guid EmitidaPorUsuarioId { get; private set; }
    public DateTime EmitidaEnUtc { get; private set; }

    /// <summary>Última regeneración, si hubo alguna. La serie anterior dejó de valer en ese momento.</summary>
    public DateTime? RegeneradaEnUtc { get; private set; }
    public Guid? RegeneradaPorUsuarioId { get; private set; }

    public MotivoRevocacionCredencial? MotivoRevocacion { get; private set; }
    public DateTime? RevocadaEnUtc { get; private set; }
    public Guid? RevocadaPorUsuarioId { get; private set; }

    public DateTime? CaducadaEnUtc { get; private set; }

    private CredencialAcceso()
    {
    }

    /// <summary>Instantánea del sujeto: el Trabajador en el Tenant TEC, tal como lo conoce la evidencia que lo acredita.</summary>
    public sealed record Sujeto(Guid TenantOrigenId, Guid TrabajadorOrigenId, string Nombre, string? DocumentoIdentidad);

    public static CredencialAcceso Emitir(Sujeto sujeto, Guid emitidaPorUsuarioId, DateTime emitidaEnUtc)
    {
        ArgumentNullException.ThrowIfNull(sujeto);
        ExigirId(sujeto.TenantOrigenId, "el Tenant de la Empresa contratista");
        ExigirId(sujeto.TrabajadorOrigenId, "el Trabajador");
        ExigirId(emitidaPorUsuarioId, "el usuario que emite");
        ValidarSujeto(sujeto.Nombre, sujeto.DocumentoIdentidad);

        return new CredencialAcceso
        {
            TenantOrigenId = sujeto.TenantOrigenId,
            TrabajadorOrigenId = sujeto.TrabajadorOrigenId,
            SujetoNombre = sujeto.Nombre.Trim(),
            SujetoDocumentoIdentidad = LimpiarDocumento(sujeto.DocumentoIdentidad),
            Serie = 1,
            Estado = EstadoCredencialAcceso.Emitida,
            EmitidaPorUsuarioId = emitidaPorUsuarioId,
            EmitidaEnUtc = emitidaEnUtc,
        };
    }

    /// <summary>
    /// Invalida la serie anterior: sube la serie y refresca la instantánea del sujeto (un nombre o un documento corregido
    /// en el Tenant TEC llega al carné nuevo). Solo una credencial <see cref="EstadoCredencialAcceso.Emitida"/>; una
    /// revocada o caducada no vuelve, se emite otra.
    /// </summary>
    public void Regenerar(string nombre, string? documentoIdentidad, Guid usuarioId, DateTime momentoUtc)
    {
        ExigirId(usuarioId, "el usuario que regenera");
        ValidarSujeto(nombre, documentoIdentidad);
        if (Estado != EstadoCredencialAcceso.Emitida)
            throw new InvalidOperationException("Solo una credencial emitida se regenera.");

        Serie++;
        SujetoNombre = nombre.Trim();
        SujetoDocumentoIdentidad = LimpiarDocumento(documentoIdentidad);
        RegeneradaEnUtc = momentoUtc;
        RegeneradaPorUsuarioId = usuarioId;
    }

    public void Revocar(MotivoRevocacionCredencial motivo, Guid usuarioId, DateTime momentoUtc)
    {
        if (!Enum.IsDefined(motivo))
            throw new ArgumentException("El motivo de la revocación no es válido.", nameof(motivo));
        ExigirId(usuarioId, "el usuario que revoca");
        if (Estado != EstadoCredencialAcceso.Emitida)
            throw new InvalidOperationException("Solo una credencial emitida se revoca.");

        Estado = EstadoCredencialAcceso.Revocada;
        MotivoRevocacion = motivo;
        RevocadaEnUtc = momentoUtc;
        RevocadaPorUsuarioId = usuarioId;
    }

    /// <summary>Paso por vencimiento, sin usuario: lo da una regla de la plataforma, no una persona.</summary>
    public void Caducar(DateTime momentoUtc)
    {
        if (Estado != EstadoCredencialAcceso.Emitida)
            throw new InvalidOperationException("Solo una credencial emitida caduca.");

        Estado = EstadoCredencialAcceso.Caducada;
        CaducadaEnUtc = momentoUtc;
    }

    private static void ValidarSujeto(string nombre, string? documentoIdentidad)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("La credencial necesita el nombre de su sujeto.", nameof(nombre));
        if (nombre.Trim().Length > LongitudMaximaSujetoNombre)
            throw new ArgumentException($"El nombre del sujeto no puede superar {LongitudMaximaSujetoNombre} caracteres.", nameof(nombre));
        if (documentoIdentidad?.Trim().Length > LongitudMaximaDocumentoIdentidad)
            throw new ArgumentException(
                $"El documento de identidad no puede superar {LongitudMaximaDocumentoIdentidad} caracteres.", nameof(documentoIdentidad));
    }

    private static string? LimpiarDocumento(string? documentoIdentidad) =>
        string.IsNullOrWhiteSpace(documentoIdentidad) ? null : documentoIdentidad.Trim();

    private static void ExigirId(Guid id, string que)
    {
        if (id == Guid.Empty)
            throw new ArgumentException($"La credencial necesita {que}.");
    }
}

public enum EstadoCredencialAcceso
{
    /// <summary>Vale (con su serie vigente). Es la única que se regenera, se revoca o caduca.</summary>
    Emitida = 0,

    Revocada = 1,
    Caducada = 2
}

/// <summary>
/// Motivo cerrado de una revocación. Nunca texto libre. DECISION provisional de AF-02 (Chris no fijó la lista): los
/// valores cubren lo operativo sin dato personal; ampliar es añadir un valor y su CHECK.
/// </summary>
public enum MotivoRevocacionCredencial
{
    PerdidaOExtravio = 0,
    UsoIndebido = 1,
    FinDeLaRelacion = 2,
    Otro = 3
}

public interface ICredencialAccesoRepository
{
    Task<CredencialAcceso?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>La credencial Emitida de ese Trabajador de ese Tenant TEC en el Tenant TCE actual, si la hay.</summary>
    Task<CredencialAcceso?> ObtenerEmitidaDelTrabajadorAsync(
        Guid tenantOrigenId, Guid trabajadorOrigenId, CancellationToken cancellationToken = default);

    void Agregar(CredencialAcceso credencial);
}
