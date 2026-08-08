namespace CaeManager.Application.Common;

/// <summary>
/// Emails de los usuarios de portal (rol Cliente, vinculados vía
/// ApplicationUser.ClienteId) de un Cliente — destinatarios de
/// ReclamacionDocumental. Abstracción por el mismo motivo que
/// IDirectorioUsuariosService: ApplicationUser vive en
/// Infrastructure.Identity, fuera del alcance de Application.
/// </summary>
public interface IContactosClienteService
{
    Task<IReadOnlyList<string>> ObtenerEmailsPortalAsync(Guid clienteId, CancellationToken cancellationToken = default);
}
