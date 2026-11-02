using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class RespuestaSugeridaRepository(CaeManagerDbContext dbContext) : IRespuestaSugeridaRepository
{
    public Task<RespuestaSugerida?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.RespuestasSugeridas.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<RespuestaSugerida?> ObtenerActivaPorConversacionAsync(Guid conversacionId, CancellationToken cancellationToken = default) =>
        dbContext.RespuestasSugeridas.FirstOrDefaultAsync(
            r => r.ConversacionId == conversacionId &&
                 (r.Estado == EstadoRespuestaSugerida.Propuesta || r.Estado == EstadoRespuestaSugerida.SinRevisar),
            cancellationToken);

    public void Agregar(RespuestaSugerida respuesta) => dbContext.RespuestasSugeridas.Add(respuesta);
}
