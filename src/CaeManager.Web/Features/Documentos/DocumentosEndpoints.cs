using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Importacion;
using CaeManager.Domain.Auditoria;
using CaeManager.Web.Exportacion;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Sirve el PDF adjunto de un Documento vía un endpoint autenticado — nunca
/// como archivo estático público, precisamente porque IFileStorageService
/// guarda fuera de wwwroot (ver ARCHITECTURE.md, "Archivos").
/// </summary>
public static class DocumentosEndpoints
{
    /// <summary>
    /// Prohíbe almacenar la respuesta en cualquier caché. Sin una directiva
    /// explícita, un navegador puede aplicar caducidad heurística y dejar el
    /// PDF en su caché de disco: un reconocimiento médico —art. 9 RGPD—
    /// sobreviviendo al cierre de sesión en un equipo compartido, que es
    /// justamente lo que servirlo por endpoint autenticado quería evitar.
    ///
    /// Solo se aplica a lo que lleva datos del tenant. No se sube a
    /// <c>UseCabecerasSeguridad</c> porque ahí alcanzaría también a los
    /// estáticos, que sí deben cachearse. <c>X-Content-Type-Options: nosniff</c>
    /// ya lo pone ese middleware para toda la aplicación, esta ruta incluida.
    ///
    /// <c>Pragma</c> es para los intermediarios que solo entienden HTTP/1.0;
    /// es redundante en cualquier cliente actual y no molesta.
    /// </summary>
    private static void ProhibirCache(HttpContext contexto)
    {
        contexto.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        contexto.Response.Headers.Pragma = "no-cache";
    }

    public static IEndpointRouteBuilder MapDocumentosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/documentos/{id:guid}/archivo", async (
            Guid id, HttpContext contexto, IMediator mediator, IFileStorageService almacenamiento,
            IRegistroAccesoDocumentoSensibleService registroAcceso, CancellationToken cancellationToken) =>
        {
            var documento = await mediator.Send(new ObtenerDocumentoPorIdQuery(id), cancellationToken);
            if (documento?.ArchivoUrl is null)
                return Results.NotFound();

            ProhibirCache(contexto);

            // DEC-36 (REC-099): registra el acceso antes de abrir el archivo
            // — la autorización ya pasó (ObtenerDocumentoPorIdQuery resolvió
            // alcance), así que a partir de aquí el acceso es efectivo. Solo
            // deja fila si el Documento resulta sensible.
            await registroAcceso.RegistrarSiSensibleAsync(documento.Id, TipoAccesoDocumentoSensible.Apertura, cancellationToken);

            var flujo = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
            // enableRangeProcessing: el visor de PDF del navegador pide por
            // rangos al paginar/buscar en vez de volver a traer el archivo
            // entero en cada petición. No reduce el coste del servidor —
            // DiskFileStorageService ya descifra el archivo completo en
            // memoria antes de servirlo (medición de Módulo 2, PR #360) — la
            // lectura por rangos que sí lo haría exige un formato cifrado por
            // bloques, pendiente de decisión.
            return Results.File(flujo, "application/pdf", $"{documento.TipoDocumentoNombre}.pdf", enableRangeProcessing: true);
        });

        endpoints.MapGet("/documentos/plantilla.xlsx", (IPlantillaDocumentosService servicio) =>
            Results.File(
                servicio.GenerarPlantilla(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "plantilla-documentos.xlsx"));

        // Mismo patrón de referencia que ClientesEndpoints. Sin columna de
        // Plataformas/Acreditaciones: ObtenerDocumentosQueryHandler la
        // resuelve con una segunda pasada fuera de la página, y repetirla
        // por cada lote de PaginadorExportacion sería un N+1.
        // Lleva el listado completo de Documentos del tenant (propietario,
        // tipo, fechas): mismo criterio de caché que el PDF.
        endpoints.MapGet("/documentos/exportar.xlsx", async (
            HttpContext contexto, IMediator mediator, CancellationToken cancellationToken) =>
        {
            ProhibirCache(contexto);

            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Documentos");

            hoja.Cell(1, 1).Value = "Propietario";
            hoja.Cell(1, 2).Value = "Ámbito";
            hoja.Cell(1, 3).Value = "Tipo de documento";
            hoja.Cell(1, 4).Value = "Emisión";
            hoja.Cell(1, 5).Value = "Vencimiento";
            hoja.Cell(1, 6).Value = "Estado";
            hoja.Row(1).Style.Font.Bold = true;

            var fila = 2;
            await foreach (var documento in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerDocumentosQuery(TrabajadorId: null, Ambito: null, Busqueda: null, Pagina: pagina, TamanoPagina: tamanoPagina),
                    cancellationToken)))
            {
                hoja.Cell(fila, 1).Value = documento.PropietarioNombre;
                hoja.Cell(fila, 2).Value = documento.Ambito.ToString();
                hoja.Cell(fila, 3).Value = documento.TipoDocumentoNombre;
                hoja.Cell(fila, 4).Value = documento.FechaEmision.ToDateTime(TimeOnly.MinValue);
                if (documento.FechaVencimiento is not null)
                    hoja.Cell(fila, 5).Value = documento.FechaVencimiento.Value.ToDateTime(TimeOnly.MinValue);
                hoja.Cell(fila, 6).Value = EstadoDocumentoUi.Texto(documento.Estado);
                fila++;
            }

            hoja.Columns().AdjustToContents();

            var stream = new MemoryStream();
            libro.SaveAs(stream);
            stream.Position = 0;

            return Results.File(
                stream,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "documentos.xlsx");
        });

        return endpoints;
    }
}
