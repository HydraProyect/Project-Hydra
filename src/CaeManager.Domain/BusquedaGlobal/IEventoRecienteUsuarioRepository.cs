namespace CaeManager.Domain.BusquedaGlobal;

public interface IEventoRecienteUsuarioRepository
{
    void Agregar(EventoRecienteUsuario evento);

    /// <summary>
    /// Marca para eliminar los eventos del mismo usuario que queden fuera de
    /// los <paramref name="maximoAConservar"/> más recientes — sin guardar
    /// por sí sola: el handler que la invoca hace un único SaveChangesAsync
    /// junto con el Agregar() que la precede, para que alta y purga sean la
    /// misma unidad de trabajo. Sin parámetro de tenant: el filtro global de
    /// aislamiento (CaeManagerDbContext.OnModelCreating) ya acota cualquier
    /// consulta sobre EventoRecienteUsuario al tenant actual.
    /// </summary>
    Task PurgarExcedentesAsync(Guid usuarioId, int maximoAConservar, CancellationToken cancellationToken);
}
