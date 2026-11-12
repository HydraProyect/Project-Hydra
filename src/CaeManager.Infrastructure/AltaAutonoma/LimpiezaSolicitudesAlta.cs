using CaeManager.Application.AltaAutonoma;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.AltaAutonoma;

/// <summary>
/// <see cref="ILimpiezaSolicitudesAlta"/> sobre las funciones <c>SECURITY DEFINER</c> de la migración
/// <c>CaducidadYBorradoSolicitudesAlta</c>. No pasa por el <c>DbSet</c>: <c>SolicitudesAlta</c> está cerrada a
/// <c>cae_app_runtime</c> y así sigue. El único parámetro es el tamaño del lote, que EF envía como parámetro.
/// </summary>
public sealed class LimpiezaSolicitudesAlta(CaeManagerDbContext contexto) : ILimpiezaSolicitudesAlta
{
    public Task<int> CaducarVencidasAsync(int tamanoLote, CancellationToken cancellationToken) =>
        contexto.Database
            .SqlQuery<int>($"SELECT public.app_alta_caducar_solicitudes({tamanoLote}) AS \"Value\"")
            .SingleAsync(cancellationToken);

    public Task<int> BorrarDatosPersonalesAsync(int tamanoLote, CancellationToken cancellationToken) =>
        contexto.Database
            .SqlQuery<int>($"SELECT public.app_alta_borrar_datos_personales({tamanoLote}) AS \"Value\"")
            .SingleAsync(cancellationToken);
}
