using CaeManager.Domain.EstadoMaterializado;

namespace CaeManager.Application.EstadoMaterializado;

/// <summary>
/// Lectura de las tablas del estado materializado del Tenant propietario de la conexión (la RLS forzada y el filtro global
/// de Tenant dejan ver solo las suyas). Es de solo lectura: quien escribe es el recálculo, por
/// <see cref="IEscritorEstadoMaterializado"/>.
/// </summary>
public interface IEstadoMaterializadoQueryContext
{
    IQueryable<EstadoEntidad> EstadosEntidad { get; }

    /// <summary>Una fila por Tenant ya relleno; ver <see cref="ILectorEstadoCentros.TenantCompletoAsync"/>.</summary>
    IQueryable<VersionEstadoTenant> VersionesEstadoTenant { get; }

    /// <summary>Recálculos diferidos aún sin consumir: mientras haya alguno, lo guardado puede estar viejo.</summary>
    IQueryable<RecalculoPendiente> RecalculosPendientes { get; }
}
