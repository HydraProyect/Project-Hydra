using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Commands.CrearTrabajador;

/// <summary>
/// Un trabajador pertenece a una Empresa o a una Subcontrata, nunca ambas
/// (ver <see cref="Trabajador.DeEmpresa"/>/<see cref="Trabajador.DeSubcontrata"/>) —
/// exactamente uno de EmpresaId/SubcontrataId debe venir informado.
///
/// <see cref="IComandoDeAprovisionamiento" /> (PD-A3): forma parte del alta
/// planificada de contenido CAE en un tenant durante su aprovisionamiento
/// inicial — ver <c>AutorizacionEscrituraBehavior</c>.
/// </summary>
public record CrearTrabajadorCommand(
    Guid? EmpresaId,
    Guid? SubcontrataId,
    string Nombre,
    string Apellidos,
    string Dni,
    DateOnly? FechaNacimiento,
    string? Email,
    string? Observaciones,
    string? Alias = null,
    string? Telefono = null,
    string? Puesto = null,
    TipoContratacion TipoContratacion = TipoContratacion.CuentaAjena,
    Guid? OficioEmpresaId = null,
    Guid? DelegacionEmpresaId = null) : ICommand<Guid>, IComandoDeAprovisionamiento;

public class CrearTrabajadorCommandValidator : AbstractValidator<CrearTrabajadorCommand>
{
    public CrearTrabajadorCommandValidator()
    {
        RuleFor(c => c)
            .Must(c => (c.EmpresaId is not null) ^ (c.SubcontrataId is not null))
            .WithMessage("Selecciona una empresa o una subcontrata (no ambas).");

        RuleFor(c => c.TipoContratacion).IsInEnum().WithMessage("Elige cuenta ajena, autónomo o voluntario.");

        RuleFor(c => c.Nombre)
            .NotEmpty().WithMessage("El nombre es obligatorio.")
            .MaximumLength(Trabajador.LongitudMaximaNombre);

        RuleFor(c => c.Apellidos)
            .NotEmpty().WithMessage("Los apellidos son obligatorios.")
            .MaximumLength(Trabajador.LongitudMaximaApellidos);

        RuleFor(c => c.Dni)
            .Custom((dni, contexto) =>
            {
                // Criterio único del documento del Trabajador (decisión de Chris, 2026-10-20): DNI, NIE o Pasaporte.
                if (CriterioDocumentoIdentidadTrabajador.Clasificar(dni) is { EsValido: false, Error: { } error })
                    contexto.AddFailure(error);
            });

        RuleFor(c => c.Email)
            .MaximumLength(Trabajador.LongitudMaximaEmail)
            .EmailAddress().WithMessage("Introduce un correo válido.")
            .When(c => !string.IsNullOrWhiteSpace(c.Email));

        RuleFor(c => c.Observaciones).MaximumLength(Trabajador.LongitudMaximaObservaciones);
        RuleFor(c => c.Alias).MaximumLength(Trabajador.LongitudMaximaAlias);
        RuleFor(c => c.Telefono).MaximumLength(Trabajador.LongitudMaximaTelefono);
        RuleFor(c => c.Puesto).MaximumLength(Trabajador.LongitudMaximaPuesto);
        RuleFor(c => c)
            .Must(c => c.EmpresaId is not null || (c.OficioEmpresaId is null && c.DelegacionEmpresaId is null))
            .WithMessage("Solo un trabajador de empresa tiene delegación y oficio.");
    }

}

public class CrearTrabajadorCommandHandler(
    ITrabajadorRepository repositorio, IEmpresasQueryContext empresasContext,
    ICatalogoTrabajadoresEmpresaRepository catalogo, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearTrabajadorCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearTrabajadorCommand request, CancellationToken cancellationToken)
    {
        // Autoridad sobre el empleador, no solo existencia (auditoría Módulo
        // 5, hallazgo crítico 5/9) — ver TrabajadorAutorizacion.
        if (request.EmpresaId is { } empresaId)
        {
            if (!await empresasContext.Empresas.AnyAsync(e => e.Id == empresaId, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Trabajador.EmpresaNoEncontrada", "No encontramos esta empresa."));
            // Defensa en profundidad (REC-149): inalcanzable para el rol
            // Cliente vía AutorizacionEscrituraBehavior; alcance de gestión
            // como segunda barrera independiente.
            if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(empresaId, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Trabajador.EmpresaNoEncontrada", "No encontramos esta empresa."));
        }

        if (request.SubcontrataId is { } subcontrataId)
        {
            if (!await empresasContext.Empresas.AnyAsync(e => e.Id == subcontrataId, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Trabajador.SubcontrataNoEncontrada", "No encontramos esta subcontrata."));
            if (!await alcanceDatos.SubcontrataVisibleAsync(subcontrataId, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Trabajador.SubcontrataNoEncontrada", "No encontramos esta subcontrata."));
        }

        // Normalizado como lo guarda Trabajador: «12.345.678-Z» y «12345678Z» son el mismo documento.
        var dniNormalizado = CriterioDocumentoIdentidadTrabajador.Clasificar(request.Dni).Normalizado ?? request.Dni;
        if (await repositorio.ExisteConDniAsync(dniNormalizado, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Trabajador.DniDuplicado", "Ya existe un trabajador con este DNI."));

        var trabajador = request.EmpresaId is not null
            ? Trabajador.DeEmpresa(
                request.EmpresaId.Value, request.Nombre, request.Apellidos, request.Dni,
                request.FechaNacimiento, request.Email, request.Observaciones, request.Alias, request.Telefono, request.Puesto)
            : Trabajador.DeSubcontrata(
                request.SubcontrataId!.Value, request.Nombre, request.Apellidos, request.Dni,
                request.FechaNacimiento, request.Email, request.Observaciones, request.Alias, request.Telefono, request.Puesto);

        trabajador.CambiarTipoContratacion(request.TipoContratacion);

        // El alcance de gestión sobre la Empresa se comprobó arriba; el catálogo tiene que ser el de esa Empresa. Elegir un
        // Oficio copia su nombre en el Puesto (Trabajador.FijarDelegacionEmpresaYOficio).
        // Sin renovar la versión del catálogo: este comando puede correr como cae_app_aprovisionamiento, que solo lo lee
        // (ver AsignacionDelegacionEmpresaYOficio.Aplicar).
        if (request.EmpresaId is not null && (request.OficioEmpresaId is not null || request.DelegacionEmpresaId is not null))
        {
            var (asignacion, errorCatalogo) = await AsignacionDelegacionEmpresaYOficio.ResolverAsync(
                trabajador, request.DelegacionEmpresaId, request.OficioEmpresaId, catalogo, cancellationToken);
            if (errorCatalogo is not null)
                return Result.Fallo<Guid>(errorCatalogo);
            AsignacionDelegacionEmpresaYOficio.Aplicar(trabajador, asignacion!, renovarVersionDelCatalogo: false);
        }

        repositorio.Agregar(trabajador);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(trabajador.Id);
    }
}
