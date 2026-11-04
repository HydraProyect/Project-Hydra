using CaeManager.Application.Common;
using CaeManager.Domain.Importacion;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Importacion.Queries.ObtenerHistorialImportaciones;

/// <summary>Últimas importaciones ejecutadas, para el sidebar del wizard (Configuración → Importar datos).</summary>
public record ObtenerHistorialImportacionesQuery(int Limite = 10) : IRequest<IReadOnlyList<HistorialImportacionDto>>, Common.ILecturaRegistradaEnSesionPrivilegiada;

public record HistorialImportacionDto(
    Guid Id, string Plantilla, string NombreArchivo, DateTime EjecutadaEnUtc, Guid EjecutadaPorUsuarioId,
    bool Exitosa, int TotalCreados, int TotalAdvertencias, int TotalOmitidos, string? MensajeError);

/// <summary>
/// El historial se acota con la misma autoridad que las cargas masivas (<see cref="AutoridadCargaMasiva"/>), porque cada
/// fila es un agregado de lo que tocó una carga (nombre del archivo, totales, mensaje de error) sin decir sobre qué
/// Clientes empresariales, Empresas o Trabajadores, y por tanto no se puede recortar por las listas de la Asignación de
/// Cartera:
/// <list type="bullet">
/// <item>Acto de Administrador (<see cref="AutoridadCargaMasiva.Total"/>): el historial del Tenant entero.</item>
/// <item>Autoridad acotada (Coordinador CAE o Dirección CAE) que alcanza el Tenant entero
/// (<see cref="IAlcanceDatosService.AlcanzaElTenantEnteroAsync"/>, la condición para enseñar un agregado de Tenant): también
/// el historial entero, porque sus cargas no recortan nada.</item>
/// <item>Autoridad acotada con una lista de cartera: solo las cargas que ejecutó él mismo, que solo escribieron dentro
/// de su ámbito. Las de otros usuarios (un Administrador, otro Coordinador CAE) delatarían archivos y volúmenes de carga
/// de Clientes empresariales que no son de su cartera.</item>
/// <item>Sin autoridad de carga masiva, o sin usuario identificado en la autoridad acotada: nada (fallo cerrado).</item>
/// </list>
/// El aislamiento entre Tenants lo sigue garantizando RLS; esto solo recorta dentro del Tenant actual.
/// </summary>
public class ObtenerHistorialImportacionesQueryHandler(
    IImportacionQueryContext importacionContext, IAutorizacionEscrituraEfectiva autorizacionEscrituraEfectiva,
    IAlcanceDatosService alcanceDatos, ICurrentUserService usuarioActual)
    : IRequestHandler<ObtenerHistorialImportacionesQuery, IReadOnlyList<HistorialImportacionDto>>
{
    public async Task<IReadOnlyList<HistorialImportacionDto>> Handle(
        ObtenerHistorialImportacionesQuery request, CancellationToken cancellationToken)
    {
        var historial = await AcotarAlAmbitoAsync(importacionContext.HistorialImportaciones, cancellationToken);
        if (historial is null)
            return [];

        return await historial
            .OrderByDescending(h => h.EjecutadaEnUtc)
            .Take(request.Limite)
            .Select(h => new HistorialImportacionDto(
                h.Id, h.Plantilla, h.NombreArchivo, h.EjecutadaEnUtc, h.EjecutadaPorUsuarioId,
                h.Exitosa, h.TotalCreados, h.TotalAdvertencias, h.TotalOmitidos, h.MensajeError))
            .ToListAsync(cancellationToken);
    }

    private async Task<IQueryable<HistorialImportacion>?> AcotarAlAmbitoAsync(
        IQueryable<HistorialImportacion> historial, CancellationToken cancellationToken)
    {
        var autoridad = await autorizacionEscrituraEfectiva.ObtenerAutoridadCargaMasivaAsync(cancellationToken);
        if (autoridad == AutoridadCargaMasiva.Total)
            return historial;

        if (autoridad != AutoridadCargaMasiva.Acotada)
            return null;

        if (await alcanceDatos.AlcanzaElTenantEnteroAsync(cancellationToken))
            return historial;

        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is not { } id || id == Guid.Empty)
            return null;

        return historial.Where(h => h.EjecutadaPorUsuarioId == id);
    }
}
