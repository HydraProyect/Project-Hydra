using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class NotaInternaConversacionRepository(CaeManagerDbContext dbContext) : INotaInternaConversacionRepository
{
    public void Agregar(NotaInternaConversacion nota) => dbContext.NotasInternasConversacion.Add(nota);
}
