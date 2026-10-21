using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Domain.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionAEmpresaEmpleadora;

/// <summary>
/// Reclamación por acceso (decisión de Chris, 2026-10-19): uno o varios Trabajadores de una Empresa tienen que entrar a un
/// Centro; se reclama a su Empresa empleadora, en un solo borrador, todo lo que ese Centro exige y está vencido o pendiente,
/// de la Empresa y de esos Trabajadores. Qué exige el Centro lo dicen sus pares exigidos
/// (<see cref="ICalculoEstadoCentroService.ObtenerParesExigidosAsync"/>, los mismos que el Centro 360), no solo los requisitos
/// que bloquean el acceso: un Centro sin ningún tipo «bloquea el acceso» también exige; qué es reclamable y a quién va, los lotes de
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

/// <summary>
/// «Pendiente de subir a plataformas» (regla de Chris, 2026-10-19: el estado del documento en TALVEG y el de su acreditación
/// en cada plataforma o Centro son cosas distintas). Lo vigente en TALVEG que un Centro aún no da por acreditado es trabajo
/// del Gestor CAE, no se reclama a la Empresa: por eso las dos reclamaciones de arriba parten solo de la vigencia en TALVEG
/// (los lotes de siempre) y esto va aparte. Sale de <see cref="ObtenerDocumentacionBloqueantePendienteQuery"/> (la única
/// fuente del bloqueo por Centro), con las situaciones de plataforma, acotado a la Empresa y a sus Trabajadores.
/// </summary>
/// <param name="CentroId">Con valor, solo ese Centro (reclamación por acceso); sin él, todos (preventiva).</param>
/// <param name="TrabajadorIds">Con valor, solo lo de esos Trabajadores y lo de la Empresa.</param>
public record ObtenerPendientesDeSubirAPlataformaEmpresaQuery(
    Guid EmpresaId, Guid? CentroId = null, IReadOnlyList<Guid>? TrabajadorIds = null,
    Guid? DelegacionEmpresaId = null, Guid? OficioEmpresaId = null)
    : IRequest<IReadOnlyList<DocumentacionBloqueantePendienteDto>>;

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
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ICalculoEstadoCentroService calculoEstadoCentro,
    ReclamacionEmpresaEmpleadoraService empresaEmpleadora)
    : IRequestHandler<ObtenerReclamacionPorAccesoQuery, IReadOnlyList<LoteReclamacionAgrupadoDto>>
{
    public async Task<IReadOnlyList<LoteReclamacionAgrupadoDto>> Handle(ObtenerReclamacionPorAccesoQuery request, CancellationToken cancellationToken)
    {
        // Fallo cerrado: fuera de la Asignación de Cartera no se ofrece nada (los envíos lo vuelven a comprobar), y un Centro
        // que no se ve no exige nada a quien pregunta (tampoco lo de la Empresa, que no se acota por Centro en su lote).
        if (request.TrabajadorIds.Count == 0 || !await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken)
            || !await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return [];

        // Solo Trabajadores visibles y de esta Empresa (Trabajador.EmpresaId, no de una Subcontrata): los requisitos de los
        // demás no cuentan, tampoco para lo que el Centro exige de la propia Empresa.
        var visibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var pedidos = request.TrabajadorIds.Where(id => visibles is null || visibles.Contains(id)).Distinct().ToList();
        var trabajadores = (await trabajadoresContext.Trabajadores
                .Where(t => pedidos.Contains(t.Id) && t.EmpresaId == request.EmpresaId)
                .Select(t => t.Id).ToListAsync(cancellationToken)).ToHashSet();
        if (trabajadores.Count == 0)
            return [];

        // Lo que el Centro exige, no solo lo que bloquea el acceso: los pares exigidos del Centro 360 (ResolucionTipoDocumentoCentro,
        // con la serie por tipo de contratación y los requisitos de evento). Un Centro que no marca nada «bloquea el acceso» sigue
        // exigiendo su documentación, y reclamarla es justo lo que se pide aquí. Esto solo intersecta: lo que sale es lo que traen
        // los lotes, así que un ausente que el lote no produce (el de un requisito de evento que el Centro no incluye, o el de la
        // Empresa en un tipo que no bloquea, que PendientesDeReclamacionService saca aún de la evaluación de acceso) no aparece.
        var paresExigidos = (await calculoEstadoCentro.ObtenerParesExigidosAsync([request.CentroId], cancellationToken))
            .Where(p => p.CentroId == request.CentroId && trabajadores.Contains(p.TrabajadorId))
            .ToList();
        var exigidosTrabajador = paresExigidos.Select(p => (p.TrabajadorId, p.TipoDocumentoId)).ToHashSet();
        var exigidosEmpresa = await TiposDeEmpresaExigidosAsync(request.CentroId, request.EmpresaId, trabajadores, cancellationToken);

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

        // Los destinatarios dependen de los Tipos del lote: con lo de la Empresa dentro se vuelven a resolver (el contacto que
        // es dueño del Tipo de la Empresa también debe poder recibirlo). Si no se pueden resolver, el lote queda sin destinatario.
        var tipos = unido.Documentos.Select(d => d.TipoDocumentoId)
            .Concat(unido.PendientesSinFecha!.Select(p => p.TipoDocumentoId)).Distinct().ToList();
        var destino = await empresaEmpleadora.ResolverDestinoAsync(
            request.EmpresaId, receptor.EmpresaEmpleadora?.DelegacionEmpresaId, tipos, cancellationToken);
        unido = unido with { Destinatarios = destino.EsFallido ? [] : destino.Valor.Destinatarios };
        return [.. lotes.Select(l => ReferenceEquals(l, receptor) ? unido : l)];
    }

    /// <summary>
    /// Los Tipos de ámbito Empresa que el Centro exige a la Empresa, con la misma regla que los pares de Empresa del % del
    /// Centro (<see cref="ResolucionTipoDocumentoCentro"/>, sin serie por tipo de contratación, que es solo de ámbito Trabajador):
    /// solo si el Centro tiene gestión CAE y alguno de los Trabajadores elegidos tiene allí una Asignación activa.
    /// </summary>
    private async Task<HashSet<Guid>> TiposDeEmpresaExigidosAsync(
        Guid centroId, Guid empresaId, HashSet<Guid> trabajadores, CancellationToken cancellationToken)
    {
        if ((await CentrosSinGestionCae.FiltrarAsync(centrosContext, [centroId], cancellationToken)).Count > 0)
            return [];

        var trabajadorIds = trabajadores.ToList();
        var conAsignacion = await asignacionesContext.Asignaciones
            .AnyAsync(a => a.CentroId == centroId && a.FechaBaja == null && trabajadorIds.Contains(a.TrabajadorId), cancellationToken);
        if (!conAsignacion)
            return [];

        var tipos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa)
            .Select(t => new { t.Id, CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si })
            .ToListAsync(cancellationToken);
        var tipoIds = tipos.Select(t => t.Id).ToList();
        var filasDelCentro = (await tiposDocumentoContext.TiposDocumentoCentros
                .Where(tc => tc.CentroId == centroId && tipoIds.Contains(tc.TipoDocumentoId))
                .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        return [.. tipos
            .Where(t => ResolucionTipoDocumentoCentro.Aplica(filasDelCentro, t.Id, centroId, t.CuentaParaCumplimiento))
            .Select(t => t.Id)];
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

public class ObtenerPendientesDeSubirAPlataformaEmpresaQueryHandler(
    IMediator mediator,
    IAlcanceDatosService alcanceDatos,
    ITrabajadoresQueryContext trabajadoresContext)
    : IRequestHandler<ObtenerPendientesDeSubirAPlataformaEmpresaQuery, IReadOnlyList<DocumentacionBloqueantePendienteDto>>
{
    public async Task<IReadOnlyList<DocumentacionBloqueantePendienteDto>> Handle(
        ObtenerPendientesDeSubirAPlataformaEmpresaQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return [];

        // Solo Trabajadores de esta Empresa (Trabajador.EmpresaId): ni los de una Subcontrata cuentan para ella, tampoco en las
        // filas de ámbito Empresa.
        var deLaEmpresa = await trabajadoresContext.Trabajadores.Where(t => t.EmpresaId == request.EmpresaId)
            .Select(t => new { t.Id, t.DelegacionEmpresaId, t.OficioEmpresaId }).ToListAsync(cancellationToken);
        var idsDeLaEmpresa = deLaEmpresa.Select(t => t.Id).ToHashSet();
        var admitidos = deLaEmpresa
            .Where(t => request.DelegacionEmpresaId == null || t.DelegacionEmpresaId == request.DelegacionEmpresaId)
            .Where(t => request.OficioEmpresaId == null || t.OficioEmpresaId == request.OficioEmpresaId)
            .Where(t => request.TrabajadorIds is null || request.TrabajadorIds.Contains(t.Id))
            .Select(t => t.Id).ToHashSet();
        // Lo de la propia Empresa no es de ninguna Delegación ni Oficio: con esos filtros no entra (como en la preventiva).
        var conEmpresa = request.DelegacionEmpresaId is null && request.OficioEmpresaId is null;

        var filas = await mediator.Send(new ObtenerDocumentacionBloqueantePendienteQuery(request.CentroId), cancellationToken);
        return [.. filas
            .Where(f => f.Situacion is SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma or SituacionDeRequisitoBloqueante.SinValidarEnPlataforma)
            .Where(f => f.Ambito == AmbitoAplicacion.Empresa
                ? conEmpresa && f.EmpresaId == request.EmpresaId && idsDeLaEmpresa.Contains(f.TrabajadorId)
                    && (request.TrabajadorIds is null || admitidos.Contains(f.TrabajadorId))
                : admitidos.Contains(f.TrabajadorId))
            .DistinctBy(f => (f.CentroId, f.Ambito, f.Ambito == AmbitoAplicacion.Empresa ? Guid.Empty : f.TrabajadorId, f.TipoDocumentoId))];
    }
}