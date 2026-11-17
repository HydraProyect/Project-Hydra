using CaeManager.Application.Operaciones;
using CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// Doble del aviso de cartera sin Gestor CAE que anota lo que se le pidió: estos tests miden la
/// cartera en PostgreSQL, no la campana (el aviso tiene sus tests en Application).
/// </summary>
internal sealed class AvisoSinGestorCaeAnotado : IAvisoDeCarteraSinGestorCae
{
    public List<(Guid OperadorTenantId, string NombreDesactivado, IReadOnlyList<TenantEnCarteraDeGestor> SinGestorCae)> Avisos { get; } = [];

    public Task AvisarAsync(
        Guid operadorTenantId, string nombreDesactivado, IReadOnlyList<TenantEnCarteraDeGestor> sinGestorCae,
        CancellationToken cancellationToken)
    {
        Avisos.Add((operadorTenantId, nombreDesactivado, sinGestorCae));
        return Task.CompletedTask;
    }
}
