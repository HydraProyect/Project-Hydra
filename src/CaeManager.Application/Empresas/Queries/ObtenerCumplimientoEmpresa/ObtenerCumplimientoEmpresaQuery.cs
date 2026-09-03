using CaeManager.Application.Centros;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;

/// <summary>
/// % de cumplimiento agregado de una Empresa (Centro 360, PLAN-EJECUCION-UX.md
/// § 0.8) — suma <c>AlDia</c>/<c>Requeridos</c> de <see cref="ICalculoEstadoCentroService.CalcularCumplimientoAsync"/>
/// sobre los Centros donde la Empresa tiene actividad real (mismo universo que
/// <c>ObtenerCentrosConActividadDeEmpresaQuery</c>: Empresa → Trabajadores →
/// Asignaciones activas → Centro, Centro no cuelga de Empresa). Es la fracción
/// total de pares Trabajador×TipoDocumento, no la media de los % de cada
/// Centro — la media sesgaría a favor de Centros con pocos requisitos.
/// </summary>
public record ObtenerCumplimientoEmpresaQuery(Guid EmpresaId) : IRequest<int?>;

public class ObtenerCumplimientoEmpresaQueryHandler(
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ICalculoEstadoCentroService calculoEstadoCentro,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCumplimientoEmpresaQuery, int?>
{
    public async Task<int?> Handle(ObtenerCumplimientoEmpresaQuery request, CancellationToken cancellationToken)
    {
        // Alcance de LECTURA es correcto aquí (REC-149, se queda): devuelve
        // un único porcentaje agregado, ni identidades ni artefactos internos
        // de la contratista — el tipo de dato central que el portal CAE
        // existe para mostrar a un Cliente sobre sus contratistas.
        //
        // HALLAZGO SECUNDARIO, elevado y no corregido aquí (detectado en
        // revisión de Codex): el porcentaje se calcula sobre TODOS los
        // Centros donde la Empresa tiene actividad (línea 41, sin cruzar con
        // el Cliente de la relación), no solo los del Cliente que pregunta.
        // Un usuario de portal ve así el cumplimiento agregado de la
        // contratista con OTROS Clientes, mezclado en un solo número. No lo
        // cambio aquí porque decidir si el portal debe ver cumplimiento
        // agregado o solo el de su propia relación es una decisión de
        // producto (§14 del handoff), y el mismo cálculo está además
        // batcheado y sin gate propio en ObtenerEmpresasQuery
        // (CalcularCumplimientoPorEmpresaAsync) — ver RETURN PACKAGE de
        // HO-149-01.
        if (!await alcanceDatos.EmpresaVisibleAsync(request.EmpresaId, cancellationToken))
            return null;

        var centroIds = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.EmpresaId == request.EmpresaId
            select asignacion.CentroId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (centroIds.Count == 0) return null;

        var fracciones = await calculoEstadoCentro.CalcularCumplimientoAsync(centroIds, cancellationToken);

        var alDia = fracciones.Values.Sum(f => f.AlDia);
        var requeridos = fracciones.Values.Sum(f => f.Requeridos);

        return requeridos == 0 ? null : (int)Math.Round(alDia * 100.0 / requeridos);
    }
}
