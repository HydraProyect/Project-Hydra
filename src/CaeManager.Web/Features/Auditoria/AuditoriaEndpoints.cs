using CaeManager.Application.Common;
using System.Text.Json;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Exportacion;
using ClosedXML.Excel;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Auditoria;

/// <summary>
/// Sirve el archivo que tenía un Documento justo antes de una renovación —
/// AuditoriaInterceptor ya guarda su ArchivoUrl en el JSON de "DatosAntes"
/// de cada Modificado, así que no hace falta ningún cambio de modelo, solo
/// leerlo. Solo Administrador, igual que la propia página de Auditoría.
/// </summary>
public static class AuditoriaEndpoints
{
    public static IEndpointRouteBuilder MapAuditoriaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // H2 (docs/ux-audit/14-administracion.md): "con los filtros aplicados"
        // — el único filtro de la pantalla es el tipo de entidad. Resuelve
        // nombre de usuario aquí (no en Application, que no conoce Identity),
        // mismo criterio que Auditoria.razor.cs.
        endpoints.MapGet("/auditoria/exportar.xlsx", async (
            string? entidad, IMediator mediator, UserManager<ApplicationUser> userManager, CancellationToken cancellationToken) =>
        {
            using var libro = new XLWorkbook();
            var hoja = libro.Worksheets.Add("Auditoría");

            hoja.Cell(1, 1).Value = "Fecha";
            hoja.Cell(1, 2).Value = "Entidad";
            hoja.Cell(1, 3).Value = "Acción";
            hoja.Cell(1, 4).Value = "Usuario";
            hoja.Row(1).Style.Font.Bold = true;

            // Pagina en lotes en vez de TamanoPagina: int.MaxValue (P2.7): no
            // materializa todo el histórico de auditoría del tenant de golpe.
            // La caché de nombres de usuario se sigue rellenando una sola vez
            // por UsuarioId (igual que antes con el .Distinct() previo), solo
            // que ahora perezosamente a medida que aparecen en cada lote.
            var usuariosPorId = new Dictionary<Guid, string>();
            var fila = 2;
            await foreach (var registro in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
                mediator.Send(
                    new ObtenerAuditoriaQuery(
                        EntidadTipo: string.IsNullOrWhiteSpace(entidad) ? null : entidad,
                        UsuarioId: null, Pagina: pagina, TamanoPagina: tamanoPagina),
                    cancellationToken)))
            {
                string nombreUsuario;
                if (registro.UsuarioId is null)
                {
                    nombreUsuario = "Sistema";
                }
                else if (!usuariosPorId.TryGetValue(registro.UsuarioId.Value, out nombreUsuario!))
                {
                    var usuario = await userManager.FindByIdAsync(registro.UsuarioId.Value.ToString());
                    nombreUsuario = usuario?.NombreCompleto ?? usuario?.Email ?? "(usuario eliminado)";
                    usuariosPorId[registro.UsuarioId.Value] = nombreUsuario;
                }

                hoja.Cell(fila, 1).Value = registro.FechaUtc.ToLocalTime();
                hoja.Cell(fila, 2).Value = registro.EntidadTipo;
                hoja.Cell(fila, 3).Value = registro.Accion;
                hoja.Cell(fila, 4).Value = nombreUsuario;
                fila++;
            }

            hoja.Columns().AdjustToContents();

            // Se escribe directo en el stream que consume la respuesta HTTP
            // (Results.File lo cierra) en vez de bufferear en un MemoryStream
            // y duplicarlo otra vez con ToArray().
            var stream = new MemoryStream();
            libro.SaveAs(stream);
            stream.Position = 0;

            return Results.File(
                stream,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "auditoria.xlsx");
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Administrador));

        endpoints.MapGet("/auditoria/{id:guid}/archivo-anterior", async (
            Guid id, HttpContext contexto, IMediator mediator, IFileStorageService almacenamiento,
            IRegistroAccesoDocumentoSensibleService registroAcceso, CancellationToken cancellationToken) =>
        {
            CabecerasArchivoSensible.ProhibirCache(contexto);

            var registro = await mediator.Send(new ObtenerRegistroAuditoriaPorIdQuery(id), cancellationToken);
            if (registro is null || registro.EntidadTipo != "Documento" || registro.DatosAntes is null)
                return Results.NotFound();

            string? archivoUrl = null;
            try
            {
                using var datosAntes = JsonDocument.Parse(registro.DatosAntes);
                if (datosAntes.RootElement.TryGetProperty("ArchivoUrl", out var valor) && valor.ValueKind == JsonValueKind.String)
                    archivoUrl = valor.GetString();
            }
            catch (JsonException)
            {
                // DatosAntes mal formado no debería ocurrir (lo escribe el propio
                // interceptor), pero un 404 es más seguro que propagar la excepción.
            }

            if (string.IsNullOrWhiteSpace(archivoUrl))
                return Results.NotFound();

            try
            {
                // DEC-36 (REC-099): se abre primero y se registra después de
                // confirmar que el archivo existe (mismo criterio que
                // DocumentosEndpoints) — registro.EntidadId es el DocumentoId
                // (el tipo ya se comprobó arriba): sigue siendo el mismo
                // Documento aunque el archivo servido sea una versión
                // anterior.
                var flujo = await almacenamiento.AbrirAsync(archivoUrl, cancellationToken);
                await registroAcceso.RegistrarSiSensibleAsync(registro.EntidadId, TipoAccesoDocumentoSensible.VersionAnterior, cancellationToken);
                return Results.File(flujo, "application/pdf", "documento-anterior.pdf", enableRangeProcessing: true);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
        }).RequireAuthorization(policy => policy.RequireRole(Roles.Administrador));

        return endpoints;
    }
}
