using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reportes.Queries;

/// <summary>
/// Generaliza ObtenerReporteDocumentosQuery con alcance (Reportes TALVEG.dc.html,
/// pestañas "Vigencia documental"/"Incidencias" de la Biblioteca): son el
/// MISMO informe, "Incidencias" es "Vigencia documental" con
/// <see cref="IncluirVigentes"/> en falso — el propio mockup describe
/// Incidencias como "vencidos, rechazos de portal y bloqueos del periodo",
/// pero ni rechazo de portal ni bloqueo de periodo existen como concepto de
/// dominio en ningún sitio; el único dato real detrás de "Incidencias" es
/// Vencido/Urgente, así que el informe solo promete eso.
/// </summary>
public record GenerarInformeVigenciaQuery(Guid? ClienteId, Guid? CentroId, bool IncluirVigentes)
    : IRequest<InformeVigenciaDto>;

public record InformeVigenciaDto(string Alcance, IReadOnlyList<FilaReporteDocumentoDto> Filas);

public record FilaReporteDocumentoDto(
    Guid DocumentoId,
    string TrabajadorNombre,
    string EmpresaRazonSocial,
    string TipoDocumentoNombre,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado);

public class GenerarInformeVigenciaQueryHandler(
    IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext,
    IEmpresasQueryContext empresasContext, ISubcontratasQueryContext subcontratasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext)
    : IRequestHandler<GenerarInformeVigenciaQuery, InformeVigenciaDto>
{
    public async Task<InformeVigenciaDto> Handle(GenerarInformeVigenciaQuery request, CancellationToken cancellationToken)
    {
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        // El alcance por cliente/centro se resuelve por asignación activa —
        // un Trabajador puede tener Documentos aunque hoy no esté asignado a
        // ningún centro de este Cliente, y ese Documento no pertenece al
        // alcance que se está pidiendo.
        IQueryable<Guid> trabajadorIdsEnAlcance = asignacionesContext.Asignaciones.Where(a => a.FechaBaja == null).Select(a => a.TrabajadorId);
        if (request.CentroId is { } centroId)
            trabajadorIdsEnAlcance = asignacionesContext.Asignaciones.Where(a => a.FechaBaja == null && a.CentroId == centroId).Select(a => a.TrabajadorId);
        else if (request.ClienteId is { } clienteId)
            trabajadorIdsEnAlcance =
                from a in asignacionesContext.Asignaciones
                join c in centrosContext.Centros on a.CentroId equals c.Id
                where a.FechaBaja == null && c.ClienteId == clienteId
                select a.TrabajadorId;

        var consultaBase =
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            join empresa in empresasContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in subcontratasContext.Subcontratas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            select new
            {
                documento.Id,
                documento.TrabajadorId,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                RazonSocial = empresa != null ? empresa.RazonSocial : subcontrata!.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaVencimiento
            };

        if (request.ClienteId is not null || request.CentroId is not null)
            consultaBase = consultaBase.Where(f => trabajadorIdsEnAlcance.Contains(f.TrabajadorId!.Value));

        var filas = await consultaBase.ToListAsync(cancellationToken);

        var conEstado = filas
            .Select(f => new FilaReporteDocumentoDto(
                f.Id, f.TrabajadorNombre, f.RazonSocial, f.TipoDocumentoNombre, f.FechaVencimiento,
                CalculadoraEstadoDocumento.Calcular(f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)))
            .Where(f => request.IncluirVigentes || f.Estado is EstadoDocumento.Vencido or EstadoDocumento.Urgente)
            .OrderBy(f => f.Estado switch
            {
                EstadoDocumento.Vencido => 0,
                EstadoDocumento.Urgente => 1,
                EstadoDocumento.Proximo => 2,
                _ => 3
            })
            .ThenBy(f => f.FechaVencimiento)
            .ToList();

        var alcance = await ResolverTextoAlcanceAsync(request.ClienteId, request.CentroId, cancellationToken);
        return new InformeVigenciaDto(alcance, conEstado);
    }

    private async Task<string> ResolverTextoAlcanceAsync(Guid? clienteId, Guid? centroId, CancellationToken cancellationToken)
    {
        if (centroId is { } cId)
        {
            var centro = await centrosContext.Centros.Where(c => c.Id == cId).Select(c => c.Nombre).SingleOrDefaultAsync(cancellationToken);
            return centro is null ? "Centro" : $"Solo centro: {centro}";
        }

        if (clienteId is { } clId)
        {
            var cliente = await clientesContext.Clientes.Where(c => c.Id == clId).Select(c => c.RazonSocial).SingleOrDefaultAsync(cancellationToken);
            return cliente is null ? "Cliente" : $"{cliente} · todo el cliente";
        }

        return "Toda la cartera (sin filtrar por cliente)";
    }
}
