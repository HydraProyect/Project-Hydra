using System.Text;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;

/// <summary>
/// Envía el lote de reclamación de documentos de TRABAJADOR a la Empresa
/// contraparte en posición de cliente — su hermano
/// <c>EnviarReclamacionEmpresaCommand</c> cubre los documentos de empresa,
/// cuyo titular es la propia Empresa a la que pertenecen (DEC-7). Se
/// mantienen separados porque lo que de verdad difiere es el join que decide
/// qué documentos son reclamables (aquí Trabajador→Asignación→Centro, allí
/// Documento.EmpresaId directo) y la agenda que resuelve los destinatarios;
/// la cola común —buzón, envío, registro y evento— la comparten en
/// <see cref="IRegistroEnvioReclamacionService"/>, que es la parte que no
/// puede divergir. MVP1 es siempre manual
/// (el Gestor CAE revisa la vista previa y pulsa Enviar), no hay job en
/// segundo plano todavía. DocumentoIds llega de la vista previa, así que se
/// recarga y revalida server-side (no basta con que la UI solo ofrezca Ids
/// válidos, ver P0-1 de docs/business/MATURITY_REVIEW.md) con el MISMO
/// criterio de "reclamable" que ObtenerLoteReclamacionQuery (incluida la
/// ventana de 3 meses) — un Id que ya no cumple esos criterios (p. ej. el
/// documento se renovó, o un contacto salió de la agenda, entre que se abrió
/// la vista previa y se pulsó Enviar) hace fallar el envío ENTERO en vez de
/// mandar una parte sin avisar (revisión 2026-09-11): es correo real a
/// terceros, y en este flujo manual la discrepancia típica es una carrera
/// corta, no un intento malicioso — forzar refrescar la vista previa y
/// reintentar es más seguro que un envío parcial silencioso.
/// </summary>
/// <param name="CentroId">
/// Acota la resolución de destinatarios a la agenda de ese Centro antes de caer
/// a la del Cliente — lo rellena el disparo desde Centro 360. Null desde
/// /documentos, que reclama por Cliente completo.
/// </param>
/// <param name="ContactoIdsSeleccionados">
/// Contactos marcados a mano en la pantalla. Null/vacío = "los que resuelva la
/// agenda", que es el camino normal; con valor, manda lo que eligió el gestor —
/// revalidado igualmente contra la agenda real, no se confía en la UI.
/// </param>
public record EnviarReclamacionCommand(
    Guid ClienteId,
    IReadOnlyList<Guid> DocumentoIds,
    Guid? CentroId = null,
    IReadOnlyList<Guid>? ContactoIdsSeleccionados = null) : ICommand<EnvioReclamacionResultado>;

public class EnviarReclamacionCommandHandler(
    IEmpresasQueryContext empresasContext,
    IDocumentosQueryContext documentosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    IAlcanceDatosService alcanceDatos,
    Contactos.IResolucionDestinatariosAgendaService resolucionDestinatarios,
    IRegistroEnvioReclamacionService registroEnvio)
    : IRequestHandler<EnviarReclamacionCommand, Result<EnvioReclamacionResultado>>
{
    public async Task<Result<EnvioReclamacionResultado>> Handle(EnviarReclamacionCommand request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.TieneAccesoTotalAsync(cancellationToken))
        {
            var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
            if (clienteIdsVisibles is null || !clienteIdsVisibles.Contains(request.ClienteId))
                return Result.Fallo<EnvioReclamacionResultado>(Error.Crear("Reclamacion.SinAcceso", "No tienes acceso a este cliente."));
        }

        var cliente = await empresasContext.Empresas.FirstOrDefaultAsync(c => c.Id == request.ClienteId, cancellationToken);
        if (cliente is null)
            return Result.Fallo<EnvioReclamacionResultado>(Error.Crear("Reclamacion.ClienteNoEncontrado", "No encontramos este cliente."));

        if (request.DocumentoIds.Count == 0)
            return Result.Fallo<EnvioReclamacionResultado>(Error.Crear("Reclamacion.SinDocumentos", "Selecciona al menos un documento a reclamar."));

        var idsSolicitados = request.DocumentoIds.Distinct().ToList();

        // Misma ventana que ObtenerLoteReclamacionQuery (3 meses, sin límite
        // inferior): un DocumentoId que la vista previa nunca habría ofrecido
        // no puede colarse aquí como "reclamable" solo porque llegó en la
        // petición.
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var limiteVentana = hoy.AddMonths(3);

        var filas = await (
            from documento in documentosContext.Documentos
            where idsSolicitados.Contains(documento.Id)
            where documento.TrabajadorId != null && documento.FechaVencimiento != null
            where documento.FechaVencimiento <= limiteVentana
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
                "Ninguno de los documentos seleccionados sigue siendo reclamable para este cliente — puede que ya se hayan renovado."));
        }

        // Todo o nada (revisión 2026-09-11): si algo de lo pedido ya no es
        // reclamable, el envío entero falla en vez de mandar solo lo que
        // sobrevivió — un tercero no puede recibir menos de lo que el Gestor
        // CAE vio y confirmó en la vista previa sin que nadie se entere.
        var idsEncontrados = filas.Select(f => f.DocumentoId).ToHashSet();
        if (idsSolicitados.Exists(id => !idsEncontrados.Contains(id)))
        {
            return Result.Fallo<EnvioReclamacionResultado>(Error.Crear(
                "Reclamacion.DocumentosDesactualizados",
                "Algunos de los documentos seleccionados ya no son reclamables — puede que se hayan renovado o hayan salido de la ventana de reclamación. Actualiza la vista antes de volver a intentarlo."));
        }

        // La agenda decide a quién se le pide cada documento. Ya no se usan los
        // usuarios de portal (decisión del usuario 2026-08-13): tener cuenta en
        // el portal no significa estar en el flujo documental — puede ser el
        // dueño de la empresa o un comercial.
        var resueltos = await resolucionDestinatarios.ResolverAsync(
            request.ClienteId, request.CentroId, filas.Select(f => f.TipoDocumentoId).Distinct().ToList(), cancellationToken);

        // La selección manual se filtra CONTRA lo resuelto, no lo sustituye:
        // así un Id de contacto de otro cliente colado a mano no se convierte
        // en destinatario. Mismo "todo o nada" que con los documentos: un
        // contacto marcado en la vista previa que ya no resuelve la agenda
        // (se borró, cambió de tipo asignado) hace fallar el envío entero.
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
                "No hay ningún contacto en la agenda al que reclamar esta documentación — añade uno en la ficha del cliente."));
        }

        var documentoIds = filas.Select(f => f.DocumentoId).Distinct().ToList();
        var destinatarios = resueltos.Select(d => d.Email).Distinct().ToList();
        var asunto = $"{Marca.Nombre} — documentación pendiente de {cliente.RazonSocial}";
        var cuerpoHtml = ConstruirCuerpoHtml(cliente.RazonSocial, filas.Select(f => (f.TrabajadorNombre, f.TipoDocumentoNombre, f.FechaVencimiento!.Value)));

        var envio = await registroEnvio.EnviarYRegistrarAsync(
            new TitularReclamacion(request.ClienteId, cliente.RazonSocial, AmbitoAplicacion.Cliente),
            documentoIds, destinatarios, asunto, cuerpoHtml, cancellationToken);

        return envio.EsFallido
            ? Result.Fallo<EnvioReclamacionResultado>(envio.Error)
            : Result.Exito(new EnvioReclamacionResultado(documentoIds, destinatarios));
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
