using System.Text;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Reclamaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;

/// <summary>
/// Envía el lote de reclamación al Cliente indicado — MVP1 es siempre manual
/// (el Gestor CAE revisa la vista previa y pulsa Enviar), no hay job en
/// segundo plano todavía. DocumentoIds llega de la vista previa, así que se
/// recarga y revalida server-side (no basta con que la UI solo ofrezca Ids
/// válidos, ver P0-1 de docs/business/MATURITY_REVIEW.md) — un Id que ya no
/// cumple los criterios de la ventana (p. ej. el documento se renovó entre
/// que se abrió la vista previa y se pulsó Enviar) se descarta en silencio en
/// vez de fallar todo el envío.
/// </summary>
public record EnviarReclamacionCommand(Guid ClienteId, IReadOnlyList<Guid> DocumentoIds) : ICommand;

public class EnviarReclamacionCommandHandler(
    IClientesQueryContext clientesContext,
    IDocumentosQueryContext documentosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    IAlcanceDatosService alcanceDatos,
    IContactosClienteService contactosClienteService,
    IEmailService emailService,
    IReclamacionDocumentalRepository repositorio,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EnviarReclamacionCommand, Result>
{
    public async Task<Result> Handle(EnviarReclamacionCommand request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.TieneAccesoTotalAsync(cancellationToken))
        {
            var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
            if (clienteIdsVisibles is null || !clienteIdsVisibles.Contains(request.ClienteId))
                return Result.Fallo(Error.Crear("Reclamacion.SinAcceso", "No tienes acceso a este cliente."));
        }

        var cliente = await clientesContext.Clientes.FirstOrDefaultAsync(c => c.Id == request.ClienteId, cancellationToken);
        if (cliente is null)
            return Result.Fallo(Error.Crear("Reclamacion.ClienteNoEncontrado", "No encontramos este cliente."));

        if (request.DocumentoIds.Count == 0)
            return Result.Fallo(Error.Crear("Reclamacion.SinDocumentos", "Selecciona al menos un documento a reclamar."));

        var destinatarios = await contactosClienteService.ObtenerEmailsPortalAsync(request.ClienteId, cancellationToken);
        if (destinatarios.Count == 0)
        {
            return Result.Fallo(Error.Crear(
                "Reclamacion.SinDestinatario",
                "Este cliente no tiene ningún usuario de portal con email al que reclamar."));
        }

        var idsSolicitados = request.DocumentoIds.Distinct().ToList();

        var filas = await (
            from documento in documentosContext.Documentos
            where idsSolicitados.Contains(documento.Id)
            where documento.TrabajadorId != null && documento.FechaVencimiento != null
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            join asignacion in asignacionesContext.Asignaciones on trabajador.Id equals asignacion.TrabajadorId
            where asignacion.FechaBaja == null
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            where centro.ClienteId == request.ClienteId
            select new
            {
                DocumentoId = documento.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaVencimiento
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (filas.Count == 0)
        {
            return Result.Fallo(Error.Crear(
                "Reclamacion.SinDocumentosValidos",
                "Ninguno de los documentos seleccionados sigue siendo reclamable para este cliente — puede que ya se hayan renovado."));
        }

        var destinatarioUnico = string.Join("; ", destinatarios);
        var cuerpoHtml = ConstruirCuerpoHtml(cliente.RazonSocial, filas.Select(f => (f.TrabajadorNombre, f.TipoDocumentoNombre, f.FechaVencimiento!.Value)));

        foreach (var destinatario in destinatarios)
        {
            await emailService.EnviarAsync(
                destinatario, $"CAE Manager — documentación pendiente de {cliente.RazonSocial}", cuerpoHtml, cancellationToken);
        }

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Reclamacion.SinUsuario", "No pudimos identificar tu usuario."));

        repositorio.Agregar(new ReclamacionDocumental(
            request.ClienteId, usuarioId.Value, destinatarioUnico, DateTime.UtcNow, filas.Select(f => f.DocumentoId)));

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }

    private static string ConstruirCuerpoHtml(string razonSocialCliente, IEnumerable<(string TrabajadorNombre, string TipoDocumentoNombre, DateOnly FechaVencimiento)> documentos)
    {
        var builder = new StringBuilder();
        builder.Append("<p>Estimado/a ").Append(System.Net.WebUtility.HtmlEncode(razonSocialCliente)).Append(",</p>");
        builder.Append("<p>Los siguientes documentos de coordinación de actividades empresariales están próximos a vencer o ya han vencido. Por favor, gestiona su renovación lo antes posible:</p>");
        builder.Append("<table style=\"border-collapse:collapse;width:100%\"><thead><tr>")
            .Append("<th style=\"text-align:left;border-bottom:1px solid #ccc;padding:4px\">Trabajador</th>")
            .Append("<th style=\"text-align:left;border-bottom:1px solid #ccc;padding:4px\">Documento</th>")
            .Append("<th style=\"text-align:left;border-bottom:1px solid #ccc;padding:4px\">Vencimiento</th>")
            .Append("</tr></thead><tbody>");

        foreach (var (trabajadorNombre, tipoDocumentoNombre, fechaVencimiento) in documentos.OrderBy(d => d.FechaVencimiento))
        {
            builder.Append("<tr>")
                .Append("<td style=\"padding:4px\">").Append(System.Net.WebUtility.HtmlEncode(trabajadorNombre)).Append("</td>")
                .Append("<td style=\"padding:4px\">").Append(System.Net.WebUtility.HtmlEncode(tipoDocumentoNombre)).Append("</td>")
                .Append("<td style=\"padding:4px\">").Append(fechaVencimiento.ToString("dd/MM/yyyy")).Append("</td>")
                .Append("</tr>");
        }

        builder.Append("</tbody></table>");
        builder.Append("<p>Gracias por tu colaboración.</p>");
        return builder.ToString();
    }
}
