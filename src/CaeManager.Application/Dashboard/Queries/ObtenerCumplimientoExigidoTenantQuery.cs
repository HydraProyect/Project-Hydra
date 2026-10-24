using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Empresas;
using CaeManager.Application.EstadoMaterializado;
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
    ICalculoEstadoCentroService calculoEstadoCentro,
    ILectorCumplimientoDeDireccion? lectorEstado = null)
    : IRequestHandler<ObtenerCumplimientoExigidoTenantQuery, FraccionCumplimiento>
{
    private readonly ILectorCumplimientoDeDireccion lector = lectorEstado ?? SinLectorDeCumplimientoDeDireccion.Instancia;

    public async Task<FraccionCumplimiento> Handle(ObtenerCumplimientoExigidoTenantQuery request, CancellationToken cancellationToken)
    {
        var empresasPropias = await empresasContext.Empresas.Where(e => e.EsPropia).Select(e => e.Id).ToListAsync(cancellationToken);
        if (empresasPropias.Count == 0) return FraccionCumplimiento.SinRequisitos;

        var centrosPorEmpresa = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.EmpresaId != null && empresasPropias.Contains(trabajador.EmpresaId.Value)
            select new { EmpresaId = trabajador.EmpresaId!.Value, asignacion.CentroId })
            .Distinct()
            .ToListAsync(cancellationToken);
        if (centrosPorEmpresa.Count == 0) return FraccionCumplimiento.SinRequisitos;

        var empresaYCentro = centrosPorEmpresa.Select(c => (c.EmpresaId, c.CentroId)).ToHashSet();

        // De las tablas del estado materializado si el Tenant está completo y sus pares son de hoy (PR 10 del plan); si no,
        // el cálculo de siempre, que sigue siendo la referencia. La facturación del Operador CAE no pasa por aquí.
        if (await lector.CumplimientoExigidoAsync(empresaYCentro, cancellationToken) is { } leida)
            return leida;

        var pares = await calculoEstadoCentro.ObtenerParesExigidosAsync(
            centrosPorEmpresa.Select(c => c.CentroId).Distinct().ToList(), cancellationToken);
        return DeEmpresasPropias(pares, empresaYCentro);
    }

    /// <summary>
    /// La suma de las fracciones de cada Empresa propia, cada una sobre sus propios Centros (los de sus Asignaciones
    /// activas, como cada fila de /empresas): los pares de otras Empresas, sin Empresa o de un Centro que no es de la
    /// Empresa del par (un evento en un Centro de otra Empresa propia) no cuentan. Una llamada al servicio para todas.
    /// </summary>
    public static FraccionCumplimiento DeEmpresasPropias(
        IEnumerable<ParDocumentalExigido> pares, IReadOnlySet<(Guid EmpresaId, Guid CentroId)> centrosPorEmpresa)
    {
        var empresasPropias = centrosPorEmpresa.Select(c => c.EmpresaId).Distinct().ToList();
        var propios = pares.Where(p => p.EmpresaId is { } empresa && centrosPorEmpresa.Contains((empresa, p.CentroId)));
        var porEmpresa = CumplimientoDocumental.PorContexto(ContextoCumplimiento.Empresa, propios);
        return FraccionCumplimiento.Sumar(empresasPropias.Select(id => porEmpresa.GetValueOrDefault(id, FraccionCumplimiento.SinRequisitos)));
    }
}
