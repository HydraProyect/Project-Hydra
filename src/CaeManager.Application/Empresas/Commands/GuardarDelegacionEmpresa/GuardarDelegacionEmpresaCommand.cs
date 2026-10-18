using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.GuardarDelegacionEmpresa;

/// <summary>
/// Crea (<paramref name="DelegacionEmpresaId"/> nulo) o edita una Delegación de la Empresa desde Empresa 360, con su contacto
/// de reclamación. Devuelve el Id de la Delegación.
///
/// <para>
/// <b>Quién la guarda.</b> Mismo patrón que <c>GuardarNotaInternaEmpresaCommand</c>: por ser <see cref="ICommand{T}"/>,
/// <c>AutorizacionEscrituraBehavior</c> exige un rol con escritura; el handler exige alcance de <b>gestión</b> sobre la
/// Empresa (<see cref="IAlcanceDatosService.EmpresaParaGestionVisibleAsync"/>) y responde «no encontrada» a quien no
/// lo tiene, también si la Delegación no es de esa Empresa. <paramref name="Version"/> es la que leyó la pantalla al
/// editar: si otra persona guardó entre medias, la respuesta es el conflicto de <see cref="ConcurrenciaOptimista"/>.
/// </para>
/// </summary>
public record GuardarDelegacionEmpresaCommand(
    Guid EmpresaId,
    Guid? DelegacionEmpresaId,
    string Nombre,
    string? ContactoReclamacionNombre,
    string? ContactoReclamacionCorreo,
    string? ContactoReclamacionTelefono,
    Guid? Version) : ICommand<Guid>;

public class GuardarDelegacionEmpresaCommandValidator : AbstractValidator<GuardarDelegacionEmpresaCommand>
{
    public GuardarDelegacionEmpresaCommandValidator()
    {
        RuleFor(c => c.EmpresaId).NotEmpty();
        RuleFor(c => c.Version).NotEmpty().When(c => c.DelegacionEmpresaId is not null);
        RuleFor(c => c.Nombre).NotEmpty().WithMessage("El nombre de la delegación es obligatorio.")
            .MaximumLength(DelegacionEmpresa.LongitudMaximaNombre);
        RuleFor(c => c.ContactoReclamacionNombre).MaximumLength(DelegacionEmpresa.LongitudMaximaContactoNombre);
        RuleFor(c => c.ContactoReclamacionTelefono).MaximumLength(DelegacionEmpresa.LongitudMaximaContactoTelefono);
        RuleFor(c => c.ContactoReclamacionCorreo).MaximumLength(DelegacionEmpresa.LongitudMaximaContactoCorreo);
        RuleFor(c => c.ContactoReclamacionCorreo).EmailAddress().WithMessage("Introduce un correo electrónico válido.").When(c => !string.IsNullOrWhiteSpace(c.ContactoReclamacionCorreo));
    }
}

public class GuardarDelegacionEmpresaCommandHandler(
    IEmpresaRepository empresas,
    ICatalogoTrabajadoresEmpresaRepository catalogo,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarDelegacionEmpresaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(GuardarDelegacionEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await empresas.ObtenerPorIdAsync(request.EmpresaId, cancellationToken);
        if (empresa is null || !await alcanceDatos.EmpresaParaGestionVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        var existentes = await catalogo.ListarDelegacionesEmpresaAsync(empresa.Id, cancellationToken);
        var clave = NombreDeCatalogo.Clave(request.Nombre);
        if (existentes.Any(d => d.Id != request.DelegacionEmpresaId && NombreDeCatalogo.Clave(d.Nombre) == clave))
            return Result.Fallo<Guid>(Error.Crear("Delegacion.Duplicada", "Esta empresa ya tiene una delegación con ese nombre."));

        DelegacionEmpresa delegacion;
        if (request.DelegacionEmpresaId is { } id)
        {
            var encontrada = existentes.FirstOrDefault(d => d.Id == id);
            if (encontrada is null)
                return Result.Fallo<Guid>(Error.Crear("Delegacion.NoEncontrada", "No encontramos esta delegación."));
            if (ConcurrenciaOptimista.Verificar(encontrada, request.Version!.Value, "esta delegación") is { } conflicto)
                return Result.Fallo<Guid>(conflicto);
            delegacion = encontrada;
            delegacion.Renombrar(request.Nombre);
        }
        else
        {
            delegacion = new DelegacionEmpresa(empresa.Id, request.Nombre);
            catalogo.Agregar(delegacion);
        }

        delegacion.FijarContactoReclamacion(
            request.ContactoReclamacionNombre, request.ContactoReclamacionCorreo, request.ContactoReclamacionTelefono);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return delegacion.Id;
    }
}
