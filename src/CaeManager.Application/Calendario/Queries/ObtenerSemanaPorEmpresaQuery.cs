using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Calendario.Queries;

/// <summary>
/// Documentos operativos de Empresa y de sus Trabajadores visibles, de lunes a viernes.
/// La propiedad documental y la cartera se comprueban por separado: ver un Trabajador
/// no da acceso a los documentos de su Empresa. No incluye el ámbito Cliente empresarial.
/// </summary>
public record ObtenerSemanaPorEmpresaQuery(DateOnly Fecha) : IRequest<IReadOnlyList<SemanaEmpresaDto>>;

public record DocumentoSemanaDto(Guid DocumentoId, DateOnly Fecha, bool BloqueaAcceso,
    string TipoDocumentoNombre, string SujetoNombre);
public record SemanaEmpresaDto(Guid EmpresaId, string EmpresaNombre,
    IReadOnlyList<DocumentoSemanaDto> Documentos, bool ReclamadoHoy);

public class ObtenerSemanaPorEmpresaQueryHandler(
    IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext, IProyectosQueryContext proyectosContext,
    IReclamacionesQueryContext reclamacionesContext, ITiposDocumentoQueryContext tiposContext,
    IAlcanceDatosService alcanceDatos, IMediator mediator)
    : IRequestHandler<ObtenerSemanaPorEmpresaQuery, IReadOnlyList<SemanaEmpresaDto>>
{
    public async Task<IReadOnlyList<SemanaEmpresaDto>> Handle(ObtenerSemanaPorEmpresaQuery request, CancellationToken cancellationToken)
    {
        var lunes = request.Fecha.AddDays(-((int)request.Fecha.DayOfWeek + 6) % 7);
        var viernes = lunes.AddDays(4);
        var hoy = DiaDeNegocio.Hoy();
        var empresas = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var trabajadores = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var inactivos = ActividadVoluntarios.VoluntariosInactivosIds(trabajadoresContext.Trabajadores, proyectosContext, hoy);
        var documentos = documentosContext.Documentos.Operativos()
            .Where(d => d.FechaVencimiento >= lunes && d.FechaVencimiento <= viernes);

        var deEmpresa = await (
            from d in documentos
            where d.EmpresaId != null && (empresas == null || empresas.Contains(d.EmpresaId.Value))
            join e in empresasContext.Empresas on d.EmpresaId equals e.Id
            select new { d.Id, EmpresaId = e.Id, e.RazonSocial, d.TrabajadorId, d.TipoDocumentoId, Fecha = d.FechaVencimiento!.Value })
            .ToListAsync(cancellationToken);

        var deTrabajador = await (
            from d in documentos
            where d.TrabajadorId != null && (trabajadores == null || trabajadores.Contains(d.TrabajadorId.Value))
            join t in trabajadoresContext.Trabajadores on d.TrabajadorId equals t.Id
            where !inactivos.Contains(t.Id)
            // SubcontrataId es deuda terminológica legacy: aquí identifica la Empresa empleadora contraparte.
            join e in empresasContext.Empresas on (t.SubcontrataId ?? t.EmpresaId) equals e.Id
            select new { d.Id, EmpresaId = e.Id, e.RazonSocial, d.TrabajadorId, d.TipoDocumentoId, Fecha = d.FechaVencimiento!.Value })
            .ToListAsync(cancellationToken);

        var filas = deEmpresa.Concat(deTrabajador).ToList();
        if (filas.Count == 0) return [];

        var tipoIds = filas.Select(f => f.TipoDocumentoId).Distinct().ToList();
        var tipos = await tiposContext.TiposDocumento.Where(t => tipoIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);
        var trabajadorIds = filas.Where(f => f.TrabajadorId != null).Select(f => f.TrabajadorId!.Value).Distinct().ToList();
        var nombres = await trabajadoresContext.Trabajadores.Where(t => trabajadorIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre + " " + t.Apellidos, cancellationToken);

        var bloqueos = await mediator.Send(new ObtenerDocumentacionBloqueantePendienteQuery(), cancellationToken);
        var ids = filas.Select(f => f.Id).ToList();
        var inicio = DiaDeNegocio.InicioEnUtc(hoy);
        var fin = DiaDeNegocio.InicioEnUtc(hoy.AddDays(1));
        var reclamados = await (
            from linea in reclamacionesContext.ReclamacionesDocumentalesDocumento
            join envio in reclamacionesContext.ReclamacionesDocumentales on linea.ReclamacionDocumentalId equals envio.Id
            where linea.DocumentoId != null && ids.Contains(linea.DocumentoId.Value)
                && envio.FechaEnvioUtc >= inicio && envio.FechaEnvioUtc < fin
            select linea.DocumentoId!.Value).Distinct().ToListAsync(cancellationToken);

        return filas.GroupBy(f => new { f.EmpresaId, f.RazonSocial })
            .OrderBy(g => g.Key.RazonSocial)
            .Select(g => new SemanaEmpresaDto(g.Key.EmpresaId, g.Key.RazonSocial,
                g.Select(f => new DocumentoSemanaDto(f.Id, f.Fecha, bloqueos.Any(b =>
                    b.TipoDocumentoId == f.TipoDocumentoId &&
                    (f.TrabajadorId is { } trabajadorId
                        ? b.Ambito == AmbitoAplicacion.Trabajador && b.TrabajadorId == trabajadorId
                        : b.Ambito == AmbitoAplicacion.Empresa && b.EmpresaId == f.EmpresaId)),
                    tipos.GetValueOrDefault(f.TipoDocumentoId, string.Empty),
                    f.TrabajadorId is { } id ? nombres.GetValueOrDefault(id, string.Empty) : f.RazonSocial))
                    .OrderBy(d => d.Fecha).ThenBy(d => d.DocumentoId).ToList(),
                g.All(f => reclamados.Contains(f.Id))))
            .ToList();
    }
}
