using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// Los Tipos de Documento cuyos documentos se reclaman por la vía de un Trabajador: los de ámbito Trabajador. Un Documento
/// que cuelga de un Trabajador pero es de un Tipo de ámbito Empresa no es de esa vía (<c>CrearDocumentoCommand</c> no lo
/// crea, pero la base podría tenerlo): ni el lote lo ofrece ni el envío a la Empresa empleadora lo acepta. Es LA regla que
/// comparten el lote de Trabajador (<c>ObtenerLoteReclamacionQuery</c>), sus pendientes «Sin confirmar» sin fecha
/// (<see cref="PendientesDeReclamacionService.ListarParaClientesAsync"/>) y el envío a la Empresa empleadora
/// (<c>EnviarReclamacionEmpresaEmpleadoraCommand</c>), para que lo que se ofrece sea lo que se acepta.
///
/// <para>
/// Devuelve una subconsulta componible sin materializar, como <see cref="AsignacionesReclamables"/>: cada lector la une con
/// sus propios filtros, y los filtros globales de Tenant y la RLS siguen aplicándose al IQueryable de origen.
/// </para>
/// </summary>
public static class TiposDocumentoReclamablesDeTrabajador
{
    public static IQueryable<TipoDocumento> De(IQueryable<TipoDocumento> tiposDocumento) =>
        tiposDocumento.Where(tipo => tipo.AmbitoAplicacion == AmbitoAplicacion.Trabajador);
}
