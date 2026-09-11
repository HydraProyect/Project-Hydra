using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;

/// <summary>
/// Envía el lote de reclamación de DOCUMENTOS DE EMPRESA a la Empresa
/// contraparte que es su titular (DEC-7, "todos los documentos de empresa de
/// una empresa") — hermano de <c>EnviarReclamacionCommand</c>, que cubre los
/// documentos de Trabajador y cuyo titular es la Empresa contraparte en
/// posición de cliente.
///
/// Comando propio y no un parámetro más del otro: lo que cambia entre los dos
/// no es un dato sino el camino entero —qué documentos son reclamables
/// (aquí <c>Documento.EmpresaId</c> directo, allí
/// Trabajador→Asignación→Centro), de qué agenda salen los destinatarios, y
/// qué columnas tiene el correo—. Meterlos en un mismo comando obligaría a un
/// ámbito con valor por defecto, y un llamador que se lo olvidara reclamaría
/// al titular equivocado en silencio. La cola común —buzón, envío, registro
/// append-only y evento— sí se comparte, en
/// <see cref="IRegistroEnvioReclamacionService"/>.
///
/// MVP1 es siempre manual (el Gestor CAE revisa la vista previa y pulsa
/// Enviar). DocumentoIds llega de esa vista previa, así que se recarga y
/// revalida server-side con el MISMO criterio de "reclamable" que
/// <see cref="Queries.ObtenerLoteReclamacionEmpresa.ObtenerLoteReclamacionEmpresaQuery"/>
/// (incluida la ventana de 3 meses): un Id que ya no cumple esos criterios
/// hace fallar el envío ENTERO en vez de mandar una parte sin avisar
/// (revisión 2026-09-11) — ver el razonamiento completo en el docstring
/// gemelo de <c>EnviarReclamacionCommand</c>.
/// </summary>
/// <param name="ContactoIdsSeleccionados">
/// Contactos marcados a mano en la pantalla. Null/vacío = "los que resuelva la
/// agenda de la Empresa", que es el camino normal; con valor, manda lo que
/// eligió el gestor — revalidado igualmente contra la agenda real, no se
/// confía en la UI.
/// </param>
public record EnviarReclamacionEmpresaCommand(
    Guid EmpresaId,
    IReadOnlyList<Guid> DocumentoIds,
    IReadOnlyList<Guid>? ContactoIdsSeleccionados = null) : ICommand<EnvioReclamacionResultado>;

