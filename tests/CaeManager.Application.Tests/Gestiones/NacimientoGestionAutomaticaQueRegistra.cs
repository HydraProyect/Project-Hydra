using CaeManager.Application.Gestiones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;

namespace CaeManager.Application.Tests.Gestiones;

/// <summary>
/// Doble de <see cref="INacimientoGestionAutomaticaService"/> para los Commands cuya prueba no mira la Gestión
/// automática: no agrega nada. Que el acto la pida lo prueban los tests que usan el servicio real
/// (<see cref="GestionAutomaticaEnLosActosTests"/> y el de plantilla en <c>GenerarDocumentoIndividualCommandHandlerTests</c>).
/// </summary>
public sealed class NacimientoGestionAutomaticaQueRegistra : INacimientoGestionAutomaticaService
{
    public Task<GestionAutomatica?> AgregarAsync(Documento nacido, Documento? sustituido, CancellationToken cancellationToken = default) =>
        Task.FromResult<GestionAutomatica?>(null);
}
