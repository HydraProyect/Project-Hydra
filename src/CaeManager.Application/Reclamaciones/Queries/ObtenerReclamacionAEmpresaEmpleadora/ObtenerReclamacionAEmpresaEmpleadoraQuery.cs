using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Domain.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionAEmpresaEmpleadora;

/// <summary>
/// Reclamación por acceso (decisión de Chris, 2026-10-19): uno o varios Trabajadores de una Empresa tienen que entrar a un
/// Centro; se reclama a su Empresa empleadora, en un solo borrador, todo lo que ese Centro exige y está vencido o pendiente,
/// de la Empresa y de esos Trabajadores. Qué exige el Centro lo dice <see cref="IEvaluacionDeAccesoPorCentroService"/>
/// (la única fuente del bloqueo por Centro, sobre las Asignaciones activas); qué es reclamable y a quién va, los lotes de
/// siempre (<see cref="ObtenerLoteReclamacionQuery"/>, <see cref="ObtenerLoteReclamacionEmpresaQuery"/> y
/// <see cref="ReclamacionEmpresaEmpleadoraService"/>). Esta query solo intersecta: no recuenta ni amplía nada.
/// </summary>
/// <remarks>
/// Lo de la Empresa viaja en el lote de los Trabajadores cuando hay uno solo, o en el que va a la agenda de la Empresa si
/// existe; si no, sale en su propia tarjeta (ámbito Empresa). Un Trabajador sin Asignación activa en el Centro no tiene
/// requisitos que evaluar allí y no aporta nada.
/// </remarks>
public record ObtenerReclamacionPorAccesoQuery(Guid EmpresaId, Guid CentroId, IReadOnlyList<Guid> TrabajadorIds)
    : IRequest<IReadOnlyList<LoteReclamacionAgrupadoDto>>;

/// <summary>
/// Reclamación preventiva consolidada (decisión de Chris, 2026-10-19): todo lo vencido, por vencer o pendiente de una
/// Empresa y de todos sus Trabajadores, con los lotes ya dirigidos a quien toca (una tarjeta por Delegación y otra para la
/// agenda de la Empresa). Filtrable por Delegación y por Oficio del Trabajador; con cualquiera de los dos filtros los
/// documentos de la propia Empresa no entran (no son de ninguna Delegación ni Oficio).
/// </summary>
public record ObtenerReclamacionPreventivaEmpresaQuery(Guid EmpresaId, Guid? DelegacionEmpresaId = null, Guid? OficioEmpresaId = null)
    : IRequest<ReclamacionPreventivaEmpresaDto>;

public record OpcionFiltroDto(Guid Id, string Nombre);

/// <summary>Un Centro con gestión CAE donde la Empresa tiene Trabajadores con Asignación activa: lo que ofrece la reclamación por acceso.</summary>
public record CentroConTrabajadoresDto(Guid CentroId, string CentroNombre, IReadOnlyList<OpcionFiltroDto> Trabajadores);

/// <param name="Lotes">Los lotes listos para <c>DrawerReclamacionLote</c>, ya filtrados.</param>
/// <param name="DelegacionesEmpresa">Las sedes (Delegaciones de Empresa) de la Empresa, para el filtro.</param>
/// <param name="Oficios">Los Oficios de la Empresa, para el filtro.</param>
/// <param name="Centros">Los Centros donde trabaja hoy la Empresa, para la reclamación por acceso.</param>
public record ReclamacionPreventivaEmpresaDto(
    IReadOnlyList<LoteReclamacionAgrupadoDto> Lotes,
    IReadOnlyList<OpcionFiltroDto> DelegacionesEmpresa,
    IReadOnlyList<OpcionFiltroDto> Oficios,
    IReadOnlyList<CentroConTrabajadoresDto> Centros);

