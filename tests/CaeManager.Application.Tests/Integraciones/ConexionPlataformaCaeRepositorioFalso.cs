using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

public class ConexionPlataformaCaeRepositorioFalso : IConexionPlataformaCaeRepository
{
    public List<ConexionPlataformaCae> Conexiones { get; } = [];
    public List<CredencialConexionPlataformaCae> Credenciales { get; } = [];
    public List<UsoConexionPlataformaCae> Usos { get; } = [];
    public List<AcreditacionParaConector> Acreditaciones { get; } = [];
    public List<TrabajadorParaConector> Trabajadores { get; } = [];
    public HabilitacionComercialConector? Habilitacion { get; set; }

    public Task<ConexionPlataformaCae?> ObtenerAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conexiones.FirstOrDefault(c => c.Id == id && !c.EstaEliminado));

    public Task<ConexionPlataformaCae?> ObtenerPorNombreAsync(string nombre, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conexiones.FirstOrDefault(c => c.Nombre == nombre && !c.EstaEliminado));

    public Task<CredencialConexionPlataformaCae?> ObtenerCredencialAsync(Guid conexionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Credenciales.FirstOrDefault(c => c.ConexionPlataformaCaeId == conexionId));

    public Task<AcreditacionParaConector?> ObtenerAcreditacionAsync(Guid acreditacionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Acreditaciones.FirstOrDefault(a => a.Id == acreditacionId));

    public Task<TrabajadorParaConector?> ObtenerTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Trabajadores.FirstOrDefault(t => t.Id == trabajadorId));

    public Task<HabilitacionComercialConector?> ObtenerHabilitacionComercialAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Habilitacion);

    public void Agregar(ConexionPlataformaCae conexion) => Conexiones.Add(conexion);

    public void AgregarCredencial(CredencialConexionPlataformaCae credencial) => Credenciales.Add(credencial);

    public void QuitarCredencial(CredencialConexionPlataformaCae credencial) => Credenciales.Remove(credencial);

    public void RegistrarUso(UsoConexionPlataformaCae uso) => Usos.Add(uso);
}
