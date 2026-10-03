using CaeManager.Application.Centros;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;

/// <summary>
/// % de cumplimiento de una Empresa (Centro 360, Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md
/// § 0.8): contexto <see cref="ContextoCumplimiento.Empresa"/> de <see cref="CumplimientoDocumental"/>. Mide los pares
/// Trabajador×TipoDocumento exigidos a los Trabajadores de la Empresa en los Centros donde tienen una Asignación activa
/// (<see cref="ICalculoEstadoCentroService.ObtenerParesExigidosAsync"/>); los de otras Empresas que comparten Centro con
/// ella no cuentan. Es la fracción total de pares, no la media de los % de cada Centro — la media sesgaría a favor de
/// Centros con pocos requisitos.
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

        var pares = await calculoEstadoCentro.ObtenerParesExigidosAsync(centroIds, cancellationToken);

        return CumplimientoDocumental.De(ContextoCumplimiento.Empresa, request.EmpresaId, pares).Porcentaje;
    }
}
