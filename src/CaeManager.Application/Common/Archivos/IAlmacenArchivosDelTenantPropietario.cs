namespace CaeManager.Application.Common.Archivos;

/// <summary>
/// Apertura de un archivo del almacenamiento con la carpeta y la clave de un Tenant propietario explícito, no con las del
/// Tenant activo. Es lo que el almacén del Núcleo ofrece para que el módulo Inbound abra, desde la petición del Tenant del
/// Cliente empresarial, los originales del Tenant de la Empresa contratista sin que el Núcleo conozca el puerto ni el DTO
/// de Inbound (ADR-013, K-3): el adaptador <c>AdaptadorAlmacenOriginalesInbound</c> aplica antes la regla de Inbound y
/// llama aquí.
///
/// <para>
/// <see cref="IFileStorageService"/> solo abre lo del Tenant activo; esta interfaz cruza a propósito el aislamiento del
/// almacenamiento por Tenant. Por eso no se añade a <see cref="IFileStorageService"/>, que inyectan decenas de tipos, y
/// su único consumidor es ese adaptador (<c>AlmacenDeDiscoSoloPorSusPuertosTests</c>): cualquier otro podría abrir
/// archivos de cualquier Tenant con solo conocer su clave. Por lo mismo, la implementación no es la instancia que el
/// contenedor entrega como <see cref="IFileStorageService"/>: un consumidor del almacenamiento del Tenant activo no llega
/// aquí con un cast, ni con <c>dynamic</c> fuera de Infrastructure; dentro, lo vigila el mismo test.
/// </para>
/// </summary>
public interface IAlmacenArchivosDelTenantPropietario
{
    /// <summary>
    /// El contenido descifrado con la clave derivada de <paramref name="tenantPropietarioId"/>, con el mismo presupuesto de
    /// memoria y el mismo descifrado (formato v2, legado y alertas) que <see cref="IFileStorageService.AbrirAsync"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// El Tenant está vacío, la clave no es de su carpeta (<see cref="ClavesDeAlmacen.EsDeLaCarpetaDelTenant"/>) o el
    /// archivo no existe: el mismo «no existe» que <see cref="IFileStorageService.AbrirAsync"/> para otro Tenant.
    /// </exception>
    /// <exception cref="PresupuestoMemoriaAgotadoException">Sin hueco en el presupuesto de memoria: transitorio.</exception>
    Task<Stream> AbrirDelTenantPropietarioAsync(
        string identificador, Guid tenantPropietarioId, CancellationToken cancellationToken = default);
}
