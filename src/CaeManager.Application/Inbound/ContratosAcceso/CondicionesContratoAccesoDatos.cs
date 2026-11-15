using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using FluentValidation;

namespace CaeManager.Application.Inbound.ContratosAcceso;

/// <summary>
/// Las condiciones del contrato de acceso tal como llegan en un comando (B-05): las mismas de
/// <see cref="CondicionesContratoAcceso"/>, sin validar todavía. El validador da el mensaje de campo y el dominio repite
/// las reglas; <see cref="ADominio"/> convierte un fallo del dominio en un <see cref="Result"/>.
/// </summary>
/// <param name="CupoTrabajadores">Null = sin límite.</param>
/// <param name="CupoSubcontratas">Null = sin límite.</param>
/// <param name="MotivoCupo">Obligatorio si hay algún cupo.</param>
public record CondicionesContratoAccesoDatos(
    TipoContratoAcceso? Tipo = null,
    PropiaActividadContrato? PropiaActividad = null,
    int? NivelMaxSubcontrata = null,
    int? CupoTrabajadores = null,
    int? CupoSubcontratas = null,
    string? MotivoCupo = null)
{
    public Result<CondicionesContratoAcceso> ADominio()
    {
        try
        {
            return Result.Exito(new CondicionesContratoAcceso(
                Tipo, PropiaActividad, NivelMaxSubcontrata, CupoTrabajadores, CupoSubcontratas, MotivoCupo));
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo<CondicionesContratoAcceso>(Error.Crear("ContratoAcceso.CondicionesInvalidas", ex.Message));
        }
    }
}

public class CondicionesContratoAccesoDatosValidator : AbstractValidator<CondicionesContratoAccesoDatos>
{
    public CondicionesContratoAccesoDatosValidator()
    {
        RuleFor(c => c.Tipo).IsInEnum().When(c => c.Tipo is not null);
        RuleFor(c => c.PropiaActividad).IsInEnum().When(c => c.PropiaActividad is not null);
        RuleFor(c => c.NivelMaxSubcontrata).InclusiveBetween(0, CondicionesContratoAcceso.NivelSubcontrataMaximo)
            .When(c => c.NivelMaxSubcontrata is not null)
            .WithMessage($"El nivel máximo de subcontrata va de 0 a {CondicionesContratoAcceso.NivelSubcontrataMaximo}.");
        RuleFor(c => c.CupoTrabajadores).GreaterThanOrEqualTo(0).When(c => c.CupoTrabajadores is not null)
            .WithMessage("El cupo de Trabajadores no puede ser negativo.");
        RuleFor(c => c.CupoSubcontratas).GreaterThanOrEqualTo(0).When(c => c.CupoSubcontratas is not null)
            .WithMessage("El cupo de subcontratas no puede ser negativo.");
        // El dominio recorta el motivo y trata uno en blanco como ausente: el validador mide y compara lo mismo.
        RuleFor(c => c.MotivoCupo).Must(m => m is null || m.Trim().Length <= CondicionesContratoAcceso.LongitudMaximaMotivoCupo)
            .WithMessage($"El motivo del cupo no puede superar {CondicionesContratoAcceso.LongitudMaximaMotivoCupo} caracteres.");
        RuleFor(c => c.MotivoCupo).NotEmpty().When(c => c.CupoTrabajadores is not null || c.CupoSubcontratas is not null)
            .WithMessage("Si el contrato tiene cupo, indica el motivo del cupo.");
        RuleFor(c => c.MotivoCupo).Must(string.IsNullOrWhiteSpace).When(c => c.CupoTrabajadores is null && c.CupoSubcontratas is null)
            .WithMessage("Sin cupo no hay motivo de cupo.");
    }
}
