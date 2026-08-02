using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Resuelve el alcance de cartera de un <see cref="Documento"/> ya cargado —
/// mismo criterio de ámbito mutuamente excluyente que
/// <c>ObtenerDocumentoPorIdQuery</c> (Trabajador/Cliente/Vehículo/Proyecto/Empresa).
/// Documento de Trabajador es el caso más sensible: incluye archivos de
/// vigilancia de la salud (categoría especial Art. 9 RGPD).
/// </summary>
public static class DocumentoAlcanceExtensions
{
    public static async Task<bool> DocumentoVisibleAsync(
        this IAlcanceDatosService alcance, Documento documento, IProyectosQueryContext proyectosContext, CancellationToken cancellationToken = default)
    {
        if (documento.TrabajadorId is { } trabajadorId)
            return await alcance.TrabajadorVisibleAsync(trabajadorId, cancellationToken);

        if (documento.ClienteId is { } clienteId)
            return await alcance.ClienteVisibleAsync(clienteId, cancellationToken);

        if (documento.VehiculoId is { } vehiculoId)
            return await alcance.VehiculoVisibleAsync(vehiculoId, cancellationToken);

        if (documento.ProyectoId is { } proyectoId)
        {
            var clienteIdDeProyecto = await proyectosContext.Proyectos
                .Where(p => p.Id == proyectoId)
                .Select(p => (Guid?)p.ClienteId)
                .FirstOrDefaultAsync(cancellationToken);

            return clienteIdDeProyecto is { } id && await alcance.ClienteVisibleAsync(id, cancellationToken);
        }

        return await alcance.EmpresaVisibleAsync(documento.EmpresaId!.Value, cancellationToken);
    }
}
