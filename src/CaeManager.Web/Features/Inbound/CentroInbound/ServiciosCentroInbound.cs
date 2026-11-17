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
/// no puede enseñarlo porque el modelo no lo trae (plan de pantallas Inbound, § 5 bis). Con <paramref name="ConRequisito"/> a
/// false (proyección restringida, sin <c>ValidarInbound</c>; duda 6) la fila solo lleva nombre, DNI parcial, Empresa
/// contratista y apto o no apto, y la pantalla no pinta ni el tipo de contrato ni la columna de lo que bloquea o vence.
/// </summary>
public sealed record TrabajadorAptoHoy(
    Guid TrabajadorId, string Nombre, string DniParcial, string EmpresaContratista, string TipoDeContrato,
    EstadoAptitudHoy Estado, string? RequisitoQueBloquea, string? Detalle, bool ConRequisito = true);

/// <summary>
/// Puerto de la pestaña «Aptos hoy» del Centro (Q-24). Lo implementa <see cref="AptosHoyDelCentroConBackend"/>. Devuelve null
/// cuando la lectura no está disponible: la pantalla lo dice y no pinta ningún dato.
/// </summary>
public interface IAptosHoyDelCentro
{
    Task<IReadOnlyList<TrabajadorAptoHoy>?> ObtenerAsync(Guid centroId, CancellationToken cancellationToken = default);
}
