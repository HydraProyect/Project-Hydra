namespace CaeManager.Web.Features.Inbound.CentroInbound;

/// <summary>El estado del Trabajador para entrar hoy en el Centro: Apto, vencido dentro de la tolerancia (puede entrar) o No apto.</summary>
public enum EstadoAptitudHoy
{
    Apto,
    EnTolerancia,
    NoApto,
}

/// <summary>
/// Una fila de «Aptos hoy». Solo lleva lo que la portería y el Gestor CAE pueden ver: el nombre del requisito que bloquea o
/// vence (por ejemplo «Certificado de aptitud») y un detalle de fechas. No existe campo para el motivo médico: la pantalla
/// no puede enseñarlo porque el modelo no lo trae (plan de pantallas Inbound, § 5 bis).
/// </summary>
public sealed record TrabajadorAptoHoy(
    Guid TrabajadorId, string Nombre, string DniParcial, string EmpresaContratista, string TipoDeContrato,
    EstadoAptitudHoy Estado, string? RequisitoQueBloquea, string? Detalle);

/// <summary>
/// Puerto de la pestaña «Aptos hoy» del Centro (Q-24). Devuelve null cuando la lectura no está disponible: la pantalla lo
/// dice y no pinta ningún dato.
/// </summary>
public interface IAptosHoyDelCentro
{
    Task<IReadOnlyList<TrabajadorAptoHoy>?> ObtenerAsync(Guid centroId, CancellationToken cancellationToken = default);
}

/// <summary>Implementación vacía mientras no exista la aptitud materializada de B-28.</summary>
public sealed class AptosHoyDelCentroNoDisponible : IAptosHoyDelCentro
{
    // shortcut: sin backend, cablear cuando entre B-28 (aptitud materializada, Aptos hoy completa y restringida, papel de portería).
    public Task<IReadOnlyList<TrabajadorAptoHoy>?> ObtenerAsync(Guid centroId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TrabajadorAptoHoy>?>(null);
}
