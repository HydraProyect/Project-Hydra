using CaeManager.Domain.EstadoMaterializado;

namespace CaeManager.Application.EstadoMaterializado;

/// <summary>
/// Lectura de las tablas del estado materializado (diseño de 2026-10-24): filtradas por el Tenant propietario de la
/// conexión (filtro global y RLS forzada). Solo las leen los lectores de pantalla a través de
/// <see cref="ILectorEstadoMaterializado"/>; las escribe únicamente el recálculo.
/// </summary>
public interface IEstadoMaterializadoQueryContext
{
    IQueryable<EstadoEntidad> EstadosEntidad { get; }
    IQueryable<EstadoRequisito> EstadosRequisito { get; }
    IQueryable<VersionEstadoTenant> VersionesEstadoTenant { get; }
}
