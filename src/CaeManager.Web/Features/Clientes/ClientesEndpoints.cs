using CaeManager.Web.Components.DesignSystem;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Services;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Clientes;

/// <summary>
/// Exportación a Excel: patrón de referencia que el resto de módulos con
/// tabla replican (ver Project-Hydra-Negocio/tecnico/ROADMAP.md, Fase 1). Se sirve como endpoint aparte en
/// vez de generarse dentro del circuito de Blazor porque descargar un
/// archivo binario grande no encaja bien con SignalR.
/// </summary>
public static class ClientesEndpoints
{
    public static IEndpointRouteBuilder MapClientesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/clientes/exportar.xlsx", ExportarAsync)
        // DEC-1 (plan de sesiones nocturnas 2026-09-02): mismos roles que
        // /clientes (RolesComunicaciones.Gestion, no RolesDeCartera de
        // CatalogoMenuLateral — esa gatea "Visión de cartera", un dashboard distinto).
        // Sin esto, restringir la página dejaba este endpoint como vía de
        // bypass para descargar la misma lista completa de clientes con
        // cualquier rol autenticado. Mismo patrón que /auditoria/exportar.xlsx.
        // ObtenerClientesQuery ya aplica IAlcanceDatosService, así que un
        // GestorCae exporta su propia cartera, no la de todos.
        .RequireAuthorization(policy => policy.RequireRole(
            CaeManager.Infrastructure.Identity.Roles.Administrador,
            CaeManager.Infrastructure.Identity.Roles.DireccionCae,
            CaeManager.Infrastructure.Identity.Roles.CoordinadorCae,
            CaeManager.Infrastructure.Identity.Roles.GestorCae,
            CaeManager.Infrastructure.Identity.Roles.Consulta));

        // Solo se enlazan desde /importacion (Importacion.razor: Roles = ConCargaMasivaCsv: Administrador, Dirección CAE y Coordinador CAE)
        // — mismo motivo que el RequireAuthorization de /clientes/exportar.xlsx más
        // arriba, aunque aquí el archivo es una plantilla en blanco sin datos de
        // tenant, no un export con contenido real.
        endpoints.MapGet("/clientes/plantilla.xlsx", (IPlantillaClientesService servicio) =>
            Results.File(
                servicio.GenerarPlantilla(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "plantilla-clientes.xlsx"))
        .RequireAuthorization(policy => policy.RequireRole(CaeManager.Infrastructure.Identity.Roles.Administrador, CaeManager.Infrastructure.Identity.Roles.DireccionCae, CaeManager.Infrastructure.Identity.Roles.CoordinadorCae))
        .ExcluidoDelEncargoDeAdministracion();

        endpoints.MapGet("/clientes/plantilla-combinada.xlsx", (IPlantillaCombinadaService servicio) =>
            Results.File(
                servicio.GenerarPlantilla(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "plantilla-combinada.xlsx"))
        .RequireAuthorization(policy => policy.RequireRole(CaeManager.Infrastructure.Identity.Roles.Administrador, CaeManager.Infrastructure.Identity.Roles.DireccionCae, CaeManager.Infrastructure.Identity.Roles.CoordinadorCae))
        .ExcluidoDelEncargoDeAdministracion();

        return endpoints;
    }

    public static async Task<IResult> ExportarAsync(
        IMediator mediator, ITenantActual tenantActual, IRegistroExportacionService registroExportacion, CancellationToken cancellationToken,
        string? q = null, bool critico = false, string? ejecutivo = null, string? estado = null,
        string? orden = null, bool desc = false)
    {
        // Estado 4a, igual que /trabajadores/exportar.xlsx: Clientes.razor no muestra los Clientes del
        // Tenant de origen a quien tiene que elegir una empresa de su cartera
        // (ContextoEmpresaActiva.ResolverAsync); el endpoint tampoco los consulta ni genera el archivo.
        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        if (ClientesAutorizados.PideElegirEmpresa(autorizados, tenantActual.TenantId))
            return Results.Redirect("/clientes");

        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Clientes");

        hoja.Cell(1, 1).Value = "Razón social";
        hoja.Cell(1, 2).Value = "CIF";
        hoja.Cell(1, 3).Value = "Crítico";
        hoja.Cell(1, 4).Value = "Creado";
        hoja.Row(1).Style.Font.Bold = true;

        // Pagina en lotes en vez de TamanoPagina: int.MaxValue (P2.7):
        // no materializa toda la tabla de clientes del tenant de golpe.
        var fila = 2;
        await foreach (var cliente in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
            mediator.Send(
                new ObtenerClientesQuery(
                    // Posicionales, en el orden del record: búsqueda, solo críticos y
                    // Gestor CAE de referencia. El estado es la selección de la franja
                    // (varios nombres separados por coma) y va en EstadosDocumentales.
                    string.IsNullOrWhiteSpace(q) ? null : q,
                    critico ? true : null,
                    Guid.TryParse(ejecutivo, out var ejecutivoId) ? ejecutivoId : null,
                    EstadoDocumental: null,
                    Pagina: pagina,
                    EstadosDocumentales: SeleccionEstados.Separar<EstadoDocumento>(estado) is { Count: > 0 } estados ? estados : null,
                    TamanoPagina: tamanoPagina,
                    OrdenarPor: string.IsNullOrWhiteSpace(orden) ? null : orden,
                    Descendente: desc),
                cancellationToken)))
        {
            hoja.Cell(fila, 1).Value = cliente.RazonSocial;
            hoja.Cell(fila, 2).Value = cliente.Cif;
            hoja.Cell(fila, 3).Value = cliente.EsCritico ? "Sí" : "No";
            hoja.Cell(fila, 4).Value = cliente.CreadoEnUtc;
            fila++;
        }

        hoja.Columns().AdjustToContents();

        // Se escribe directo en el stream que consume la respuesta HTTP
        // (Results.File lo cierra) en vez de bufferear en un MemoryStream
        // y duplicarlo otra vez con ToArray().
        var stream = new MemoryStream();
        libro.SaveAs(stream);
        stream.Position = 0;

        try
        {
            await registroExportacion.RegistrarAsync(
                nameof(CaeManager.Domain.Empresas.Empresa), fila - 2,
                CriteriosExportacion.Desde(
                    ("listado", "clientes"),
                    ("busqueda", string.IsNullOrWhiteSpace(q) ? null : "true"),
                    ("critico", critico ? "true" : null),
                    ("ejecutivo", Guid.TryParse(ejecutivo, out var ejecutivoAplicado) ? ejecutivoAplicado.ToString() : null),
                    ("estado", SeleccionEstados.Separar<EstadoDocumento>(estado) is { Count: > 0 } estadosAplicados ? string.Join(",", estadosAplicados) : null),
                    ("orden", CriteriosExportacion.SoloNombre(orden)),
                    ("desc", desc ? "true" : null)),
                cancellationToken);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }

        return Results.File(
            stream,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "clientes.xlsx");
    }
}
