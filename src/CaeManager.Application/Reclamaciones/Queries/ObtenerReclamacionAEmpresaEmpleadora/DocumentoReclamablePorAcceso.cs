using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionAEmpresaEmpleadora;

/// <summary>
/// Qué documento con fecha entra en la reclamación por acceso (<see cref="ObtenerReclamacionPorAccesoQuery"/>): lo vencido y
/// lo que está por vencer, nunca lo vigente. Los lotes de siempre parten de la ventana de 3 meses
/// (<see cref="VentanaReclamacion"/>), que la preventiva conserva para pedir con antelación; la reclamación por acceso
/// (decisión de Chris, 2026-10-19, y su maqueta «Reclamacion por acceso TALVEG.dc.html», donde lo «Vigente» no es
/// reclamable) solo pide lo vencido o pendiente, así que lo que la ventana trae en «Vigente» se queda fuera.
///
/// <para>
/// Es la única regla de ese universo: la query la aplica a los documentos de los Trabajadores y a los de la Empresa antes
/// de devolver los lotes, y de esos lotes salen la lista de la página, su recuento y lo que el cajón de «Reclamar en lote»
/// envía. «Por vencer» (<see cref="EstadoDocumento.Proximo"/> y <see cref="EstadoDocumento.Urgente"/>, con los umbrales de
/// <c>ParametrosSistema</c>) sigue dentro, como estaba.
/// </para>
/// </summary>
public static class DocumentoReclamablePorAcceso
{
    public static bool SeReclama(DocumentoReclamableDto documento) => SeReclama(documento.Estado);

    public static bool SeReclama(EstadoDocumento estado) => estado != EstadoDocumento.Vigente;
}
