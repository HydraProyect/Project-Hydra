using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class PreferenciaDashboardUsuarioRepository(CaeManagerDbContext dbContext) : IPreferenciaDashboardUsuarioRepository
{
    public Task<PreferenciaDashboardUsuario?> ObtenerPorUsuarioIdAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        dbContext.PreferenciasDashboardUsuario.FirstOrDefaultAsync(p => p.UsuarioId == usuarioId, cancellationToken);

    public void Agregar(PreferenciaDashboardUsuario preferencia) => dbContext.PreferenciasDashboardUsuario.Add(preferencia);
}
