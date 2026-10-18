using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.BusquedaGlobal.Queries.ContarVencenEstaSemana;

/// <summary>
/// Subtítulo de «Reclamar a {Empresa}» en el palette: «N documentos vencen esta semana». Cuenta los documentos
/// operativos con fecha de vencimiento entre hoy y el domingo de la semana en curso (semana de lunes a domingo, la de
/// <c>ObtenerPulsoEquipoQuery</c>) que el usuario también ve en /documentos (<c>ObtenerDocumentosQuery</c>):
/// <list type="bullet">
/// <item>los de ámbito Empresa de esa Empresa, solo si la Empresa está en su alcance de Empresa (el mismo filtro
/// <c>empresaIdsVisibles</c> del listado); una Empresa visible solo como Cliente empresarial o como Subcontrata no
/// aporta sus documentos de Empresa;</item>
/// <item>los de sus Trabajadores (empleados o de subcontrata) visibles en la cartera.</item>
/// </list>
/// Los de ámbito Cliente (la Empresa como Cliente empresarial que exige documentación) no cuentan: no son
/// documentación que se le reclame a la Empresa, y el listado los separa en su propio ámbito. Los vencidos antes de
/// hoy no entran: ya no «vencen», están vencidos. Un voluntario inactivo no cuenta: sus vencimientos no avisan
/// (<see cref="ActividadVoluntarios"/>).
///
/// <para>
/// Devuelve <c>null</c> —no 0— si la Empresa no está en ninguno de los alcances de cartera del usuario (Empresa,
/// Cliente empresarial o Subcontrata): el recuento no se calcula y no revela nada de una Empresa ajena. La Empresa
/// llega de los Recientes del palette; el recuento es para ese único sujeto, nunca por tecla.
/// </para>
/// </summary>
public record ContarVencenEstaSemanaDeEmpresaQuery(Guid EmpresaId) : IRequest<int?>;

public class ContarVencenEstaSemanaDeEmpresaQueryHandler(
    IDocumentosQueryContext documentosContext, ITrabajadoresQueryContext trabajadoresContext,
    IProyectosQueryContext proyectosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ContarVencenEstaSemanaDeEmpresaQuery, int?>
{
    public async Task<int?> Handle(ContarVencenEstaSemanaDeEmpresaQuery request, CancellationToken cancellationToken)
    {
        var empresaId = request.EmpresaId;

        var documentosDeEmpresaVisibles = await alcanceDatos.EmpresaVisibleAsync(empresaId, cancellationToken);
        if (!documentosDeEmpresaVisibles &&
            !await alcanceDatos.ClienteVisibleAsync(empresaId, cancellationToken) &&
            !await alcanceDatos.SubcontrataVisibleAsync(empresaId, cancellationToken))
            return null;

        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        var hoy = DiaDeNegocio.Hoy();
        var domingo = hoy.AddDays((7 - (int)hoy.DayOfWeek) % 7);

        var inactivos = ActividadVoluntarios.VoluntariosInactivosIds(trabajadoresContext.Trabajadores, proyectosContext, hoy);
        var trabajadoresDeLaEmpresa = trabajadoresContext.Trabajadores
            .Where(t => t.EmpresaId == empresaId || t.SubcontrataId == empresaId)
            .Where(t => trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(t.Id))
            .Where(t => !inactivos.Contains(t.Id))
            .Select(t => t.Id);

        return await documentosContext.Documentos.Operativos()
            .Where(d => d.FechaVencimiento != null && d.FechaVencimiento >= hoy && d.FechaVencimiento <= domingo)
            .Where(d => (documentosDeEmpresaVisibles && d.EmpresaId == empresaId) ||
                        (d.TrabajadorId != null && trabajadoresDeLaEmpresa.Contains(d.TrabajadorId.Value)))
            .CountAsync(cancellationToken);
    }
}
