using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// Contrato del selector de lote documental (DEC-4 + DEC-7, plan de sesiones
/// nocturnas 2026-09-02): el lote de reclamación se acota por tipo de
/// documento × ámbito de la entidad, no por clase heterogénea de ítem de
/// cola (<c>TipoItemBandeja</c> mezcla nueve tipos con destinos y semánticas
/// distintas — seleccionarlos a mano no define qué se reclama). Casos
/// literales que debe cubrir esta forma: "todos los EPIs de todos los
/// trabajadores" (Ambito=Trabajador, TipoDocumentoIds=[EPI], EntidadId=null),
/// "todos los documentos de empresa de una empresa" (Ambito=Empresa,
/// TipoDocumentoIds=[], EntidadId=esaEmpresa), "todos los documentos de un
/// trabajador en concreto" (Ambito=Trabajador, TipoDocumentoIds=[],
/// EntidadId=eseTrabajador — resuelto vía el Cliente/Centro al que está
/// asignado, mismo criterio que TrabajadorDetalle.ReclamarFaltantesAsync).
///
/// Compartido entre Web/Features/Bandeja (lote de /bandeja) y
/// Web/Features/Alertas (reclamación agregada de /alertas, DEC-4) — mismo
/// selector, dos superficies. No asumas que todos los valores de
/// AmbitoAplicacion tienen ya un camino de reclamación construido: cada
/// consumidor de este filtro decide qué ámbitos ofrece
/// (SelectorLoteDocumental.AmbitosDisponibles) según lo que de verdad sepa
/// resolver, para no ofrecer un ámbito que luego no se puede reclamar. Hoy
/// tienen camino <b>Trabajador</b> y <b>Empresa</b>; Cliente, Vehículo y
/// Proyecto siguen sin tenerlo y ObtenerLoteReclamacionPorFiltroQuery lanza
/// para ellos.
/// </summary>
/// <param name="TipoDocumentoIds">Vacío = todos los tipos de documento de ese ámbito.</param>
/// <param name="EntidadId">Null = todas las entidades visibles de ese ámbito (respetando IAlcanceDatosService); con valor = una entidad concreta.</param>
public record FiltroLoteDocumental(
    AmbitoAplicacion Ambito,
    IReadOnlyList<Guid> TipoDocumentoIds,
    Guid? EntidadId);
