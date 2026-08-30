using CaeManager.Application.Common;
using FluentValidation;

namespace CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;

/// <summary>
/// Rellena una <see cref="Domain.Plantillas.PlantillaDocumentoVersion"/>
/// confirmada con datos de Hydra y crea un <see cref="Domain.Documentos.Documento"/>
/// real (ADR-010 § 2.1) más su <see cref="Domain.Plantillas.DocumentoGenerado"/>
/// de trazabilidad.
///
/// <paramref name="OwnerId"/> es a quién pertenece el <c>Documento</c> —
/// Trabajador/Cliente/Empresa, según el <c>AmbitoAplicacion</c> de la
/// plantilla (ADR-010 no contempla Vehículo/Proyecto para este flujo, pero
/// tampoco lo prohíbe: <c>Documento</c> los soporta igual, así que se
/// reutilizan sin más).
///
/// <paramref name="CentroId"/> es contexto opcional: de él se resuelven
/// <c>CentroNombre</c>/<c>CentroDireccion</c> y, si el ámbito no es ya
/// Empresa/Cliente, también la Empresa/Cliente del Centro (todo Centro
/// pertenece a exactamente uno de cada) — es la vía más común en la práctica
/// (un formulario de Trabajador en un Centro concreto).
///
/// <paramref name="ValoresManuales"/> resuelve los elementos con
/// <see cref="Domain.Plantillas.FuenteDatoPlantilla.Manual"/>, indexados por
/// <c>PlantillaElemento.Id</c> — estable desde que la versión está
/// confirmada (nunca vuelve a llamarse <c>EstablecerElementos</c>).
/// </summary>
public record GenerarDocumentoIndividualCommand(
    Guid PlantillaDocumentoVersionId,
    Guid OwnerId,
    Guid? CentroId = null,
    IReadOnlyDictionary<Guid, string>? ValoresManuales = null)
    : ICommand<GenerarDocumentoIndividualResultadoDto>;

public record GenerarDocumentoIndividualResultadoDto(Guid DocumentoGeneradoId, Guid DocumentoId);

public class GenerarDocumentoIndividualCommandValidator : AbstractValidator<GenerarDocumentoIndividualCommand>
{
    // Anti-abuso (auditoría de seguridad del módulo, 2026-08-30): sin límite,
    // el valor manual (texto libre del gestor) se serializa sin cota en
    // DocumentoGenerado.DatosUtilizadosJson — un valor no acotado o un número
    // arbitrario de ellos infla ese JSON sin ningún control. No hay tantos
    // PlantillaElemento con FuenteDato Manual en una plantilla real como para
    // acercarse a este límite.
    public const int LongitudMaximaValorManual = 2000;
    public const int MaximoValoresManuales = 200;

    public GenerarDocumentoIndividualCommandValidator()
    {
        RuleFor(c => c.PlantillaDocumentoVersionId).NotEmpty();
        RuleFor(c => c.OwnerId).NotEmpty();
        RuleFor(c => c.ValoresManuales)
            .Must(v => v is null || v.Count <= MaximoValoresManuales)
            .WithMessage($"No se pueden enviar más de {MaximoValoresManuales} valores manuales.");
        RuleForEach(c => c.ValoresManuales)
            .Must(kv => kv.Value is null || kv.Value.Length <= LongitudMaximaValorManual)
            .WithMessage($"Cada valor manual no puede superar {LongitudMaximaValorManual} caracteres.")
            .When(c => c.ValoresManuales is not null);
    }
}
