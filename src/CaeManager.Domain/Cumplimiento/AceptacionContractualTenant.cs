using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.Cumplimiento;

/// <summary>
/// Evidencia de que el Administrador del Tenant propietario aceptó una versión concreta de un documento
/// contractual (#2009): qué documento, qué versión, el SHA-256 del texto aceptado, quién lo aceptó, si
/// declaró tener facultades, desde qué IP, cuándo y, si se mostró, el importe de permanencia.
///
/// <para>
/// Es del Tenant propietario (<see cref="EntidadConTenant"/>, con RLS), a diferencia de
/// <see cref="AceptacionTerminos"/>, que es un hecho del usuario frente a la plataforma: lo que se
/// acepta aquí obliga a la organización. Solo se añade: nunca se edita ni se borra. La base de datos lo
/// impone sin conceder UPDATE ni DELETE a ningún rol de la aplicación. El acuse por correo va en una
/// tabla aparte (<see cref="AcuseAceptacionContractualTenant"/>), justamente para no tener que
/// actualizar esta fila.
/// </para>
///
/// <para>
/// La aceptación del alta autónoma nace de la evidencia previa de la Solicitud de alta
/// (<see cref="AceptacionCondicionesAlta"/>), que el alta materializa aquí en la misma transacción que
/// crea el Tenant. <see cref="SolicitudAltaId"/> la enlaza con su origen y hace idempotente ese paso.
/// </para>
/// </summary>
public class AceptacionContractualTenant : EntidadConTenant
{
    public const int LongitudMaximaDocumento = AceptacionCondicionesAlta.LongitudMaximaDocumento;
    public const int LongitudMaximaVersion = AceptacionCondicionesAlta.LongitudMaximaVersion;
    public const int LongitudHash = AceptacionCondicionesAlta.LongitudHash;
    public const int LongitudMaximaIp = AceptacionCondicionesAlta.LongitudMaximaIp;
    public const int PrecisionImporte = 12;
    public const int EscalaImporte = 2;

    /// <summary>La cuenta que aceptó: el Administrador del Tenant propietario.</summary>
    public Guid UsuarioId { get; private set; }

    /// <summary>La Solicitud de alta de la que procede, o <c>null</c> si se aceptó fuera del alta.</summary>
    public Guid? SolicitudAltaId { get; private set; }

    public string Documento { get; private set; } = string.Empty;
    public string Version { get; private set; } = string.Empty;
    public string HashSha256 { get; private set; } = string.Empty;
    public bool DeclaraFacultades { get; private set; }
    public string Ip { get; private set; } = string.Empty;
    public DateTime AceptadaEnUtc { get; private set; }

    /// <summary>
    /// El importe de permanencia que se mostró al aceptar, sin impuestos, tal como se mostró. Lo calcula
    /// quien pinta la oferta; aquí solo se guarda como evidencia. <c>null</c> si no se mostró ninguno.
    /// </summary>
    public decimal? ImportePermanencia { get; private set; }

    /// <summary>Cuándo se guardó la evidencia en el Tenant, que en el alta es posterior a la aceptación.</summary>
    public DateTime RegistradaEnUtc { get; private set; }

    private AceptacionContractualTenant()
    {
        // Requerido por EF Core.
    }

    public AceptacionContractualTenant(
        Guid usuarioId, Guid? solicitudAltaId, AceptacionDeclarada declarada, decimal? importePermanencia, DateTime registradaEnUtc)
    {
        ArgumentNullException.ThrowIfNull(declarada);
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("La aceptación debe pertenecer a una cuenta.", nameof(usuarioId));
        if (solicitudAltaId == Guid.Empty)
            throw new ArgumentException("La Solicitud de alta de origen no puede ser vacía.", nameof(solicitudAltaId));
        if (importePermanencia is < 0m || importePermanencia is { } importe && decimal.Round(importe, EscalaImporte) != importe)
            throw new ArgumentException("El importe de permanencia no puede ser negativo ni tener más de dos decimales.", nameof(importePermanencia));

        // Valida con las mismas reglas que la evidencia previa de la Solicitud de alta: un dato que allí
        // no se admitiría tampoco se admite aquí.
        var validada = new AceptacionCondicionesAlta(Guid.Empty, declarada);
        var registrada = SolicitudAlta.ExigirUtc(registradaEnUtc, nameof(registradaEnUtc));
        if (validada.AceptadaEnUtc > registrada)
            throw new ArgumentException("Una aceptación no puede ser posterior a su registro.", nameof(declarada));

        UsuarioId = usuarioId;
        SolicitudAltaId = solicitudAltaId;
        Documento = validada.Documento;
        Version = validada.Version;
        HashSha256 = validada.HashSha256;
        DeclaraFacultades = validada.DeclaraFacultades;
        Ip = validada.Ip;
        AceptadaEnUtc = validada.AceptadaEnUtc;
        ImportePermanencia = importePermanencia;
        RegistradaEnUtc = registrada;
    }
}

/// <summary>
/// Un envío por correo del texto aceptado (acuse) de una <see cref="AceptacionContractualTenant"/>.
/// Solo se añade: cada envío o reenvío es una fila. Guarda a qué cuenta se envió, no su dirección.
/// </summary>
public class AcuseAceptacionContractualTenant : EntidadConTenant
{
    public Guid AceptacionContractualTenantId { get; private set; }
    public Guid UsuarioDestinatarioId { get; private set; }
    public DateTime EnviadoEnUtc { get; private set; }

    private AcuseAceptacionContractualTenant()
    {
        // Requerido por EF Core.
    }

    public AcuseAceptacionContractualTenant(Guid aceptacionContractualTenantId, Guid usuarioDestinatarioId, DateTime enviadoEnUtc)
    {
        if (aceptacionContractualTenantId == Guid.Empty)
            throw new ArgumentException("El acuse debe referirse a una aceptación.", nameof(aceptacionContractualTenantId));
        if (usuarioDestinatarioId == Guid.Empty)
            throw new ArgumentException("El acuse debe tener una cuenta destinataria.", nameof(usuarioDestinatarioId));

        AceptacionContractualTenantId = aceptacionContractualTenantId;
        UsuarioDestinatarioId = usuarioDestinatarioId;
        EnviadoEnUtc = SolicitudAlta.ExigirUtc(enviadoEnUtc, nameof(enviadoEnUtc));
    }
}

public interface IAceptacionContractualTenantRepository
{
    void Agregar(AceptacionContractualTenant aceptacion);

    void AgregarAcuse(AcuseAceptacionContractualTenant acuse);

    /// <summary>Si el Tenant actual ya materializó las aceptaciones de esa Solicitud de alta.</summary>
    Task<bool> ExistenDeSolicitudAltaAsync(Guid solicitudAltaId, CancellationToken cancellationToken = default);

    /// <summary>Todas las aceptaciones del Tenant actual, de la más reciente a la más antigua.</summary>
    Task<IReadOnlyList<AceptacionContractualTenant>> ListarAsync(CancellationToken cancellationToken = default);

    /// <summary>El último acuse de cada aceptación del Tenant actual que tenga alguno.</summary>
    Task<IReadOnlyDictionary<Guid, DateTime>> UltimoAcusePorAceptacionAsync(CancellationToken cancellationToken = default);
}