public class ObtenerReclamacionPorAccesoQueryHandler(
    IMediator mediator,
    IAlcanceDatosService alcanceDatos,
    IEvaluacionDeAccesoPorCentroService evaluacionDeAcceso,
    ReclamacionEmpresaEmpleadoraService empresaEmpleadora)
    : IRequestHandler<ObtenerReclamacionPorAccesoQuery, IReadOnlyList<LoteReclamacionAgrupadoDto>>
{
    public async Task<IReadOnlyList<LoteReclamacionAgrupadoDto>> Handle(ObtenerReclamacionPorAccesoQuery request, CancellationToken cancellationToken)
    {
        // Fallo cerrado: fuera de la Asignación de Cartera no se ofrece nada (los envíos lo vuelven a comprobar).
        if (request.TrabajadorIds.Count == 0 || !await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return [];

        var trabajadores = request.TrabajadorIds.ToHashSet();
        var evaluacion = await evaluacionDeAcceso.EvaluarAsync([request.CentroId], cancellationToken);
        var requisitos = evaluacion.Requisitos.Where(r => r.CentroId == request.CentroId && trabajadores.Contains(r.TrabajadorId)).ToList();
        var exigidosTrabajador = requisitos.Where(r => r.Ambito == AmbitoAplicacion.Trabajador)
            .Select(r => (r.TrabajadorId, r.TipoDocumentoId)).ToHashSet();
        var exigidosEmpresa = requisitos.Where(r => r.Ambito == AmbitoAplicacion.Empresa && r.EmpresaId == request.EmpresaId)
            .Select(r => r.TipoDocumentoId).ToHashSet();

        var lotesCliente = await mediator.Send(new ObtenerLoteReclamacionQuery(CentroId: request.CentroId, IncluirPendientesSinFecha: true), cancellationToken);
        var reagrupados = await empresaEmpleadora.ReagruparAsync(lotesCliente, cancellationToken);
        var lotes = reagrupados.LotesEmpresaEmpleadora
            .Where(l => l.TitularId == request.EmpresaId)
            .Select(l => l with
            {
                Documentos = [.. l.Documentos.Where(d => d.TrabajadorId is { } t && exigidosTrabajador.Contains((t, d.TipoDocumentoId)))],
                PendientesSinFecha = [.. (l.PendientesSinFecha ?? []).Where(p => p.TrabajadorId is { } t && exigidosTrabajador.Contains((t, p.TipoDocumentoId)))],
            })
            .Where(l => l.Documentos.Count > 0 || l.PendientesSinFecha!.Count > 0)
            .ToList();

        if (exigidosEmpresa.Count == 0)
            return lotes;

        var deEmpresa = (await mediator.Send(
                new ObtenerLoteReclamacionEmpresaQuery(request.EmpresaId, [.. exigidosEmpresa], IncluirPendientesSinFecha: true), cancellationToken))
            .FirstOrDefault(l => l.EmpresaId == request.EmpresaId);
        if (deEmpresa is null || (deEmpresa.Documentos.Count == 0 && (deEmpresa.PendientesSinFecha ?? []).Count == 0))
            return lotes;

        // Un solo borrador: lo de la Empresa entra en el lote de los Trabajadores si hay uno solo, o en el de la agenda.
        var receptor = lotes.Count == 1 ? lotes[0] : lotes.FirstOrDefault(l => l.EmpresaEmpleadora?.DelegacionEmpresaId is null);
        if (receptor is null)
        {
            return [.. lotes, new LoteReclamacionAgrupadoDto(
                deEmpresa.EmpresaId, deEmpresa.RazonSocialEmpresa, AmbitoAplicacion.Empresa, deEmpresa.UltimaReclamacionFechaUtc,
                deEmpresa.Documentos, deEmpresa.UltimaReclamacionConversacionId, deEmpresa.Destinatarios, deEmpresa.PendientesSinFecha)];
        }

        var unido = receptor with
        {
            Documentos = [.. deEmpresa.Documentos, .. receptor.Documentos],
            PendientesSinFecha = [.. deEmpresa.PendientesSinFecha ?? [], .. receptor.PendientesSinFecha ?? []],
        };
        return [.. lotes.Select(l => ReferenceEquals(l, receptor) ? unido : l)];
    }
}

public class ObtenerReclamacionPreventivaEmpresaQueryHandler(
    IMediator mediator,
    IAlcanceDatosService alcanceDatos,
    IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    ReclamacionEmpresaEmpleadoraService empresaEmpleadora)
    : IRequestHandler<ObtenerReclamacionPreventivaEmpresaQuery, ReclamacionPreventivaEmpresaDto>
{
    public async Task<ReclamacionPreventivaEmpresaDto> Handle(ObtenerReclamacionPreventivaEmpresaQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return new([], [], [], []);

        var delegaciones = await empresasContext.DelegacionesEmpresa.Where(d => d.EmpresaId == request.EmpresaId)
            .OrderBy(d => d.Nombre).Select(d => new OpcionFiltroDto(d.Id, d.Nombre)).ToListAsync(cancellationToken);
        var oficios = await empresasContext.OficiosEmpresa.Where(o => o.EmpresaId == request.EmpresaId)
            .OrderBy(o => o.Nombre).Select(o => new OpcionFiltroDto(o.Id, o.Nombre)).ToListAsync(cancellationToken);
        var trabajadores = await trabajadoresContext.Trabajadores.Where(t => t.EmpresaId == request.EmpresaId)
            .Select(t => new { t.Id, t.DelegacionEmpresaId, t.OficioEmpresaId }).ToListAsync(cancellationToken);
        var admitidos = trabajadores
            .Where(t => request.DelegacionEmpresaId is null || t.DelegacionEmpresaId == request.DelegacionEmpresaId)
            .Where(t => request.OficioEmpresaId is null || t.OficioEmpresaId == request.OficioEmpresaId)
            .Select(t => t.Id).ToHashSet();

        var lotesCliente = await mediator.Send(new ObtenerLoteReclamacionQuery(IncluirPendientesSinFecha: true), cancellationToken);
        var reagrupados = await empresaEmpleadora.ReagruparAsync(lotesCliente, cancellationToken);
        var lotes = reagrupados.LotesEmpresaEmpleadora
            .Where(l => l.TitularId == request.EmpresaId)
            .Select(l => l with
            {
                Documentos = [.. l.Documentos.Where(d => d.TrabajadorId is { } t && admitidos.Contains(t))],
                PendientesSinFecha = [.. (l.PendientesSinFecha ?? []).Where(p => p.TrabajadorId is { } t && admitidos.Contains(t))],
            })
            .Where(l => l.Documentos.Count > 0 || l.PendientesSinFecha!.Count > 0)
            .ToList();

        if (request.DelegacionEmpresaId is null && request.OficioEmpresaId is null)
        {
            var deEmpresa = (await mediator.Send(new ObtenerLoteReclamacionEmpresaQuery(request.EmpresaId, IncluirPendientesSinFecha: true), cancellationToken))
                .FirstOrDefault(l => l.EmpresaId == request.EmpresaId);
            if (deEmpresa is not null && (deEmpresa.Documentos.Count > 0 || (deEmpresa.PendientesSinFecha ?? []).Count > 0))
            {
                lotes.Insert(0, new LoteReclamacionAgrupadoDto(
                    deEmpresa.EmpresaId, deEmpresa.RazonSocialEmpresa, AmbitoAplicacion.Empresa, deEmpresa.UltimaReclamacionFechaUtc,
                    deEmpresa.Documentos, deEmpresa.UltimaReclamacionConversacionId, deEmpresa.Destinatarios, deEmpresa.PendientesSinFecha));
            }
        }

        return new(lotes, delegaciones, oficios, await CentrosAsync(request.EmpresaId, cancellationToken));
    }

    /// <summary>Mismo alcance que el lote: Asignación activa, Centro con gestión CAE y Trabajadores, Centros y Clientes visibles.</summary>
    private async Task<IReadOnlyList<CentroConTrabajadoresDto>> CentrosAsync(Guid empresaId, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var clientesEnAlcance = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        var filas = await (
                from trabajador in trabajadoresContext.Trabajadores
                where trabajador.EmpresaId == empresaId
                where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(trabajador.Id)
                join asignacion in asignacionesContext.Asignaciones on trabajador.Id equals asignacion.TrabajadorId
                where asignacion.FechaBaja == null
                where centroIdsVisibles == null || centroIdsVisibles.Contains(asignacion.CentroId)
                join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
                where centro.GestionCae != ModalidadGestionCae.SinGestionCae
                where clientesEnAlcance == null || clientesEnAlcance.Contains(centro.ClienteId)
                select new { CentroId = centro.Id, CentroNombre = centro.Nombre, TrabajadorId = trabajador.Id, TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos })
            .Distinct()
            .ToListAsync(cancellationToken);
        return [.. filas.GroupBy(f => (f.CentroId, f.CentroNombre))
            .OrderBy(g => g.Key.CentroNombre)
            .Select(g => new CentroConTrabajadoresDto(g.Key.CentroId, g.Key.CentroNombre,
                [.. g.OrderBy(f => f.TrabajadorNombre).Select(f => new OpcionFiltroDto(f.TrabajadorId, f.TrabajadorNombre))]))];
    }
}
