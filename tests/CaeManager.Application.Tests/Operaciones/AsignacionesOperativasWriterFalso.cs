using CaeManager.Application.Operaciones;
using CaeManager.Domain.Operaciones;

namespace CaeManager.Application.Tests.Operaciones;

/// <summary>
/// Doble de la doble escritura para los tests de handlers que no la están
/// ejercitando. Registra las llamadas en vez de ignorarlas, para que un test
/// que sí quiera comprobar que el handler escribió en las dos partes pueda
/// hacerlo sin montar una base de datos.
/// </summary>
public class AsignacionesOperativasWriterFalso : IAsignacionesOperativasWriter
{
    public List<(Guid ClienteId, Guid? EjecutivoId)> CarterasReasignadas { get; } = [];
    public List<Guid> RaicesAseguradas { get; } = [];
    public List<(Guid Propietario, Guid Operador)> OperacionesAbiertas { get; } = [];
    public List<(Guid Propietario, Guid Operador, MotivoCierreAsignacion Motivo)> OperacionesCerradas { get; } = [];
    public List<(Guid Usuario, string Rol)> CarterasAbiertas { get; } = [];
    public List<(Guid Propietario, Guid Operador, Guid Usuario, MotivoCierreAsignacion Motivo)> CarterasCerradas { get; } = [];
    public List<Guid> DelegacionesConCarterasReabiertas { get; } = [];

    public Task ReasignarCarteraClienteAsync(
        Guid clienteId, Guid? nuevoEjecutivoUsuarioId, CancellationToken cancellationToken = default)
    {
        CarterasReasignadas.Add((clienteId, nuevoEjecutivoUsuarioId));
        return Task.CompletedTask;
    }

    public Task AsegurarOperacionRaizAsync(
        Guid propietarioTenantId, DateTime vigenciaDesde, CancellationToken cancellationToken = default)
    {
        RaicesAseguradas.Add(propietarioTenantId);
        return Task.CompletedTask;
    }

    public Task<AsignacionOperacion> AbrirOperacionDelegadaAsync(
        Guid propietarioTenantId, Guid operadorTenantId, DateTime vigenciaDesde, DateTime? vigenciaHasta,
        CancellationToken cancellationToken = default)
    {
        OperacionesAbiertas.Add((propietarioTenantId, operadorTenantId));

        // Se devuelve una instancia real: el contrato exige que quien la reciba
        // pueda colgarle una cartera sin volver a buscarla.
        return Task.FromResult(AsignacionOperacion.Externa(
            propietarioTenantId, operadorTenantId, ServicioCae.Outbound,
            AmbitoAsignacion.Universal, vigenciaDesde, vigenciaHasta, DateTime.UtcNow));
    }

    public Task CerrarOperacionDelegadaAsync(
        Guid propietarioTenantId, Guid operadorTenantId, MotivoCierreAsignacion motivo,
        CancellationToken cancellationToken = default)
    {
        OperacionesCerradas.Add((propietarioTenantId, operadorTenantId, motivo));
        return Task.CompletedTask;
    }

    public Task AbrirCarteraOperadorAsync(
        AsignacionOperacion operacion, Guid usuarioId, string rol, CancellationToken cancellationToken = default)
    {
        CarterasAbiertas.Add((usuarioId, rol));
        return Task.CompletedTask;
    }

    public Task AbrirCarteraOperadorAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, string rol,
        CancellationToken cancellationToken = default)
    {
        CarterasAbiertas.Add((usuarioId, rol));
        return Task.CompletedTask;
    }

    public Task ReabrirCarterasDeOperadoresAsync(
        AsignacionOperacion operacion, Guid delegacionTenantId, CancellationToken cancellationToken = default)
    {
        DelegacionesConCarterasReabiertas.Add(delegacionTenantId);
        return Task.CompletedTask;
    }

    public Task CerrarCarteraOperadorAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, MotivoCierreAsignacion motivo,
        CancellationToken cancellationToken = default)
    {
        CarterasCerradas.Add((propietarioTenantId, operadorTenantId, usuarioId, motivo));
        return Task.CompletedTask;
    }
}
