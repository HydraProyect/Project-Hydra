using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ParametroSistemaRepository(CaeManagerDbContext dbContext) : IParametroSistemaRepository
{
    /// <summary>
    /// La fila vigente, con seguimiento para poder actualizarla. En un circuito de Blazor el contexto vive todo el circuito:
    /// si otro usuario cambió los umbrales, EF devolvería la instancia ya rastreada con los valores viejos y un
    /// <c>Actualizar</c> a valores iguales a esa copia no emitiría ningún UPDATE aunque la base tenga otros. Por eso se
    /// refrescan los valores desde la base cuando la instancia está sin cambios pendientes.
    /// </summary>
    public async Task<ParametroSistema> ObtenerAsync(CancellationToken cancellationToken = default)
    {
        var parametros = await dbContext.ParametrosSistema.SingleAsync(cancellationToken);

        var entrada = dbContext.Entry(parametros);
        if (entrada.State == EntityState.Unchanged)
            await entrada.ReloadAsync(cancellationToken);

        return parametros;
    }

    public void Agregar(ParametroSistema parametro) => dbContext.ParametrosSistema.Add(parametro);
}
