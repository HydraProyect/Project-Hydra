using System.Data.Common;
using CaeManager.Application.Auditoria;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CaeManager.Web.Services;

public static class ErroresLecturaSensible
{
    public static IResult ArchivoAusente(Guid recursoId, ILogger registro)
    {
        registro.LogWarning(
            "El recurso {RecursoId} tiene adjunto registrado pero su fichero no está en el almacén; se responde 404.",
            recursoId);
        return Results.NotFound();
    }

    public static bool EsContextoNoAutorizado(Exception excepcion) =>
        excepcion is AccesoDocumentoSensibleNoRegistradoException
            or DbException { SqlState: "42501" }
            or DbUpdateException { InnerException: DbException { SqlState: "42501" } };

    public static bool EsMapeable(Exception excepcion) =>
        EsContextoNoAutorizado(excepcion) || EsFalloTransitorio(excepcion);

    private static bool EsFalloTransitorio(Exception excepcion) =>
        excepcion is TimeoutException or DbException { IsTransient: true }
        || (excepcion is DbUpdateException or InvalidOperationException or RetryLimitExceededException
            && excepcion.InnerException is { } causa && EsFalloTransitorio(causa));

    public static IResult Responder(HttpContext contexto, Exception excepcion, Guid recursoId, ILogger registro)
    {
        var codigo = EsContextoNoAutorizado(excepcion)
            ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable;
        registro.Log(codigo == StatusCodes.Status403Forbidden ? LogLevel.Warning : LogLevel.Error,
            excepcion, "La lectura sensible del recurso {RecursoId} falló antes de servir el contenido (registro o consulta previa); se responde {Codigo}.",
            recursoId, codigo);
        // Evita que la página de estado sustituya el cuerpo vacío o vuelva a consultar la base.
        var paginasDeEstado = contexto.Features.Get<IStatusCodePagesFeature>();
        if (paginasDeEstado is not null) paginasDeEstado.Enabled = false;
        return Results.StatusCode(codigo);
    }
}
