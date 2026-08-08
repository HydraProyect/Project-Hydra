using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Acreditacion;

/// <summary>
/// Deriva a qué accesos de plataforma (<see cref="CanalGestionDocumental"/>
/// de tipo <see cref="TipoCanalGestion.Plataforma"/>) aplica un Documento —
/// docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (b): "qué plataformas
/// aplican a un documento se deriva de la operación real". Solo Trabajador y
/// Empresa tienen accesos de plataforma que acreditar (el plan no menciona
/// Cliente/Vehículo/Proyecto — quedan fuera a propósito, sin acreditación).
/// </summary>
public interface IDerivarCanalesAplicablesDocumentoService
{
    Task<IReadOnlyList<Guid>> ObtenerCanalGestionDocumentalIdsAplicablesAsync(Documento documento, CancellationToken cancellationToken = default);
}

public class DerivarCanalesAplicablesDocumentoService(
    IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, ITrabajadoresQueryContext trabajadoresContext)
    : IDerivarCanalesAplicablesDocumentoService
{
    public async Task<IReadOnlyList<Guid>> ObtenerCanalGestionDocumentalIdsAplicablesAsync(
        Documento documento, CancellationToken cancellationToken = default)
    {
        var centroIds = documento.Ambito switch
        {
            AmbitoAplicacion.Trabajador => await ObtenerCentrosDeTrabajadorAsync(documento.TrabajadorId!.Value, cancellationToken),
            AmbitoAplicacion.Empresa => await ObtenerCentrosConActividadDeEmpresaAsync(documento.EmpresaId!.Value, cancellationToken),
            _ => []
        };

        if (centroIds.Count == 0) return [];

        return await centrosContext.CanalesGestionDocumental
            .Where(c => centroIds.Contains(c.CentroId) && c.Tipo == TipoCanalGestion.Plataforma)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<Guid>> ObtenerCentrosDeTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken) =>
        await asignacionesContext.Asignaciones
            .Where(a => a.TrabajadorId == trabajadorId && a.FechaBaja == null)
            .Select(a => a.CentroId)
            .Distinct()
            .ToListAsync(cancellationToken);

    /// <summary>Mismo criterio que ObtenerCentrosConActividadDeEmpresaQueryHandler: Empresa → Trabajadores → Asignaciones activas → Centro (Centro cuelga de Cliente, no de Empresa — no hay relación directa).</summary>
    private async Task<List<Guid>> ObtenerCentrosConActividadDeEmpresaAsync(Guid empresaId, CancellationToken cancellationToken) =>
        await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.EmpresaId == empresaId
            select asignacion.CentroId)
            .Distinct()
            .ToListAsync(cancellationToken);
}
