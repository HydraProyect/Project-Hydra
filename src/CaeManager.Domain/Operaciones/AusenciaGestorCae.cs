using CaeManager.Domain.Common;

namespace CaeManager.Domain.Operaciones;

public enum TipoAusenciaGestorCae { Vacaciones, BajaTemporal, BajaDefinitiva }

/// <summary>Ausencia del Gestor CAE, propiedad de su Operador CAE. No cambia la propiedad de ningún dato CAE.</summary>
public class AusenciaGestorCae : EntidadConTenant, IVersionable
{
    public Guid GestorUsuarioId { get; private set; }
    public TipoAusenciaGestorCae Tipo { get; private set; }
    public DateOnly Desde { get; private set; }
    public DateOnly? Hasta { get; private set; }
    public Guid RegistradaPorUsuarioId { get; private set; }
    public DateTime RegistradaEnUtc { get; private set; }
    public DateTime? CerradaEnUtc { get; private set; }
    public Guid Version { get; private set; } = Guid.NewGuid();
    public List<CoberturaEmpresaAusencia> Coberturas { get; private set; } = [];

    private AusenciaGestorCae() { }

    public AusenciaGestorCae(Guid gestorUsuarioId, TipoAusenciaGestorCae tipo, DateOnly desde,
        DateOnly? hasta, Guid actorUsuarioId, DateTime ahora)
    {
        if (gestorUsuarioId == Guid.Empty || actorUsuarioId == Guid.Empty || !Enum.IsDefined(tipo))
            throw new ArgumentException("La ausencia necesita un Gestor CAE, un tipo y un actor válidos.");
        if (tipo != TipoAusenciaGestorCae.BajaDefinitiva && (hasta is null || hasta < desde))
            throw new ArgumentException("La fecha de vuelta no puede ser anterior al inicio.");
        GestorUsuarioId = gestorUsuarioId;
        Tipo = tipo;
        Desde = desde;
        Hasta = tipo == TipoAusenciaGestorCae.BajaDefinitiva ? null : hasta;
        RegistradaPorUsuarioId = actorUsuarioId;
        RegistradaEnUtc = ahora;
    }

    public void Cubrir(Guid operacionId, Guid propietarioTenantId, Guid destinatarioUsuarioId)
    {
        if (CerradaEnUtc is not null || operacionId == Guid.Empty || propietarioTenantId == Guid.Empty
            || destinatarioUsuarioId == Guid.Empty || destinatarioUsuarioId == GestorUsuarioId
            || Coberturas.Any(c => c.AsignacionOperacionId == operacionId))
            throw new InvalidOperationException("La Empresa ya está cubierta o la ausencia no admite esa cobertura.");
        Coberturas = [.. Coberturas, new(operacionId, propietarioTenantId, destinatarioUsuarioId)];
    }

    public void Cerrar(DateTime ahora)
    {
        if (Tipo == TipoAusenciaGestorCae.BajaDefinitiva)
            throw new InvalidOperationException("Una baja definitiva no tiene vuelta.");
        CerradaEnUtc ??= ahora;
    }
}

public record CoberturaEmpresaAusencia(Guid AsignacionOperacionId, Guid PropietarioTenantId, Guid DestinatarioUsuarioId);
