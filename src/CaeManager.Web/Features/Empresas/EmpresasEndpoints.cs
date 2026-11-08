using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Empresas.Queries.PaqueteDocumentalEmpresa;
using CaeManager.Domain.Common;
using CaeManager.Web.Services;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Documentos;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Empresas;

/// <summary>
/// Mismo patrón que ClientesEndpoints.cs (Project-Hydra-Negocio/tecnico/docs/ux-audit/03-empresas-subcontratas.md H7).
/// </summary>
public static class EmpresasEndpoints
{
    public static IEndpointRouteBuilder MapEmpresasEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/empresas/exportar.xlsx", ExportarAsync);
        endpoints.MapGet("/empresas/{id:guid}/paquete-documental.zip", ServirPaqueteAsync);

        return endpoints;
    }

    /// <summary>
    /// Descarga del «Preparar paquete» de Empresa 360, por el mismo mecanismo que el zip de una Visita
    /// (<c>VisitasEndpoints.ServirPaqueteAsync</c>): endpoint autenticado (FallbackPolicy global), sin caché y nunca un
    /// archivo estático. Autorización, selección y registro de accesos sensibles (DEC-36) viven en
    /// <see cref="DescargarPaqueteEmpresaQuery"/>. <paramref name="cliente"/> vacío es «Todo lo vigente».
    /// </summary>
    public static async Task<IResult> ServirPaqueteAsync(
        Guid id, HttpContext contexto, IMediator mediator, ILoggerFactory fabricaRegistro, CancellationToken cancellationToken, string? cliente = null)
    {
        Guid? clienteEmpresarialId = null;
        if (!string.IsNullOrWhiteSpace(cliente))
        {
            if (!Guid.TryParse(cliente, out var destinoId))
                return Results.BadRequest();
            clienteEmpresarialId = destinoId;
        }

        Result<PaqueteEmpresaDescargaDto> resultado;
        try
        {
            resultado = await mediator.Send(new DescargarPaqueteEmpresaQuery(id, clienteEmpresarialId), cancellationToken);
        }
        catch (Exception ex) when (ErroresLecturaSensible.EsMapeable(ex))
        {
            return ErroresLecturaSensible.Responder(contexto, ex, id, fabricaRegistro.CreateLogger(typeof(EmpresasEndpoints).FullName!));
        }

        if (resultado.EsFallido)
        {
            // Fuera de alcance o de otro Tenant responde igual que inexistente; un rol que no descarga o una Sesión
            // Privilegiada, 403; el resto son estados del paquete que el Gestor CAE puede entender. El diálogo descarga por
            // fetch y enseña este texto en pantalla.
            if (resultado.Error.Equals(AutorizacionPaqueteEmpresa.NoEncontrada))
                return Results.NotFound();
            var prohibido = resultado.Error.Equals(AutorizacionPaqueteEmpresa.RolSinDescarga)
                || resultado.Error.Equals(AutorizacionPaqueteEmpresa.SesionPrivilegiadaSinDescarga);
            return Results.Text(resultado.Error.Mensaje, "text/plain; charset=utf-8",
                statusCode: prohibido ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict);
        }

        CabecerasArchivoSensible.ProhibirCache(contexto);
        return Results.File(resultado.Valor.Contenido, "application/zip", resultado.Valor.NombreArchivo);
    }

    public static async Task<IResult> ExportarAsync(
        IMediator mediator, ITenantActual tenantActual, IRegistroExportacionService registroExportacion, CancellationToken cancellationToken,
        string? q = null, string? estado = null, string? cliente = null)
    {
        // Estado 4a, igual que /trabajadores/exportar.xlsx: Empresas.razor no muestra las Empresas del
        // Tenant de origen a quien tiene que elegir una empresa de su cartera
        // (ContextoEmpresaActiva.ResolverAsync); el endpoint tampoco las consulta ni genera el archivo.
        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        if (ClientesAutorizados.PideElegirEmpresa(autorizados, tenantActual.TenantId))
            return Results.Redirect("/empresas");

        // Un Cliente que no es un Id es una petición mal formada: exportar TODO por ignorarlo engañaría a quien exporta la vista filtrada.
        Guid? clienteEmpresarialId = null;
        if (!string.IsNullOrWhiteSpace(cliente))
        {
            if (!Guid.TryParse(cliente, out var id))
                return Results.BadRequest();
            clienteEmpresarialId = id;
        }

        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Empresas");

        hoja.Cell(1, 1).Value = "Razón social";
        hoja.Cell(1, 2).Value = "CIF";
        hoja.Cell(1, 3).Value = "Estado documental";
        hoja.Cell(1, 4).Value = "% cumplimiento";
        hoja.Cell(1, 5).Value = "Creado";
        hoja.Row(1).Style.Font.Bold = true;
        hoja.Column(4).Style.NumberFormat.Format = "0%";

        // Pagina en lotes en vez de TamanoPagina: int.MaxValue (P2.7):
        // no materializa toda la tabla de empresas del tenant de golpe.
        var fila = 2;
        await foreach (var empresa in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
            mediator.Send(
                new ObtenerEmpresasQuery(
                    Busqueda: string.IsNullOrWhiteSpace(q) ? null : q,
                    Pagina: pagina,
                    TamanoPagina: tamanoPagina,
                    EstadoDocumental: string.IsNullOrWhiteSpace(estado) ? null : estado,
                    // El mismo filtro «Cliente» de la pantalla, con su mismo recorte por alcance.
                    ClienteEmpresarialId: clienteEmpresarialId),
                cancellationToken)))
        {
            hoja.Cell(fila, 1).Value = empresa.RazonSocial;
            hoja.Cell(fila, 2).Value = empresa.Cif;
            hoja.Cell(fila, 3).Value = EstadoDocumentoUi.TextoDocumental(empresa.EstadoDocumental);
            if (empresa.CumplimientoPorcentaje is not null)
                hoja.Cell(fila, 4).Value = empresa.CumplimientoPorcentaje.Value / 100.0;
            hoja.Cell(fila, 5).Value = empresa.CreadoEnUtc;
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
                    ("listado", "empresas"),
                    ("busqueda", string.IsNullOrWhiteSpace(q) ? null : "true"),
                    ("estado", CriteriosExportacion.EstadosDocumentalesAplicados(estado)),
                    ("cliente", clienteEmpresarialId?.ToString())),
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
            "empresas.xlsx");
    }
}
