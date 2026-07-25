using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerCentrosConActividadDeEmpresa;

/// <summary>
/// Respalda la pestaña "Centros" del Context Workspace de Empresa. Centro
/// cuelga de Cliente, no de Empresa (ver DATABASE.md) — no existe una lista
/// de "todos los centros del cliente" aquí a propósito: una Empresa puede
/// trabajar para un Cliente con cientos de centros y operar solo en unos
/// pocos, así que se deriva la actividad real: Empresa → Trabajadores →
/// Asignaciones activas → Centro. Es una aproximación intencional a la
/// realidad operativa, no una relación directa del modelo.
/// </summary>
public record ObtenerCentrosConActividadDeEmpresaQuery(Guid EmpresaId) : IRequest<IReadOnlyList<CentroConActividadDto>>;

public class ObtenerCentrosConActividadDeEmpresaQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCentrosConActividadDeEmpresaQuery, IReadOnlyList<CentroConActividadDto>>
{
    public async Task<IReadOnlyList<CentroConActividadDto>> Handle(
        ObtenerCentrosConActividadDeEmpresaQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaVisibleAsync(request.EmpresaId, cancellationToken))
            return [];

        var filas = await (
            from asignacion in dbContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in dbContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.EmpresaId == request.EmpresaId
            join centro in dbContext.Centros on asignacion.CentroId equals centro.Id
            join cliente in dbContext.Clientes on centro.ClienteId equals cliente.Id
            select new FilaActividadCentro(centro.Id, centro.Nombre, cliente.RazonSocial, trabajador.Id))
            .ToListAsync(cancellationToken);

        return CentroConActividadAgrupador.Agrupar(filas);
    }
}
