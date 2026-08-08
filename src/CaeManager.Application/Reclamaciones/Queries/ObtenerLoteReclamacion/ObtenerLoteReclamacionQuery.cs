using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;

/// <summary>
/// Agrupa por Cliente los Documentos de sus Trabajadores (vía Asignación
/// activa a un Centro de ese Cliente — mismo join que
/// ObtenerAlertasQueryHandler.ObtenerFaltantesAsync) que vencen dentro de los
/// próximos 3 meses o ya vencieron, para poder reclamarlos en un único correo
/// por Cliente en vez de uno por Documento. Mismo alcance limitado que
/// Alertas: solo Documentos de Trabajador, no de Cliente/Empresa (ver ese
/// comentario). Un mismo Trabajador con Asignaciones activas en Centros de
/// varios Clientes (relación Empresa-Cliente N:N, ver DOMAIN.md) puede
/// aparecer reclamado desde más de un Cliente — es el comportamiento
/// correcto: cada titular necesita saberlo para su propio Centro.
/// </summary>
public record ObtenerLoteReclamacionQuery : IRequest<IReadOnlyList<LoteReclamacionClienteDto>>;

public record LoteReclamacionClienteDto(
    Guid ClienteId,
    string RazonSocialCliente,
    IReadOnlyList<string> DestinatariosEmail,
    DateTime? UltimaReclamacionFechaUtc,
    IReadOnlyList<DocumentoReclamableDto> Documentos);

public record DocumentoReclamableDto(
    Guid DocumentoId,
    Guid TrabajadorId,
    string TrabajadorNombre,
    Guid TipoDocumentoId,
    string TipoDocumentoNombre,
    DateOnly FechaVencimiento,
    EstadoDocumento Estado);

public class ObtenerLoteReclamacionQueryHandler(
    IConfiguracionQueryContext configuracionContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    IClientesQueryContext clientesContext,
    IReclamacionesQueryContext reclamacionesContext,
    IAlcanceDatosService alcanceDatos,
    IContactosClienteService contactosClienteService)
    : IRequestHandler<ObtenerLoteReclamacionQuery, IReadOnlyList<LoteReclamacionClienteDto>>
{
    public async Task<IReadOnlyList<LoteReclamacionClienteDto>> Handle(
        ObtenerLoteReclamacionQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var limiteVentana = hoy.AddMonths(3);

        var filas = await (
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            where documento.FechaVencimiento != null && documento.FechaVencimiento <= limiteVentana
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            join asignacion in asignacionesContext.Asignaciones on trabajador.Id equals asignacion.TrabajadorId
            where asignacion.FechaBaja == null
            where centroIdsVisibles == null || centroIdsVisibles.Contains(asignacion.CentroId)
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(centro.ClienteId)
            join cliente in clientesContext.Clientes on centro.ClienteId equals cliente.Id
            select new
            {
                cliente.Id,
                cliente.RazonSocial,
                DocumentoId = documento.Id,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                TipoDocumentoId = tipoDocumento.Id,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaVencimiento
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        // GetValueOrDefault sobre un Dictionary<Guid, DateTime> devolvería
        // DateTime.MinValue para un cliente sin reclamaciones previas, no
        // null — de ahí el valor explícitamente nullable aquí.
        var ultimasReclamaciones = await reclamacionesContext.ReclamacionesDocumentales
            .GroupBy(r => r.ClienteId)
            .Select(g => new { ClienteId = g.Key, Ultima = (DateTime?)g.Max(r => r.FechaEnvioUtc) })
            .ToDictionaryAsync(x => x.ClienteId, x => x.Ultima, cancellationToken);

        var resultado = new List<LoteReclamacionClienteDto>();

        foreach (var grupoCliente in filas.GroupBy(f => new { f.Id, f.RazonSocial }))
        {
            // Sin filtrar por Estado: el filtro SQL de arriba (FechaVencimiento
            // <= limiteVentana) ya acota la ventana de 1 a 3 meses que pidió el
            // usuario. Filtrar además por Proximo/Urgente/Vencido reintroduciría
            // el umbral corto de Alertas (30/15 días por defecto) y dejaría
            // fuera justo los documentos a 2-3 meses que esta ventana existe
            // para capturar con antelación — Estado se calcula solo para
            // mostrarlo como badge informativo en la fila.
            var documentos = grupoCliente
                .Select(f => new DocumentoReclamableDto(
                    f.DocumentoId, f.TrabajadorId, f.TrabajadorNombre, f.TipoDocumentoId, f.TipoDocumentoNombre,
                    f.FechaVencimiento!.Value,
                    CalculadoraEstadoDocumento.Calcular(
                        f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)))
                .OrderBy(d => d.FechaVencimiento)
                .ToList();

            if (documentos.Count == 0) continue;

            var destinatarios = await contactosClienteService.ObtenerEmailsPortalAsync(grupoCliente.Key.Id, cancellationToken);

            resultado.Add(new LoteReclamacionClienteDto(
                grupoCliente.Key.Id,
                grupoCliente.Key.RazonSocial,
                destinatarios,
                ultimasReclamaciones.GetValueOrDefault(grupoCliente.Key.Id),
                documentos));
        }

        return resultado
            .OrderByDescending(r => r.Documentos.Any(d => d.Estado == EstadoDocumento.Vencido))
            .ThenBy(r => r.RazonSocialCliente)
            .ToList();
    }
}
