using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.FijarDelegacionEmpresaYOficioTrabajador;

/// <summary>
/// Fija la Delegación y el Oficio del Trabajador (los dos desplegables de su ficha). <c>null</c> deja el campo vacío.
///
/// <para>
/// Escritura (<c>AutorizacionEscrituraBehavior</c>) y alcance sobre el Trabajador como
/// <c>GuardarNotaInternaTrabajadorCommand</c>; además, alcance de gestión sobre su Empresa
/// (<see cref="IAlcanceDatosService.EmpresaParaGestionVisibleAsync"/>), porque el catálogo es de la Empresa. Fuera de
/// alcance, «no encontrado». La Delegación y el Oficio tienen que ser de la Empresa del Trabajador: un Id de otra
/// Empresa responde «no encontrado» y, por debajo, la FK compuesta lo rechazaría igual.
/// </para>
/// </summary>
public record FijarDelegacionEmpresaYOficioTrabajadorCommand(
    Guid TrabajadorId, Guid? DelegacionEmpresaId, Guid? OficioEmpresaId, Guid Version) : ICommand;

public class FijarDelegacionEmpresaYOficioTrabajadorCommandValidator : AbstractValidator<FijarDelegacionEmpresaYOficioTrabajadorCommand>
{
    public FijarDelegacionEmpresaYOficioTrabajadorCommandValidator()
    {
        RuleFor(c => c.TrabajadorId).NotEmpty();
        RuleFor(c => c.Version).NotEmpty();
    }
}

public class FijarDelegacionEmpresaYOficioTrabajadorCommandHandler(
    ITrabajadorRepository trabajadores,
    ICatalogoTrabajadoresEmpresaRepository catalogo,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<FijarDelegacionEmpresaYOficioTrabajadorCommand, Result>
{
    public async Task<Result> Handle(FijarDelegacionEmpresaYOficioTrabajadorCommand request, CancellationToken cancellationToken)
    {
        var trabajador = await trabajadores.ObtenerPorIdAsync(request.TrabajadorId, cancellationToken);
        if (trabajador is null || !await alcanceDatos.TrabajadorVisibleAsync(trabajador.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));

        if (trabajador.EmpresaId is not { } empresaId)
            return Result.Fallo(Error.Crear("Trabajador.SinEmpresa",
                "Solo un trabajador de empresa tiene delegación y oficio."));

        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(empresaId, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));

        if (ConcurrenciaOptimista.Verificar(trabajador, request.Version, "este trabajador") is { } conflicto)
            return Result.Fallo(conflicto);

        var (asignacion, error) = await AsignacionDelegacionEmpresaYOficio.ResolverAsync(
            trabajador, request.DelegacionEmpresaId, request.OficioEmpresaId, catalogo, cancellationToken);
        if (error is not null)
            return Result.Fallo(error);
        AsignacionDelegacionEmpresaYOficio.Aplicar(trabajador, asignacion!, renovarVersionDelCatalogo: true);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
