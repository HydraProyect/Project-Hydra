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
    public List<(Guid Propietario, Guid Gestor)> CarterasTenantEnteroAseguradas { get; } = [];
    public List<Guid> RaicesAseguradas { get; } = [];
    public List<(Guid Propietario, Guid Operador)> OperacionesAbiertas { get; } = [];
    public List<(Guid Propietario, Guid Operador, MotivoCierreAsignacion Motivo)> OperacionesCerradas { get; } = [];
    public List<(Guid Usuario, string Rol)> CarterasAbiertas { get; } = [];
    public List<(Guid Propietario, Guid Operador, Guid Usuario, MotivoCierreAsignacion Motivo)> CarterasCerradas { get; } = [];
    public List<Guid> DelegacionesConCarterasReabiertas { get; } = [];

    public Task AsegurarCarteraTenantEnteroAsync(
        Guid propietarioTenantId, Guid gestorUsuarioId, CancellationToken cancellationToken = default)
    {
        CarterasTenantEnteroAseguradas.Add((propietarioTenantId, gestorUsuarioId));
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

    /// <summary>Quién era el principal cuando la cascada cerró la operación; lo fija cada test.</summary>
    public Guid? AnteriorPrincipal { get; set; }

    /// <summary>Para que un test cuelgue de la operación recién abierta las carteras que la reactivación repone.</summary>
    public Action<AsignacionOperacion>? AlReabrirCarteras { get; set; }

    public Task<Guid?> ReabrirCarterasDeOperadoresAsync(
        AsignacionOperacion operacion, Guid delegacionTenantId, CancellationToken cancellationToken = default)
    {
        DelegacionesConCarterasReabiertas.Add(delegacionTenantId);
        AlReabrirCarteras?.Invoke(operacion);
        return Task.FromResult(AnteriorPrincipal);
    }

    public Task CerrarCarteraOperadorAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, MotivoCierreAsignacion motivo,
        CancellationToken cancellationToken = default)
    {
        CarterasCerradas.Add((propietarioTenantId, operadorTenantId, usuarioId, motivo));
        return Task.CompletedTask;
    }
}
