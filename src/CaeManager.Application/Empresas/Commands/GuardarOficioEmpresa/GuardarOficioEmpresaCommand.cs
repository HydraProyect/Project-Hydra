using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.GuardarOficioEmpresa;

/// <summary>
/// Crea (<paramref name="OficioId"/> nulo) o renombra un Oficio de la Empresa, desde Empresa 360 o desde el selector
/// «+ Crear oficio» del alta y la edición del Trabajador. Devuelve el Id del Oficio. Si ya existe uno con el mismo nombre
/// (sin distinguir mayúsculas ni espacios), crear devuelve ese en vez de duplicarlo.
///
/// <para>
/// Autorización igual que <c>GuardarNotaInternaEmpresaCommand</c>: escritura (<c>AutorizacionEscrituraBehavior</c>) y
/// alcance de gestión sobre la Empresa (<see cref="IAlcanceDatosService.EmpresaParaGestionVisibleAsync"/>); fuera de
/// cartera, «no encontrada». Renombrar exige la <paramref name="Version"/> leída (<see cref="ConcurrenciaOptimista"/>).
/// </para>
/// </summary>
public record GuardarOficioEmpresaCommand(Guid EmpresaId, Guid? OficioId, string Nombre, Guid? Version) : ICommand<Guid>;

public class GuardarOficioEmpresaCommandValidator : AbstractValidator<GuardarOficioEmpresaCommand>
{
    public GuardarOficioEmpresaCommandValidator()
    {
        RuleFor(c => c.EmpresaId).NotEmpty();
        RuleFor(c => c.Version).NotEmpty().When(c => c.OficioId is not null);
        RuleFor(c => c.Nombre).NotEmpty().WithMessage("El nombre del oficio es obligatorio.")
            .MaximumLength(OficioEmpresa.LongitudMaximaNombre);
    }
}

public class GuardarOficioEmpresaCommandHandler(
    IEmpresaRepository empresas,
    ICatalogoTrabajadoresEmpresaRepository catalogo,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarOficioEmpresaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(GuardarOficioEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await empresas.ObtenerPorIdAsync(request.EmpresaId, cancellationToken);
        if (empresa is null || !await alcanceDatos.EmpresaParaGestionVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        var existentes = await catalogo.ListarOficiosAsync(empresa.Id, cancellationToken);
        var clave = NombreDeCatalogo.Clave(request.Nombre);
        var mismoNombre = existentes.FirstOrDefault(o => o.Id != request.OficioId && NombreDeCatalogo.Clave(o.Nombre) == clave);

        if (request.OficioId is not { } id)
        {
            if (mismoNombre is not null)
                return mismoNombre.Id;

            var nuevo = new OficioEmpresa(empresa.Id, request.Nombre);
            catalogo.Agregar(nuevo);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return nuevo.Id;
        }

        var oficio = existentes.FirstOrDefault(o => o.Id == id);
        if (oficio is null)
            return Result.Fallo<Guid>(Error.Crear("Oficio.NoEncontrado", "No encontramos este oficio."));
        if (ConcurrenciaOptimista.Verificar(oficio, request.Version!.Value, "este oficio") is { } conflicto)
            return Result.Fallo<Guid>(conflicto);
        if (mismoNombre is not null)
            return Result.Fallo<Guid>(Error.Crear("Oficio.Duplicado", "Esta empresa ya tiene un oficio con ese nombre."));

        oficio.Renombrar(request.Nombre);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return oficio.Id;
    }
}
