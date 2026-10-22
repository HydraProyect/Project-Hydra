using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.CorregirDocumentoIdentidadTrabajador;

/// <summary>
/// Acción «Corregir documento de identidad» de la ficha 360 del Trabajador (decisión de Chris, 2026-10-20). Aplica el
/// criterio del alta (<see cref="CriterioDocumentoIdentidadTrabajador"/>) y devuelve la decisión de la casilla «Trabajador
/// extranjero (cobrable)» al documento nuevo, solo hacia delante: las asignaciones a Visitas ya hechas conservan su foto.
/// </summary>
public record CorregirDocumentoIdentidadTrabajadorCommand(Guid Id, Guid Version, string Documento) : ICommand;

public class CorregirDocumentoIdentidadTrabajadorCommandValidator : AbstractValidator<CorregirDocumentoIdentidadTrabajadorCommand>
{
    public CorregirDocumentoIdentidadTrabajadorCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Version).NotEmpty();
        RuleFor(c => c.Documento).Custom((documento, contexto) =>
        {
            if (CriterioDocumentoIdentidadTrabajador.Clasificar(documento) is { EsValido: false, Error: { } error })
                contexto.AddFailure(error);
        });
    }
}

/// <summary>
/// <para>
/// <b>Alcance</b>: el Trabajador ha de ser visible y quien corrige ha de <b>gestionar</b> su Empresa o Subcontrata, el
/// mismo listón que la marca de extranjero cobrable (<c>EditarFichaTrabajadorCommandHandler</c>): el documento decide
/// qué cobra el Operador CAE. El rol con escritura lo exige antes <c>AutorizacionEscrituraBehavior</c>.
/// </para>
/// <para>
/// <b>Auditoría</b>: el interceptor de auditoría registra la modificación del Trabajador con el documento anterior y el
/// nuevo (<c>DatosAntes</c>/<c>DatosDespues</c>) y el Actor real que la hizo, también en impersonación.
/// </para>
/// </summary>
public class CorregirDocumentoIdentidadTrabajadorCommandHandler(
    ITrabajadorRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CorregirDocumentoIdentidadTrabajadorCommand, Result>
{
    public async Task<Result> Handle(CorregirDocumentoIdentidadTrabajadorCommand request, CancellationToken cancellationToken)
    {
        var trabajador = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (trabajador is null || !await alcanceDatos.TrabajadorVisibleAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));
        if (trabajador.EstaAnonimizado)
            return Result.Fallo(Error.Crear("Trabajador.Anonimizado",
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse."));
        if (ConcurrenciaOptimista.Verificar(trabajador, request.Version, "este trabajador") is { } conflicto)
            return Result.Fallo(conflicto);
        if (!await GestionaElEmpleadorAsync(trabajador, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.DocumentoSinGestion",
                "Solo quien gestiona la empresa o la subcontrata de este trabajador puede corregir su documento de identidad."));

        var documento = CriterioDocumentoIdentidadTrabajador.Clasificar(request.Documento);
        if (!documento.EsValido)
            return Result.Fallo(Error.Crear("Trabajador.DocumentoNoValido", documento.Error!));
        if (documento.Normalizado == trabajador.Dni)
            return Result.Exito();
        if (await repositorio.ExisteConDniAsync(documento.Normalizado!, trabajador.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.DniDuplicado", "Ya existe un trabajador con este documento de identidad."));

        trabajador.CorregirDocumentoIdentidad(documento.Normalizado!);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }

    private async Task<bool> GestionaElEmpleadorAsync(Trabajador trabajador, CancellationToken cancellationToken) =>
        trabajador.EmpresaId is { } empresaId
            ? await alcanceDatos.EmpresaParaGestionVisibleAsync(empresaId, cancellationToken)
            : trabajador.SubcontrataId is { } subcontrataId
              && await alcanceDatos.SubcontrataParaGestionVisibleAsync(subcontrataId, cancellationToken);
}
