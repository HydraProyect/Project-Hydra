using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Features.Documentos;
using ClosedXML.Excel;
using MediatR;

namespace CaeManager.Web.Features.Trabajadores;

/// <summary>
/// Fila de la exportación de Trabajadores: las mismas columnas de datos que
/// pinta el listado, DNI incluido. Decisión del propietario del 2026-10-08 (D2
/// de la auditoría de capacidades de los listados): «exportar debe incluir todos
/// los datos ya que quien tiene acceso a esa pantalla para exportar tiene acceso
/// ya a la información». Sustituye a la decisión S4 del 2026-09-24, que retiró
/// el DNI porque la salida masiva no dejaba rastro: ahora cada descarga queda
/// en la auditoría (<see cref="IRegistroExportacionService"/>).
/// </summary>
public sealed record FilaExportacionTrabajador(
    string Apellidos, string Nombre, string? Dni, string EmpleadorNombre, EstadoDocumento? EstadoDocumental)
{
    public static FilaExportacionTrabajador Desde(TrabajadorListaDto trabajador) =>
        new(trabajador.Apellidos, trabajador.Nombre, trabajador.Dni, trabajador.EmpleadorNombre, trabajador.EstadoDocumental);
}

/// <summary>Mismo patrón de referencia que ClientesEndpoints — ver comentario allí.</summary>
public static class TrabajadoresEndpoints
{
    public static IEndpointRouteBuilder MapTrabajadoresEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/trabajadores/exportar.xlsx", ExportarAsync);

        return endpoints;
    }

    /// <summary>
    /// Público para que el test mida la decisión del estado 4a sin levantar la aplicación.
    /// </summary>
    /// <remarks>
    /// Los parámetros de consulta son los de «Exportar esta vista»: los mismos
    /// criterios que <c>Trabajadores.razor.cs</c> pasa a
    /// <see cref="ObtenerTrabajadoresQuery"/>, leídos igual que allí. Sin
    /// ninguno, es «Exportar todo». Las dos variantes pasan por esa misma
    /// consulta: su autorización, su alcance y su Tenant son los del listado.
    /// </remarks>
    public static async Task<IResult> ExportarAsync(
        IMediator mediator, ITenantActual tenantActual, IRegistroExportacionService registroExportacion,
        CancellationToken cancellationToken,
        string? q = null, string? estado = null, string? empresa = null, string? subcontrata = null,
        string? orden = null, bool desc = false)
    {
        // Estado 4a: la página no muestra los datos del Tenant de origen a quien tiene que
        // elegir una empresa de su cartera; el endpoint no los exporta tampoco. Misma
        // condición que la página (ContextoEmpresaActiva.ResolverAsync compone las mismas dos
        // condiciones que ClientesAutorizados.PideElegirEmpresa, que es la que usa el endpoint).
        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        if (ClientesAutorizados.PideElegirEmpresa(autorizados, tenantActual.TenantId))
            return Results.Redirect("/trabajadores");

        var trabajadores = PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
            mediator.Send(
                new ObtenerTrabajadoresQuery(
                    Busqueda: string.IsNullOrWhiteSpace(q) ? null : q,
                    EmpresaId: Guid.TryParse(empresa, out var empresaId) ? empresaId : null,
                    SubcontrataId: Guid.TryParse(subcontrata, out var subcontrataId) ? subcontrataId : null,
                    Pagina: pagina,
                    TamanoPagina: tamanoPagina,
                    OrdenarPor: string.IsNullOrWhiteSpace(orden) ? null : orden,
                    Descendente: desc,
                    EstadoDocumental: string.IsNullOrWhiteSpace(estado) ? null : estado),
                cancellationToken));

        var filas = 0;
        var stream = await GenerarLibroAsync(ContarAsync(trabajadores), cancellationToken);

        // El libro lleva DNI: la descarga deja rastro ANTES de entregarse. Si el
        // rastro no se puede guardar, la excepción sube y el fichero no sale.
        // Al rastro va el criterio APLICADO, nunca el texto de la petición, que es
        // libre y puede ser un DNI o un nombre: de la búsqueda solo consta que la
        // hubo; de los filtros de Empresa y Subcontrata, el Id ya leído (uno que no
        // se pudo leer no filtró nada, así que no consta); el estado y el orden,
        // solo si tienen forma de nombre.
        await registroExportacion.RegistrarAsync(
            nameof(Domain.Trabajadores.Trabajador), filas,
            CriteriosExportacion.Desde(
                ("busqueda", string.IsNullOrWhiteSpace(q) ? null : "true"),
                ("estado", CriteriosExportacion.SoloNombres(estado)),
                ("empresa", Guid.TryParse(empresa, out var empresaAplicada) ? empresaAplicada.ToString() : null),
                ("subcontrata", Guid.TryParse(subcontrata, out var subcontrataAplicada) ? subcontrataAplicada.ToString() : null),
                ("orden", CriteriosExportacion.SoloNombre(orden)), ("desc", desc ? "true" : null)),
            cancellationToken);

        return Results.File(
            stream,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "trabajadores.xlsx");

        async IAsyncEnumerable<TrabajadorListaDto> ContarAsync(IAsyncEnumerable<TrabajadorListaDto> origen)
        {
            await foreach (var trabajador in origen.WithCancellation(cancellationToken))
            {
                filas++;
                yield return trabajador;
            }
        }
    }

    /// <summary>
    /// Escribe el libro a partir de lo que devuelve la query del listado,
    /// pasando cada Trabajador por <see cref="FilaExportacionTrabajador"/>
    /// antes de tocar la hoja. Público para que el test mida el xlsx real sin
    /// levantar la aplicación (el proyecto no expone WebApplicationFactory).
    /// </summary>
    public static async Task<MemoryStream> GenerarLibroAsync(
        IAsyncEnumerable<TrabajadorListaDto> trabajadores, CancellationToken cancellationToken = default)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Trabajadores");

        hoja.Cell(1, 1).Value = "Apellidos";
        hoja.Cell(1, 2).Value = "Nombre";
        hoja.Cell(1, 3).Value = "DNI";
        hoja.Cell(1, 4).Value = "Empresa/Subcontrata";
        hoja.Cell(1, 5).Value = "Documentación";
        hoja.Row(1).Style.Font.Bold = true;

        var fila = 2;
        await foreach (var trabajador in trabajadores.WithCancellation(cancellationToken))
        {
            var exportada = FilaExportacionTrabajador.Desde(trabajador);
            hoja.Cell(fila, 1).Value = exportada.Apellidos;
            hoja.Cell(fila, 2).Value = exportada.Nombre;
            // Texto, no número: un DNI con ceros a la izquierda no puede perderlos.
            hoja.Cell(fila, 3).SetValue(exportada.Dni ?? string.Empty);
            hoja.Cell(fila, 4).Value = exportada.EmpleadorNombre;
            hoja.Cell(fila, 5).Value = EstadoDocumentoUi.TextoDocumental(exportada.EstadoDocumental);
            fila++;
        }

        hoja.Columns().AdjustToContents();

        var stream = new MemoryStream();
        libro.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}
