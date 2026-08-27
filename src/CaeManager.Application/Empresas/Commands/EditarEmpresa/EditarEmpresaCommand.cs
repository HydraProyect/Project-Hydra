using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Commands.EditarEmpresa;

public record EditarEmpresaCommand(
    Guid Id,
    string RazonSocial,
    string? Cif,
    IReadOnlyList<Guid> ClienteIds,
    string? Cnae = null,
    string? ConvenioAplicable = null,
    bool EsActividadAnexoI = false,
    Guid Version = default) : ICommand;

public class EditarEmpresaCommandValidator : AbstractValidator<EditarEmpresaCommand>
{
    public EditarEmpresaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.RazonSocial)
            .NotEmpty().WithMessage("La razón social es obligatoria.")
            .MaximumLength(Empresa.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Empresa.LongitudMaximaRazonSocial} caracteres.");

        // A diferencia del alta (CrearEmpresaCommand), aquí el CIF sigue
        // siendo opcional: hay Empresas legacy sin CIF (ver Empresa.Cif) y
        // editar otro campo no debe forzar retroactivamente su relleno.
        RuleFor(c => c.Cif)
            .Must(EsCifValido).WithMessage("El CIF no es válido.")
            .When(c => !string.IsNullOrWhiteSpace(c.Cif));

        RuleFor(c => c.Cnae).MaximumLength(Empresa.LongitudMaximaCnae);
        RuleFor(c => c.ConvenioAplicable).MaximumLength(Empresa.LongitudMaximaConvenioAplicable);
    }

    private static bool EsCifValido(string? cif)
    {
        var resultado = ValidadorIdentificacion.Analizar(cif!);
        return resultado.Tipo == TipoIdentificacion.NifEmpresa && resultado.EsValido;
    }
}

public class EditarEmpresaCommandHandler(
    IEmpresaRepository repositorio,
    IRelacionEmpresarialRepository relacionEmpresarialRepositorio,
    IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarEmpresaCommand, Result>
{
    public async Task<Result> Handle(EditarEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (empresa is null || !await alcanceDatos.EmpresaVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        if (ConcurrenciaOptimista.Verificar(empresa, request.Version, "esta empresa") is { } conflicto)
            return Result.Fallo(conflicto);

        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.RazonSocialDuplicada", "Ya existe una empresa con esta razón social."));

        if (!string.IsNullOrWhiteSpace(request.Cif) && await repositorio.ExisteConCifAsync(request.Cif, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.CifDuplicado", "Ya existe una empresa con este CIF."));

        empresa.Actualizar(request.RazonSocial, request.Cif, request.Cnae, request.ConvenioAplicable, request.EsActividadAnexoI);

        // F4.2c — los DOS lados del diff leen la misma fuente clasificada
        // (ver ContrapartesVigentes): "actuales" es el eje Cliente de la
        // arista, y una contraparte OPACA (soft-deleted, o no clasificable)
        // no entra jamás en "actuales" — por tanto jamás puede cerrarse por
        // ausencia en "deseados". Es el invariante que impide el borrado
        // silencioso que la revisión adversarial de F4.2b encontró: las
        // bajas se calculan sobre lo que el usuario pudo desmarcar, no sobre
        // lo que existe.
        var contrapartes = await relacionEmpresarialRepositorio.ObtenerContrapartesVigentesAsync(empresa.Id, cancellationToken);
        var deseados = request.ClienteIds.Distinct().ToHashSet();
        var actualesClienteIds = contrapartes.ClienteIds.ToHashSet();

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        // Solo hace falta verificar las vinculaciones NUEVAS: las que ya
        // estaban antes ya pasaron por esta comprobación cuando se crearon.
        var clienteIdsNuevos = deseados.Except(actualesClienteIds).ToList();
        var clientesNuevosEncontrados = await empresasContext.Empresas
            .Where(e => clienteIdsNuevos.Contains(e.Id))
            .CountAsync(cancellationToken);

        if (clientesNuevosEncontrados != clienteIdsNuevos.Count)
            return Result.Fallo(Error.Crear("Empresa.ClienteNoEncontrado", "Alguno de los clientes seleccionados no existe."));

        // Solo el diff de ClienteIds toca la arista. Los campos de identidad
        // actualizados arriba (RazonSocial/Cif/Cnae/ConvenioAplicable/
        // EsActividadAnexoI) no generan ninguna escritura aquí — editar el
        // nombre de una Empresa no es cambiar sus relaciones.
        var ahora = DateTime.UtcNow;

        foreach (var clienteId in actualesClienteIds.Where(id => !deseados.Contains(id)))
            await relacionEmpresarialRepositorio.CerrarVigenteAsync(empresa.Id, clienteId, ahora, cancellationToken);

        foreach (var clienteId in clienteIdsNuevos)
            await relacionEmpresarialRepositorio.AgregarSiNoVigenteAsync(
                empresa.Id, clienteId, ahora, cancellationToken: cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
