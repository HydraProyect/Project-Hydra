using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// El cumplimiento del Tenant del ámbito con el universo de <see cref="ContextoCumplimiento.Empresa"/>, y medido igual
/// que el listado de Empresas (<c>ObtenerEmpresasQuery</c>): sus Empresas propias, los Centros donde sus Trabajadores
/// tienen Asignación activa y los pares Trabajador×TipoDocumento que esos Centros les exigen hoy, con los que faltan en
/// contra (<see cref="ICalculoEstadoCentroService.ObtenerParesExigidosAsync"/>). Los pares de Trabajadores de otras
/// Empresas que comparten Centro no cuentan. El resultado es la suma de las filas de /empresas, nunca un promedio.
/// Es la columna por Tenant beneficiario del Cuadro de dirección (decisión de Coordinación del 2026-10-21: cada
/// porcentaje mide su contexto, y esa columna es contexto Empresa; el porcentaje de cartera de Inicio mide los
/// documentos de Trabajador guardados y es otro universo). Corre dentro del <c>AmbitoTenantExplicito</c> de quien llama,
/// solo para Tenants que alcanza enteros: el filtro global y la RLS lo acotan a ese Tenant.
/// </summary>
public record ObtenerCumplimientoExigidoTenantQuery : IRequest<FraccionCumplimiento>;

public class ObtenerCumplimientoExigidoTenantQueryHandler(
    IEmpresasQueryContext empresasContext,
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ICalculoEstadoCentroService calculoEstadoCentro)
    : IRequestHandler<ObtenerCumplimientoExigidoTenantQuery, FraccionCumplimiento>
{
    public async Task<FraccionCumplimiento> Handle(ObtenerCumplimientoExigidoTenantQuery request, CancellationToken cancellationToken)
    {
        var empresasPropias = await empresasContext.Empresas.Where(e => e.EsPropia).Select(e => e.Id).ToListAsync(cancellationToken);
        if (empresasPropias.Count == 0) return FraccionCumplimiento.SinRequisitos;

        var centroIds = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.EmpresaId != null && empresasPropias.Contains(trabajador.EmpresaId.Value)
            select asignacion.CentroId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (centroIds.Count == 0) return FraccionCumplimiento.SinRequisitos;

        var pares = await calculoEstadoCentro.ObtenerParesExigidosAsync(centroIds, cancellationToken);
        return DeEmpresasPropias(pares, empresasPropias);
    }

    /// <summary>La suma de las fracciones de cada Empresa propia; los pares de otras Empresas o sin Empresa no cuentan.</summary>
    public static FraccionCumplimiento DeEmpresasPropias(IEnumerable<ParDocumentalExigido> pares, IReadOnlyCollection<Guid> empresasPropias)
    {
        var porEmpresa = CumplimientoDocumental.PorContexto(ContextoCumplimiento.Empresa, pares);
        return FraccionCumplimiento.Sumar(empresasPropias.Distinct().Select(id => porEmpresa.GetValueOrDefault(id, FraccionCumplimiento.SinRequisitos)));
    }
}
