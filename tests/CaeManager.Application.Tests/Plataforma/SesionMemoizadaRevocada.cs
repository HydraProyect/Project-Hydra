using CaeManager.Application.Plataforma;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// La memo del circuito aún conserva la sesión; la base ya no la reconoce porque su
/// concesión se revocó o la sesión se cerró en otra pestaña después de la primera
/// resolución. Una consulta que lea con <c>ObtenerAsync</c> sigue viéndola; una que
/// revalide con <c>RevalidarAsync</c>, no.
/// </summary>
internal sealed class SesionMemoizadaRevocada(SesionPrivilegiadaActiva memoizada) : ISesionPrivilegiadaActual
{
    public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<SesionPrivilegiadaActiva?>(memoizada);

    public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<SesionPrivilegiadaActiva?>(null);
}
