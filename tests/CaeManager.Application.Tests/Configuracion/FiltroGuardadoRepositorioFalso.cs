using CaeManager.Domain.Configuracion;

namespace CaeManager.Application.Tests.Configuracion;

/// <summary>
/// Repositorio en memoria. No conoce el Tenant: en el de verdad lo acotan el
/// filtro global de EF y la política RLS, y eso se prueba en integración.
/// </summary>
public class FiltroGuardadoRepositorioFalso : IFiltroGuardadoRepository
{
    public List<FiltroGuardado> Filtros { get; } = [];

    public Task<FiltroGuardado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Filtros.FirstOrDefault(f => f.Id == id));

    public Task<bool> ExisteConNombreAsync(Guid usuarioId, string pantalla, string nombre, CancellationToken cancellationToken = default) =>
        Task.FromResult(Filtros.Any(f => f.UsuarioId == usuarioId && f.Pantalla == pantalla && f.Nombre == nombre));

    public Task<FiltroGuardado?> ObtenerVistaRecordadaAsync(Guid usuarioId, string pantalla, CancellationToken cancellationToken = default) =>
        Task.FromResult(Filtros.FirstOrDefault(f => f.UsuarioId == usuarioId && f.Pantalla == pantalla && f.EsVistaRecordada));

    public void Agregar(FiltroGuardado filtro) => Filtros.Add(filtro);

    public void Eliminar(FiltroGuardado filtro) => Filtros.Remove(filtro);
}
