using CaeManager.Application.Common;
using FluentValidation;

namespace CaeManager.Application.Plantillas.Commands.ConfirmarPlantillaDocumentoVersion;

/// <summary>
/// Congela la versión — deja de poder editarse y pasa a ser la versión
/// vigente de la <see cref="Domain.Plantillas.PlantillaDocumento"/> (ADR-010
/// § 2.3, "una plantilla confirmada nunca está parcialmente configurada").
/// Quién confirma sale de <see cref="ICurrentUserService"/>, nunca del
/// cliente — es lo que queda registrado en <c>ConfirmadaPorUsuarioId</c>.
/// </summary>
public record ConfirmarPlantillaDocumentoVersionCommand(Guid PlantillaDocumentoVersionId) : ICommand;

public class ConfirmarPlantillaDocumentoVersionCommandValidator : AbstractValidator<ConfirmarPlantillaDocumentoVersionCommand>
{
    public ConfirmarPlantillaDocumentoVersionCommandValidator()
    {
        RuleFor(c => c.PlantillaDocumentoVersionId).NotEmpty();
    }
}
