using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerBorradorPedirPrioridad;

/// <summary>
/// Fase G ("Pedir prioridad"): construye un borrador de correo — asunto,
/// cuerpo y destinatario sugerido — a partir de los documentos obligatorios
/// que le faltan a cada Trabajador del Centro (misma lógica
/// <see cref="IDocumentosFaltantesService"/> que ya usa la Bandeja del
/// gestor y el preflight de asignación en lote), agrupados por
/// Empresa/Subcontrata → Trabajador y ordenados por
/// <c>TipoDocumento.Orden</c>. El Gestor edita el borrador antes de enviarlo
/// — "plantilla adaptable" es literal: nada se envía sin que alguien lo
/// revise primero.
/// </summary>
public record ObtenerBorradorPedirPrioridadQuery(Guid CentroId) : IRequest<Result<BorradorPedirPrioridadDto>>;

public record BorradorPedirPrioridadDto(
    string? DestinatarioSugerido,
    string Asunto,
    string CuerpoHtml,
    bool HayConexionDisponible,
    DateTime? UltimaSolicitudEnviadaEnUtc,
    int TotalDocumentosPendientes);

public class ObtenerBorradorPedirPrioridadQueryHandler(
    ICentrosQueryContext centrosContext,
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IIntegracionesQueryContext integracionesContext,
    IComunicacionesQueryContext comunicacionesContext,
    IDocumentosFaltantesService documentosFaltantesService,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerBorradorPedirPrioridadQuery, Result<BorradorPedirPrioridadDto>>
{
    public async Task<Result<BorradorPedirPrioridadDto>> Handle(
        ObtenerBorradorPedirPrioridadQuery request, CancellationToken cancellationToken)
    {
        var centro = await centrosContext.Centros.FirstOrDefaultAsync(c => c.Id == request.CentroId, cancellationToken);
        if (centro is null || !await alcanceDatos.CentroVisibleAsync(centro.Id, cancellationToken))
            return Result.Fallo<BorradorPedirPrioridadDto>(Error.Crear("Centro.NoEncontrado", "No encontramos este centro."));

        var trabajadoresDelCentro = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.CentroId == centro.Id && asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            join empresa in empresasContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in empresasContext.Empresas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            select new
            {
                trabajador.Id,
                Nombre = trabajador.Nombre + " " + trabajador.Apellidos,
                EmpleadorNombre = empresa != null ? empresa.RazonSocial : (subcontrata != null ? subcontrata.RazonSocial : "Sin empleador")
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (trabajadoresDelCentro.Count == 0)
            return Result.Fallo<BorradorPedirPrioridadDto>(Error.Crear(
                "SolicitudPrioridad.SinTrabajadores", "Este centro no tiene trabajadores asignados."));

        var parejas = trabajadoresDelCentro
            .Select(t => new ParejaTrabajadorCentro(t.Id, t.Nombre, centro.Id, centro.Nombre))
            .ToList();

        var faltantes = await documentosFaltantesService.CalcularAsync(parejas, cancellationToken);
        if (faltantes.Count == 0)
            return Result.Fallo<BorradorPedirPrioridadDto>(Error.Crear(
                "SolicitudPrioridad.SinPendientes", "No hay documentos obligatorios pendientes en este centro."));

        var ordenPorTipo = await tiposDocumentoContext.TiposDocumento
            .Where(t => faltantes.Select(f => f.TipoDocumentoId).Contains(t.Id))
            .Select(t => new { t.Id, t.Orden })
            .ToDictionaryAsync(t => t.Id, t => t.Orden, cancellationToken);

        var empleadorPorTrabajador = trabajadoresDelCentro.ToDictionary(t => t.Id, t => t.EmpleadorNombre);

        var cuerpoHtml = ConstruirCuerpoHtml(centro.Nombre, faltantes, empleadorPorTrabajador, ordenPorTipo);
        var asunto = $"Solicitud de validación prioritaria — {centro.Nombre}";

        var destinatarioSugerido = await ResolverDestinatarioAsync(centro, cancellationToken);
        var conexionId = await ResolverConexionIntegracionAsync(centro.ClienteId, cancellationToken);

        var ultimaSolicitud = await comunicacionesContext.SolicitudesPrioridadDocumento
            .Where(s => s.CentroId == centro.Id)
            .OrderByDescending(s => s.EnviadaEnUtc)
            .Select(s => (DateTime?)s.EnviadaEnUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return Result.Exito(new BorradorPedirPrioridadDto(
            destinatarioSugerido, asunto, cuerpoHtml, conexionId is not null, ultimaSolicitud, faltantes.Count));
    }

    private static string ConstruirCuerpoHtml(
        string centroNombre,
        IReadOnlyList<DocumentoFaltanteDto> faltantes,
        IReadOnlyDictionary<Guid, string> empleadorPorTrabajador,
        IReadOnlyDictionary<Guid, int> ordenPorTipo)
    {
        var sb = new StringBuilder();
        sb.Append("<p>Buenos días,</p>");
        sb.Append($"<p>Os escribimos para pedir, con carácter prioritario, la validación de la siguiente documentación pendiente en <strong>{centroNombre}</strong>:</p>");

        var porEmpleador = faltantes
            .GroupBy(f => empleadorPorTrabajador.GetValueOrDefault(f.TrabajadorId, "Sin empleador"))
            .OrderBy(g => g.Key);

        foreach (var grupoEmpleador in porEmpleador)
        {
            sb.Append($"<p><strong>{grupoEmpleador.Key}</strong></p><ul>");

            var porTrabajador = grupoEmpleador
                .GroupBy(f => (f.TrabajadorId, f.TrabajadorNombre))
                .OrderBy(g => g.Key.TrabajadorNombre);

            foreach (var grupoTrabajador in porTrabajador)
            {
                var tipos = grupoTrabajador
                    .OrderBy(f => ordenPorTipo.GetValueOrDefault(f.TipoDocumentoId, int.MaxValue))
                    .Select(f => f.TipoDocumentoNombre);

                sb.Append($"<li>{grupoTrabajador.Key.TrabajadorNombre}: {string.Join(", ", tipos)}</li>");
            }

            sb.Append("</ul>");
        }

        sb.Append("<p>Agradecemos mucho vuestra ayuda para poder tramitarlo cuanto antes.</p><p>Un saludo.</p>");
        return sb.ToString();
    }

    private async Task<string?> ResolverDestinatarioAsync(Centro centro, CancellationToken cancellationToken)
    {
        // N canales por Centro desde el Lote 0-E: se prefiere el principal, y
        // entre los demás cualquiera de tipo Email — un Centro puede tener a la
        // vez portal y correo, y solo el segundo da un destinatario.
        var emailDestino = await centrosContext.CanalesGestionDocumental
            .Where(c => c.CentroId == centro.Id && c.Tipo == TipoCanalGestion.Email && c.EmailsDestinatarios != null)
            .OrderByDescending(c => c.EsPrincipal)
            .Select(c => c.EmailsDestinatarios)
            .FirstOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(emailDestino))
            return emailDestino;

        // Fallback: Centro.Contacto es texto libre sin garantía de ser un
        // email — solo se ofrece como sugerencia si tiene pinta de email
        // (contiene "@"); si no, el Gestor lo rellena a mano en la UI.
        if (!string.IsNullOrWhiteSpace(centro.Contacto) && centro.Contacto.Contains('@'))
            return centro.Contacto;

        return null;
    }

    // GestorPropietarioId != null excluido a propósito — ver el mismo
    // comentario en EnviarReclamacionCommand: un buzón personal también tiene
    // ClienteId null, y el borrador que se acaba enviando (PedirPrioridadValidacionCommand)
    // debe resolver el mismo buzón que este preview ya mostró, nunca el
    // personal de un gestor cualquiera.
    private async Task<Guid?> ResolverConexionIntegracionAsync(Guid? clienteId, CancellationToken cancellationToken) =>
        await integracionesContext.ConexionesIntegracion
            .Where(c => c.Proveedor == ProveedorIntegracion.Microsoft365 && c.Estado == EstadoConexionIntegracion.Habilitada)
            .Where(c => c.GestorPropietarioId == null)
            .Where(c => c.ClienteId == null || c.ClienteId == clienteId)
            .OrderByDescending(c => c.ClienteId != null)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
