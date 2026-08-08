using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Estado de un Documento frente a una plataforma destino concreta
/// (docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (b), bloque "Acreditación
/// por plataforma destino" — alcance MVP1, decisión ya cerrada con el
/// propietario en docs/business/DECISION_LOG.md 2026-08-05). Motivación: un
/// mismo documento puede estar vigente en Hydra, aceptado en Dokify y
/// pendiente en Nalanda — sin esta entidad, Hydra no puede ni preguntar qué
/// falta en qué portal.
///
/// Referencia el <see cref="Centros.CanalGestionDocumental"/> concreto (el
/// acceso de un Centro a una plataforma), no directamente el catálogo
/// <c>ProveedorPlataformaCae</c>: con N accesos por Centro (PLAN-EJECUCION-UX.md
/// § 0.6), cada acceso es la unidad real de "dónde hay que subir esto".
///
/// <b>Lote 2-C — solo dominio y persistencia.</b> Qué plataformas aplican a
/// un documento (derivado de trabajador→asignaciones activas→centros→canal,
/// o de los centros con actividad real de una Empresa) y quién crea estas
/// filas cuando se da de alta o se renueva un Documento es la lógica de
/// "Lote 2-D", todavía sin construir — igual que el catálogo
/// <c>ProveedorPlataformaCae</c> del Lote 2-A nació sin ningún consumidor en
/// la UI ("sin UI todavía... hasta que haya un consumidor real", mismo
/// criterio YAGNI aplicado aquí un nivel más abajo). Por eso este lote no
/// lleva verificación end-to-end en navegador — nada cambia en la UI.
/// </summary>
public class AcreditacionDocumentoPlataforma : EntidadBase
{
    private readonly List<RechazoAcreditacionDocumentoPlataforma> _historialRechazos = [];

    public Guid DocumentoId { get; private set; }
    public Guid CanalGestionDocumentalId { get; private set; }
    public EstadoAcreditacion Estado { get; private set; }

    /// <summary>Nunca se sobreescribe ni se borra — invariante explícita del plan. El rechazo vigente (si Estado es Rechazada) es siempre el último de la lista.</summary>
    public IReadOnlyList<RechazoAcreditacionDocumentoPlataforma> HistorialRechazos => _historialRechazos.AsReadOnly();

    private AcreditacionDocumentoPlataforma()
    {
    }

    public AcreditacionDocumentoPlataforma(Guid documentoId, Guid canalGestionDocumentalId)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La acreditación debe pertenecer a un documento.", nameof(documentoId));
        if (canalGestionDocumentalId == Guid.Empty)
            throw new ArgumentException("La acreditación debe referenciar un acceso de gestión documental.", nameof(canalGestionDocumentalId));

        DocumentoId = documentoId;
        CanalGestionDocumentalId = canalGestionDocumentalId;
        Estado = EstadoAcreditacion.PendienteDeSubir;
    }

    public void MarcarSubida() => Estado = EstadoAcreditacion.Subida;

    public void MarcarAceptada() => Estado = EstadoAcreditacion.Aceptada;

    public void MarcarNoRequerida() => Estado = EstadoAcreditacion.NoRequerida;

    /// <summary>
    /// Causa tipificada + motivo literal de la plataforma son siempre
    /// obligatorios al rechazar — sin excepción, ni siquiera desde código
    /// (RechazoAcreditacionDocumentoPlataforma ya valida el motivo). El
    /// rechazo se construye ANTES de tocar Estado a propósito: si el motivo
    /// no valida, la entidad tiene que quedar exactamente como estaba, no a
    /// medio camino (Rechazada sin ningún rechazo en el historial).
    /// </summary>
    public void Rechazar(CausaRechazoAcreditacion causa, string motivoLiteral, DateTime fechaUtc)
    {
        var rechazo = new RechazoAcreditacionDocumentoPlataforma(Id, causa, motivoLiteral, fechaUtc);
        Estado = EstadoAcreditacion.Rechazada;
        _historialRechazos.Add(rechazo);
    }

    /// <summary>
    /// Invariante del plan: renovar el Documento reinicia todas sus
    /// acreditaciones a Pendiente de subir — la versión anterior del
    /// documento ya no es la que hay que validar en ningún portal. El
    /// historial de rechazos no se toca: sigue siendo un hecho pasado real.
    /// </summary>
    public void ReiniciarPorRenovacionDocumento() => Estado = EstadoAcreditacion.PendienteDeSubir;
}
