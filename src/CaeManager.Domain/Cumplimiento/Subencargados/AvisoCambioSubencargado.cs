using CaeManager.Domain.Common;

namespace CaeManager.Domain.Cumplimiento.Subencargados;

/// <summary>
/// Un paso del envío del aviso de un subencargado nuevo a un Administrador de un Tenant propietario (#2010, D-7).
/// Es del Tenant propietario avisado (<see cref="EntidadConTenant"/>, con RLS) y solo se añade, como una bandeja de
/// salida en la que nada se reescribe: cada envío tiene un <see cref="EnvioId"/>, con una fila
/// <see cref="FaseAvisoCambioSubencargado.Preparado"/> por destinatario guardada antes de que salga el correo, y
/// después una <see cref="FaseAvisoCambioSubencargado.Enviado"/> o <see cref="FaseAvisoCambioSubencargado.NoConfirmado"/>.
///
/// <para>
/// Solo <see cref="FaseAvisoCambioSubencargado.Enviado"/> cuenta como aviso probado para la activación: un envío que
/// se queda en Preparado o acaba en NoConfirmado es de resultado incierto y no se afirma ni que llegara ni que no.
/// Guarda a qué cuenta se envió, no su dirección (mismo criterio que <c>AcuseAceptacionContractualTenant</c>).
/// </para>
/// </summary>
public class AvisoCambioSubencargado : EntidadConTenant
{
    public const int LongitudMaximaFase = 16;

    public Guid SubencargadoId { get; private set; }
    public Guid EnvioId { get; private set; }
    public Guid UsuarioDestinatarioId { get; private set; }
    public FaseAvisoCambioSubencargado Fase { get; private set; }
    public DateTime RegistradoEnUtc { get; private set; }

    /// <summary>El Actor de Plataforma TALVEG que lanzó el envío.</summary>
    public Guid AvisadoPorUsuarioId { get; private set; }

    private AvisoCambioSubencargado()
    {
        // Requerido por EF Core.
    }

    public AvisoCambioSubencargado(
        Guid subencargadoId, Guid envioId, Guid usuarioDestinatarioId, FaseAvisoCambioSubencargado fase,
        DateTime registradoEnUtc, Guid avisadoPorUsuarioId)
    {
        if (subencargadoId == Guid.Empty)
            throw new ArgumentException("El aviso tiene que referirse a un subencargado.", nameof(subencargadoId));
        if (envioId == Guid.Empty)
            throw new ArgumentException("El aviso tiene que pertenecer a un envío.", nameof(envioId));
        if (usuarioDestinatarioId == Guid.Empty)
            throw new ArgumentException("El aviso tiene que tener una cuenta destinataria.", nameof(usuarioDestinatarioId));
        if (avisadoPorUsuarioId == Guid.Empty)
            throw new ArgumentException("El aviso tiene que quedar atribuido a quien lo lanzó.", nameof(avisadoPorUsuarioId));
        if (!Enum.IsDefined(fase))
            throw new ArgumentException("La fase del aviso no es válida.", nameof(fase));

        SubencargadoId = subencargadoId;
        EnvioId = envioId;
        UsuarioDestinatarioId = usuarioDestinatarioId;
        Fase = fase;
        RegistradoEnUtc = Subencargado.Utc(registradoEnUtc, nameof(registradoEnUtc));
        AvisadoPorUsuarioId = avisadoPorUsuarioId;
    }

    /// <summary>Solo un envío confirmado por el servicio de correo prueba el aviso.</summary>
    public bool EsAvisoProbado => Fase == FaseAvisoCambioSubencargado.Enviado;
}

public enum FaseAvisoCambioSubencargado
{
    /// <summary>Guardada antes de que salga el correo: sin esta fila confirmada, el correo no sale.</summary>
    Preparado,

    /// <summary>El servicio de correo aceptó el envío. Es la única fase que cuenta como aviso probado.</summary>
    Enviado,

    /// <summary>El servicio de correo no confirmó el envío. No afirma que no saliera, y no cuenta como aviso.</summary>
    NoConfirmado,
}
