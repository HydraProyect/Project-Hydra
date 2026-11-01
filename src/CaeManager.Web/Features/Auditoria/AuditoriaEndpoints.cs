using CaeManager.Domain.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Web.Services;
using System.Text.Json;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Usuarios.Queries.ObtenerNombreCuenta;
using CaeManager.Domain.Auditoria;
using CaeManager.Web.Exportacion;
using ClosedXML.Excel;
using CaeManager.Infrastructure.Identity;
using MediatR;

namespace CaeManager.Web.Features.Auditoria;

/// <summary>
/// Sirve el archivo que tenía un Documento justo antes de una renovación —
/// AuditoriaInterceptor ya guarda su ArchivoUrl en el JSON de "DatosAntes"
/// de cada Modificado, así que no hace falta ningún cambio de modelo, solo
/// leerlo. Solo Administrador, igual que la propia página de Auditoría.
/// </summary>
public static class AuditoriaEndpoints
{
    public static async Task<IResult> ServirArchivoAnteriorAsync(Guid id, HttpContext contexto, IMediator mediator, IFileStorageService almacenamiento,
            IRegistroAccesoDocumentoSensibleService registroAcceso, ILoggerFactory fabricaRegistro, [Microsoft.AspNetCore.Mvc.FromServices] IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken)
    {
        CabecerasArchivoSensible.ProhibirCache(contexto);

        var registro = await mediator.Send(new ObtenerRegistroAuditoriaPorIdQuery(id), cancellationToken);
        if (registro is null || registro.EntidadTipo != "Documento" || registro.DatosAntes is null)
            return Results.NotFound();

        // Administrador puede tener el ámbito efectivo reducido por la lente de demo.
        // Reutiliza la misma lectura autorizada que el PDF vigente antes de abrir el histórico.
        // El acceso total conserva también la lectura de históricos de Documentos eliminados.
        // Mismo tratamiento del contexto no autorizado que DocumentosEndpoints.ServirArchivoAsync.
        if (!await alcanceDatos.TieneAccesoTotalAsync(cancellationToken))
        {
            try
            {
                if (await mediator.Send(new ObtenerDocumentoPorIdQuery(registro.EntidadId), cancellationToken) is null)
                    return Results.NotFound();
            }
            catch (Exception ex) when (ErroresLecturaSensible.EsContextoNoAutorizado(ex))
            {
                return ErroresLecturaSensible.Responder(contexto, ex, registro.EntidadId,
                    fabricaRegistro.CreateLogger(typeof(AuditoriaEndpoints).FullName!));
            }
        }

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

        // DEC-36 (REC-099): se abre primero y se registra después de
        // confirmar que el archivo existe (mismo criterio que
        // DocumentosEndpoints) — registro.EntidadId es el DocumentoId
        // (el tipo ya se comprobó arriba): sigue siendo el mismo
        // Documento aunque el archivo servido sea una versión
        // anterior. Solo la apertura se traduce a 404: una
        // FileNotFoundException del registro de lectura no es un archivo ausente.
        Stream flujo;
        try
        {
            flujo = await almacenamiento.AbrirAsync(archivoUrl, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return ErroresLecturaSensible.ArchivoAusente(registro.EntidadId,
                fabricaRegistro.CreateLogger(typeof(AuditoriaEndpoints).FullName!));
        }
        // Falla en cerrado: sin registro no sale el contenido, y el flujo se cierra.
        try
        {
            await registroAcceso.RegistrarSiSensibleAsync(registro.EntidadId, TipoAccesoDocumentoSensible.VersionAnterior, cancellationToken);
        }
        catch (Exception ex) when (ErroresLecturaSensible.EsMapeable(ex))
        {
            await flujo.DisposeAsync();
            return ErroresLecturaSensible.Responder(contexto, ex, registro.EntidadId,
                fabricaRegistro.CreateLogger(typeof(AuditoriaEndpoints).FullName!));
        }
        catch
        {
            await flujo.DisposeAsync();
            throw;
        }
        return Results.File(flujo, "application/pdf", "documento-anterior.pdf", enableRangeProcessing: true);
    }

    /// <summary>
    /// El filtro es texto libre y el rastro no admite datos personales: solo se nombra un tipo
    /// de entidad del dominio. Cualquier otro valor consta como «otro», porque sí filtró.
    /// </summary>
    private static string? EntidadParaElRastro(string? entidad) =>
        string.IsNullOrWhiteSpace(entidad) ? null : EntidadesDelDominio.Contains(entidad) ? entidad : "otro";

    private static readonly HashSet<string> EntidadesDelDominio =
        typeof(RegistroAuditoria).Assembly.GetExportedTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    public static async Task<IResult> ExportarAsync(
        string? entidad, string? desde, string? hasta, IMediator mediator, [Microsoft.AspNetCore.Mvc.FromServices] IRegistroExportacionService registroExportacion, CancellationToken cancellationToken)
    {
        // Sin «desde»/«hasta» exporta todo el historial (contrato de siempre); la pantalla de
        // Auditoría los envía siempre con el rango que muestra, para que el Excel sea lo que se ve.
        var fechaDesde = RangoFechasAuditoria.Interpretar(desde);
        var fechaHasta = RangoFechasAuditoria.Interpretar(hasta);

        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Auditoría");

        // La hoja sale de la aplicación y una celda de Excel no lleva zona: se
        // rotula para que nadie la lea como UTC ni como la hora de su equipo.
        hoja.Cell(1, 1).Value = "Fecha (hora peninsular)";
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
                    UsuarioId: null, Pagina: pagina, TamanoPagina: tamanoPagina,
                    DesdeUtc: RangoFechasAuditoria.DesdeUtc(fechaDesde),
                    HastaUtc: RangoFechasAuditoria.HastaUtcExclusivo(fechaHasta)),
                cancellationToken)))
        {
            string nombreUsuario;
            if (registro.UsuarioId is null)
            {
                nombreUsuario = "Sistema";
            }
            else if (!usuariosPorId.TryGetValue(registro.UsuarioId.Value, out nombreUsuario!))
            {
                var usuario = await mediator.Send(new ObtenerNombreCuentaQuery(registro.UsuarioId.Value), cancellationToken);
                nombreUsuario = usuario?.NombreCompleto ?? usuario?.Email ?? "(usuario eliminado)";
                usuariosPorId[registro.UsuarioId.Value] = nombreUsuario;
            }

            hoja.Cell(fila, 1).Value = registro.FechaUtc.EnHoraPeninsular();
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

        try
        {
            await registroExportacion.RegistrarAsync(
                nameof(RegistroAuditoria), fila - 2,
                CriteriosExportacion.Desde(("listado", "auditoria"), ("entidad", EntidadParaElRastro(entidad)),
                    ("desde", fechaDesde?.ToString(RangoFechasAuditoria.FormatoFecha)),
                    ("hasta", fechaHasta?.ToString(RangoFechasAuditoria.FormatoFecha))),
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
            "auditoria.xlsx");
    }

    public static IEndpointRouteBuilder MapAuditoriaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // H2 (Project-Hydra-Negocio/tecnico/docs/ux-audit/14-administracion.md): "con los filtros aplicados"
        // — los filtros de la pantalla son el tipo de entidad y el rango de fechas (el enlace los lleva). Resuelve
        // nombre de usuario con el mismo criterio que Auditoria.razor.cs.
        endpoints.MapGet("/auditoria/exportar.xlsx", ExportarAsync).RequireAuthorization(policy => policy.RequireRole(Roles.Administrador))
        .ExcluidoDelEncargoDeAdministracion();

        endpoints.MapGet("/auditoria/{id:guid}/archivo-anterior", ServirArchivoAnteriorAsync).RequireAuthorization(policy => policy.RequireRole(Roles.Administrador))
        .ExcluidoDelEncargoDeAdministracion();

        return endpoints;
    }
}