public class EnviarReclamacionEmpresaCommandHandler(
    IEmpresasQueryContext empresasContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos,
    Contactos.IResolucionDestinatariosAgendaService resolucionDestinatarios,
    IRegistroEnvioReclamacionService registroEnvio)
    : IRequestHandler<EnviarReclamacionEmpresaCommand, Result<EnvioReclamacionResultado>>
{
    public async Task<Result<EnvioReclamacionResultado>> Handle(EnviarReclamacionEmpresaCommand request, CancellationToken cancellationToken)
    {
        // Cartera de Empresas, no de Clientes: reclamar es escribir historial y
        // mandar un correo en nombre del tenant, así que la puerta va antes de
        // leer nada (CLAUDE.md § 14 — una coordenada de contexto no es
        // autoridad).
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return Result.Fallo<EnvioReclamacionResultado>(Error.Crear("Reclamacion.SinAcceso", "No tienes acceso a esta empresa."));

        var empresa = await empresasContext.Empresas
            .FirstOrDefaultAsync(e => e.Id == request.EmpresaId, cancellationToken);
        if (empresa is null)
            return Result.Fallo<EnvioReclamacionResultado>(Error.Crear("Reclamacion.EmpresaNoEncontrada", "No encontramos esta empresa."));

        if (request.DocumentoIds.Count == 0)
            return Result.Fallo<EnvioReclamacionResultado>(Error.Crear("Reclamacion.SinDocumentos", "Selecciona al menos un documento a reclamar."));

        var idsSolicitados = request.DocumentoIds.Distinct().ToList();

        // Misma ventana que ObtenerLoteReclamacionEmpresaQuery (3 meses, sin
        // límite inferior), además de los criterios de siempre revalidados
        // contra la base: que el documento siga siendo de ESTA Empresa y de
        // ámbito Empresa. Sin el filtro de ámbito, un Id de un documento de
        // Cliente de la misma Empresa contraparte entraría en el lote.
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var limiteVentana = hoy.AddMonths(3);

        var filas = await (
            from documento in documentosContext.Documentos
            where idsSolicitados.Contains(documento.Id)
            where documento.EmpresaId == request.EmpresaId && documento.FechaVencimiento != null
            where documento.FechaVencimiento <= limiteVentana
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where tipoDocumento.AmbitoAplicacion == AmbitoAplicacion.Empresa
            select new
            {
                DocumentoId = documento.Id,
                TipoDocumentoId = tipoDocumento.Id,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaVencimiento
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (filas.Count == 0)
        {
            return Result.Fallo<EnvioReclamacionResultado>(Error.Crear(
                "Reclamacion.SinDocumentosValidos",
                "Ninguno de los documentos seleccionados sigue siendo reclamable para esta empresa — puede que ya se hayan renovado."));
        }

        // Todo o nada (revisión 2026-09-11), igual que EnviarReclamacionCommand:
        // si algo de lo pedido ya no es reclamable, el envío entero falla en
        // vez de mandar solo lo que sobrevivió.
        var idsEncontrados = filas.Select(f => f.DocumentoId).ToHashSet();
        if (idsSolicitados.Exists(id => !idsEncontrados.Contains(id)))
        {
            return Result.Fallo<EnvioReclamacionResultado>(Error.Crear(
                "Reclamacion.DocumentosDesactualizados",
                "Algunos de los documentos seleccionados ya no son reclamables — puede que se hayan renovado o hayan salido de la ventana de reclamación. Actualiza la vista antes de volver a intentarlo."));
        }

        var resueltos = await resolucionDestinatarios.ResolverParaEmpresaAsync(
            request.EmpresaId, filas.Select(f => f.TipoDocumentoId).Distinct().ToList(), cancellationToken);

        // La selección manual se filtra CONTRA lo resuelto, no lo sustituye:
        // así un Id de contacto de otra Empresa colado a mano no se convierte
        // en destinatario. Mismo "todo o nada" que con los documentos.
        if (request.ContactoIdsSeleccionados is { Count: > 0 } seleccionados)
        {
            var contactoIdsResueltos = resueltos.Select(d => d.ContactoId).ToHashSet();
            if (seleccionados.Any(id => !contactoIdsResueltos.Contains(id)))
            {
                return Result.Fallo<EnvioReclamacionResultado>(Error.Crear(
                    "Reclamacion.ContactosDesactualizados",
                    "Alguno de los contactos seleccionados ya no está en la agenda para esta documentación. Actualiza la vista antes de volver a intentarlo."));
            }

            resueltos = [.. resueltos.Where(d => seleccionados.Contains(d.ContactoId))];
        }

        if (resueltos.Count == 0)
        {
            return Result.Fallo<EnvioReclamacionResultado>(Error.Crear(
                "Reclamacion.SinDestinatario",
                "No hay ningún contacto en la agenda al que reclamar esta documentación — añade uno en la ficha de la empresa."));
        }

        var documentoIds = filas.Select(f => f.DocumentoId).Distinct().ToList();
        var destinatarios = resueltos.Select(d => d.Email).Distinct().ToList();
        var asunto = $"{Marca.Nombre} — documentación pendiente de {empresa.RazonSocial}";
        var cuerpoHtml = ConstruirCuerpoHtml(
            empresa.RazonSocial, filas.Select(f => (f.TipoDocumentoNombre, f.FechaVencimiento!.Value)));

        var envio = await registroEnvio.EnviarYRegistrarAsync(
            new TitularReclamacion(request.EmpresaId, empresa.RazonSocial, AmbitoAplicacion.Empresa),
            documentoIds, destinatarios, asunto, cuerpoHtml, cancellationToken);

        return envio.EsFallido
            ? Result.Fallo<EnvioReclamacionResultado>(envio.Error)
            : Result.Exito(new EnvioReclamacionResultado(documentoIds, destinatarios));
    }

    /// <summary>
    /// Mismo correo que el de Trabajador menos la columna "Trabajador": en un
    /// documento de empresa el propietario es la propia destinataria, así que
    /// repetir su razón social en cada fila no informaría de nada.
    /// </summary>
    private static string ConstruirCuerpoHtml(
        string razonSocialEmpresa, IEnumerable<(string TipoDocumentoNombre, DateOnly FechaVencimiento)> documentos)
    {
        var builder = new StringBuilder();
        builder.Append("<p>Estimado/a ").Append(System.Net.WebUtility.HtmlEncode(razonSocialEmpresa)).Append(",</p>");
        builder.Append("<p>Los siguientes documentos de coordinación de actividades empresariales están próximos a vencer o ya han vencido. Por favor, gestiona su renovación lo antes posible:</p>");
        builder.Append("<table style=\"border-collapse:collapse;width:100%\"><thead><tr>")
            .Append("<th style=\"text-align:left;border-bottom:1px solid #ccc;padding:4px\">Documento</th>")
            .Append("<th style=\"text-align:left;border-bottom:1px solid #ccc;padding:4px\">Vencimiento</th>")
            .Append("</tr></thead><tbody>");

        foreach (var (tipoDocumentoNombre, fechaVencimiento) in documentos.OrderBy(d => d.FechaVencimiento))
        {
            builder.Append("<tr>")
                .Append("<td style=\"padding:4px\">").Append(System.Net.WebUtility.HtmlEncode(tipoDocumentoNombre)).Append("</td>")
                .Append("<td style=\"padding:4px\">").Append(fechaVencimiento.ToString("dd/MM/yyyy")).Append("</td>")
                .Append("</tr>");
        }

        builder.Append("</tbody></table>");
        builder.Append("<p>Gracias por tu colaboración.</p>");
        return builder.ToString();
    }
}
