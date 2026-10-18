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
    IAlcanceDatosService alcanceDatos)
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
            // Otro alta simultánea con el mismo nombre ganó la carrera del índice único: se devuelve ese Oficio, igual
            // que si ya hubiera existido al leer.
            if (!await catalogo.GuardarCambiosAsync(cancellationToken))
                return await OficioConElMismoNombreAsync(empresa.Id, clave, cancellationToken) is { } ganador
                    ? ganador
                    : Result.Fallo<Guid>(Error.Crear("Oficio.Duplicado", "Esta empresa ya tiene un oficio con ese nombre."));
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
        if (!await catalogo.GuardarCambiosAsync(cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Oficio.Duplicado", "Esta empresa ya tiene un oficio con ese nombre."));
        return oficio.Id;
    }

    private async Task<Guid?> OficioConElMismoNombreAsync(Guid empresaId, string clave, CancellationToken cancellationToken) =>
        (await catalogo.ListarOficiosAsync(empresaId, cancellationToken))
            .FirstOrDefault(o => NombreDeCatalogo.Clave(o.Nombre) == clave)?.Id;
}
