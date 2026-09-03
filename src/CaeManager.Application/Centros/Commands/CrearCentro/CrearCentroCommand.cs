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
    ICentroRepository repositorio, IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext, ITipoDocumentoCentroRepository tipoDocumentoCentroRepositorio,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearCentroCommand, Result<Guid>>
{
    /// <summary>
    /// Catálogo mínimo por defecto de todo Centro nuevo (PLAN-EJECUCION-UX.md
    /// § 0.4) — se busca por Nombre, no por Id fijo: los Id de
    /// <c>TipoDocumentoSeedData</c> son del catálogo semilla del tenant #1
    /// únicamente (cada tenant recibe su propia copia editable al
    /// aprovisionarse, ver docs/MULTITENANCY.md § 7), así que referenciarlos
    /// por Id aquí crearía una fila cruzando tenants. Si un TENANT concreto
    /// no tiene (o renombró) alguno de estos tipos para su propio catálogo,
    /// simplemente no se añade esa fila — degradación silenciosa deliberada,
    /// no un error de alta de Centro: personalizar el catálogo es legítimo.
    ///
    /// Lo que esto NO cubre (auditoría Módulo 5, hueco arquitectónico): si el
    /// CATÁLOGO SEMILLA (<see cref="TipoDocumentoSeedData"/>) renombra uno de
    /// estos cuatro nombres en una futura limpieza (como ya pasó con la T3,
    /// ver su doc-comment), todo tenant aprovisionado DESPUÉS de ese cambio
    /// nacería con el catálogo mínimo incompleto para siempre, en silencio —
    /// nadie personalizó nada, es la propia semilla la que dejó de casar.
    /// CatalogoMinimoCentroCasaConSemillaTests (CaeManager.Architecture.Tests)
    /// es el ratchet que falla en CI si eso ocurre — este campo es público a
    /// propósito para que ese test pueda referenciarlo.
    /// </summary>
    public static readonly string[] NombresCatalogoMinimo =
    [
        "Certificado de aptitud médica",
        "Entrega de EPI",
        "Formación Art. 19",
        "Información Art. 18"
    ];

    public async Task<Result<Guid>> Handle(CrearCentroCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        // Centro.ClienteId repunta contra Empresas desde F3b (CentroConfiguration):
        // "Cliente" es una Empresa contraparte (Empresa.CrearComoCliente), Clientes
        // queda congelada.
        if (!await empresasContext.Empresas.AnyAsync(e => e.Id == request.ClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Centro.ClienteNoEncontrado", "No encontramos este cliente."));

        if (!await empresasContext.Empresas.AnyAsync(e => e.Id == request.EmpresaId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Centro.EmpresaNoEncontrada", "No encontramos esta empresa."));

        // Autoridad sobre ambas puntas, no solo existencia (auditoría Módulo
        // 5, hallazgo crítico 5/9): un gestor podía crear un centro dentro de
        // la cartera de OTRO gestor con solo conocer el Id de su cliente.
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Centro.ClienteNoEncontrado", "No encontramos este cliente."));

        // Defensa en profundidad (REC-149): inalcanzable para el rol Cliente
        // vía AutorizacionEscrituraBehavior; alcance de gestión como segunda
        // barrera independiente.
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
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
