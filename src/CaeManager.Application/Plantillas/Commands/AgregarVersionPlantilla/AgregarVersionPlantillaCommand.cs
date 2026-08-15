using CaeManager.Application.Common;
using CaeManager.Domain.Plantillas;
using FluentValidation;

namespace CaeManager.Application.Plantillas.Commands.AgregarVersionPlantilla;

/// <summary>
/// El centro sustituye el PDF de una plantilla ya existente — el gestor
/// decide manualmente que es una versión nueva de la misma plantilla, no
/// una plantilla distinta (ADR-010 § 4, § 6: sin fingerprinting estructural
/// automático, "explícitamente diferido"). Los <see cref="PlantillaElemento"/>
/// de la nueva versión se copian de la última versión confirmada —memoria
/// de plantilla, Nivel A de § 2.4— en vez de partir de cero o volver a
/// correr la detección por IA, que no conoce las correcciones ya hechas por
/// un humano. El gestor los revisa y ajusta en el editor visual (PR5) igual
/// que con cualquier borrador.
/// </summary>
public record AgregarVersionPlantillaCommand(Guid PlantillaDocumentoId, byte[] Contenido, string NombreArchivo)
    : ICommand<AgregarVersionPlantillaResultadoDto>;

/// <summary>
/// <paramref name="ArchivoIdenticoAVersionAnterior"/> es la detección de
/// cambio "por hash exacto" de ADR-010 § 4 — un aviso para el gestor
/// (no bloquea nada): si es idéntico byte a byte a la última versión
/// confirmada, probablemente no hacía falta subir nada nuevo.
/// </summary>
public record AgregarVersionPlantillaResultadoDto(Guid PlantillaDocumentoVersionId, bool ArchivoIdenticoAVersionAnterior);

public class AgregarVersionPlantillaCommandValidator : AbstractValidator<AgregarVersionPlantillaCommand>
{
    public AgregarVersionPlantillaCommandValidator()
    {
        RuleFor(c => c.PlantillaDocumentoId).NotEmpty();
        RuleFor(c => c.Contenido).NotEmpty().WithMessage("El PDF de la nueva versión es obligatorio.");
        RuleFor(c => c.NombreArchivo).NotEmpty();
    }
}
