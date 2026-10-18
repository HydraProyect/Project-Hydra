using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

public enum IntencionCae
{
    CoordinacionAcceso = 1,
    DocumentacionMensual = 2,
    AltaContratoAccesoCentro = 3,
    CopiaProyectoObra = 4,
    UrgenciaAcceso = 5,
    ConsultaRequisitos = 6,
    Otras = 7
}

public enum ConfianzaIntencion { Baja = 1, Media = 2, Alta = 3 }
public enum TipoEntidadIntencion { Trabajador = 1, Empresa = 2, Centro = 3, TipoDocumento = 4 }

/// <summary>Propuesta sobre el primer mensaje entrante. No registra la identidad del Gestor CAE.</summary>
public class IntencionConversacion : EntidadConTenant
{
    public Guid ConversacionId { get; private set; }
    public Guid MensajeId { get; private set; }
    public IntencionCae IntencionPropuesta { get; private set; }
    public IntencionCae IntencionActual { get; private set; }
    public ConfianzaIntencion Confianza { get; private set; }
    public bool ConfirmadaPorPersona { get; private set; }
    public bool CambiadaPorPersona => ConfirmadaPorPersona && IntencionActual != IntencionPropuesta;
    public bool CuentaParaMetricas => Confianza == ConfianzaIntencion.Alta || ConfirmadaPorPersona;
    public TipoEntidadIntencion? TipoEntidadPropuesta { get; private set; }
    public Guid? EntidadPropuestaId { get; private set; }

    private IntencionConversacion() { }

    public IntencionConversacion(Guid conversacionId, Guid mensajeId, IntencionCae intencion,
        ConfianzaIntencion confianza, TipoEntidadIntencion? tipoEntidad = null, Guid? entidadId = null)
    {
        if (conversacionId == Guid.Empty || mensajeId == Guid.Empty)
            throw new ArgumentException("La intención requiere una conversación y su primer mensaje entrante.");
        if (!Enum.IsDefined(intencion) || !Enum.IsDefined(confianza))
            throw new ArgumentOutOfRangeException(nameof(intencion));
        if ((tipoEntidad is null) != (entidadId is null) || entidadId == Guid.Empty ||
            (tipoEntidad is not null && !Enum.IsDefined(tipoEntidad.Value)))
            throw new ArgumentException("La propuesta de entidad requiere un tipo y un identificador válidos.");
        ConversacionId = conversacionId;
        MensajeId = mensajeId;
        IntencionPropuesta = IntencionActual = intencion;
        Confianza = confianza;
        TipoEntidadPropuesta = tipoEntidad;
        EntidadPropuestaId = entidadId;
    }

    public void Confirmar(IntencionCae intencion)
    {
        if (!Enum.IsDefined(intencion)) throw new ArgumentOutOfRangeException(nameof(intencion));
        IntencionActual = intencion;
        ConfirmadaPorPersona = true;
    }
}
