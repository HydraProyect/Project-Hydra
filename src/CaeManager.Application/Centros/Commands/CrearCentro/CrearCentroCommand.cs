using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Commands.CrearCentro;

public record CrearCentroCommand(
    Guid ClienteId,
    Guid EmpresaId,
    string Nombre,
    string? CodigoCentro,
    string? Direccion,
    string? Contacto,
    DateOnly? ContratoVigenteHasta) : ICommand<Guid>;

public class CrearCentroCommandValidator : AbstractValidator<CrearCentroCommand>
{
    public CrearCentroCommandValidator()
    {
        RuleFor(c => c.ClienteId).NotEmpty().WithMessage("Selecciona un cliente.");
        RuleFor(c => c.EmpresaId).NotEmpty().WithMessage("Selecciona una empresa.");

        RuleFor(c => c.Nombre)
            .NotEmpty().WithMessage("El nombre es obligatorio.")
            .MaximumLength(Centro.LongitudMaximaNombre).WithMessage($"El nombre no puede superar {Centro.LongitudMaximaNombre} caracteres.");

        RuleFor(c => c.CodigoCentro).MaximumLength(Centro.LongitudMaximaCodigo);
        RuleFor(c => c.Direccion).MaximumLength(Centro.LongitudMaximaDireccion);
        RuleFor(c => c.Contacto).MaximumLength(Centro.LongitudMaximaContacto);
    }
}

public class CrearCentroCommandHandler(
    ICentroRepository repositorio, IClientesQueryContext clientesContext, IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext, ITipoDocumentoCentroRepository tipoDocumentoCentroRepositorio,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearCentroCommand, Result<Guid>>
{
    /// <summary>
    /// Catálogo mínimo por defecto de todo Centro nuevo (PLAN-EJECUCION-UX.md
    /// § 0.4) — se busca por Nombre, no por Id fijo: los Id de
    /// <c>TipoDocumentoSeedData</c> son del catálogo semilla del tenant #1
    /// únicamente (cada tenant recibe su propia copia editable al
    /// aprovisionarse, ver docs/MULTITENANCY.md § 7), así que referenciarlos
    /// por Id aquí crearía una fila cruzando tenants. Si el tenant no tiene
    /// (o renombró) alguno de estos tipos, simplemente no se añade esa fila —
    /// degradación silenciosa, no un error de alta de Centro.
    /// </summary>
    private static readonly string[] NombresCatalogoMinimo =
    [
        "Apto médico laboral",
        "EPIS (firma)",
        "Formación Art. 19",
        "Información Art. 18"
    ];

    public async Task<Result<Guid>> Handle(CrearCentroCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (!await clientesContext.Clientes.AnyAsync(c => c.Id == request.ClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Centro.ClienteNoEncontrado", "No encontramos este cliente."));

        if (!await empresasContext.Empresas.AnyAsync(e => e.Id == request.EmpresaId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Centro.EmpresaNoEncontrada", "No encontramos esta empresa."));

        if (await repositorio.ExisteConNombreEnClienteAsync(request.ClienteId, request.Nombre, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Centro.NombreDuplicado", "Este cliente ya tiene un centro con este nombre."));

        var centro = new Centro(
            request.ClienteId, request.EmpresaId, request.Nombre,
            request.CodigoCentro, request.Direccion, request.Contacto, request.ContratoVigenteHasta);

        repositorio.Agregar(centro);

        var tipoIdsCatalogoMinimo = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador && NombresCatalogoMinimo.Contains(t.Nombre))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        foreach (var tipoDocumentoId in tipoIdsCatalogoMinimo)
            tipoDocumentoCentroRepositorio.Agregar(new TipoDocumentoCentro(tipoDocumentoId, centro.Id, incluido: true));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(centro.Id);
    }
}
