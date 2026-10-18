using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;

public record EditarFichaTrabajadorCommand(Guid Id, Guid Version,
    string? NumeroAfiliacionSeguridadSocial, string? Puesto, DateOnly? FechaNacimiento,
    string? Nacionalidad, string? TipoVinculo, DateOnly? FechaAltaEmpresa, DateOnly? FechaBajaEmpresa,
    string? FiguraEnRnt, string? TipoPermisoTrabajo, DateOnly? CaducidadPermisoTrabajo,
    string? MotivoPermisoTrabajo,
    TipoContratacion? TipoContratacion = null,
    ContactoEmergenciaTrabajador? ContactoEmergencia = null) : ICommand;

/// <summary>
/// El contacto de emergencia que se fija con la ficha. En <see cref="EditarFichaTrabajadorCommand"/>, <c>null</c> (el
/// objeto entero) = no se cambia: quien edita la ficha sin conocer el campo no lo borra. Dentro, un campo vacío o
/// <c>null</c> = sin ese dato.
/// </summary>
public record ContactoEmergenciaTrabajador(string? Nombre, string? Telefono);

public class EditarFichaTrabajadorCommandValidator : AbstractValidator<EditarFichaTrabajadorCommand>
{
    public EditarFichaTrabajadorCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Version).NotEmpty();
        RuleFor(c => c.NumeroAfiliacionSeguridadSocial).Must(Trabajador.NumeroAfiliacionValido)
            .WithMessage("Introduce un número de afiliación de 11 o 12 dígitos.");
        RuleFor(c => c.Puesto).MaximumLength(Trabajador.LongitudMaximaPuesto);
        RuleFor(c => c.Nacionalidad).MaximumLength(Trabajador.LongitudMaximaCampoFicha);
        RuleFor(c => c.TipoVinculo).MaximumLength(Trabajador.LongitudMaximaCampoFicha);
        RuleFor(c => c.TipoContratacion).IsInEnum().WithMessage("Elige cuenta ajena, autónomo o voluntario.");
        RuleFor(c => c.FiguraEnRnt).MaximumLength(Trabajador.LongitudMaximaCampoFicha);
        RuleFor(c => c.TipoPermisoTrabajo).MaximumLength(Trabajador.LongitudMaximaCampoFicha);
        RuleFor(c => c.MotivoPermisoTrabajo).MaximumLength(Trabajador.LongitudMaximaCampoFicha);
        When(c => c.ContactoEmergencia is not null, () =>
        {
            // Sobre el nombre recortado, como lo guarda Trabajador.FijarContactoEmergencia: los espacios de los
            // extremos no cuentan.
            RuleFor(c => c.ContactoEmergencia!.Nombre)
                .Must(n => n is null || n.Trim().Length <= Trabajador.LongitudMaximaContactoEmergenciaNombre)
                .WithMessage($"El contacto de emergencia no puede superar {Trabajador.LongitudMaximaContactoEmergenciaNombre} caracteres.");
            RuleFor(c => c.ContactoEmergencia!.Telefono).Must(Trabajador.TelefonoEmergenciaValido)
                .WithMessage($"Introduce un teléfono de {Trabajador.MinimoDigitosTelefonoEmergencia} a {Trabajador.MaximoDigitosTelefonoEmergencia} dígitos, con «+» delante si es internacional.");
        });
        RuleFor(c => c.FechaBajaEmpresa)
            .Must((c, baja) => !baja.HasValue || !c.FechaAltaEmpresa.HasValue || baja >= c.FechaAltaEmpresa)
            .WithMessage("La baja no puede ser anterior al alta en la empresa.");
    }
}

public class EditarFichaTrabajadorCommandHandler(ITrabajadorRepository repositorio,
    IAlcanceDatosService alcanceDatos, IPoliticaNotaInterna politicaNotaInterna, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarFichaTrabajadorCommand, Result>
{
    public async Task<Result> Handle(EditarFichaTrabajadorCommand request, CancellationToken cancellationToken)
    {
        var trabajador = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (trabajador is null || !await alcanceDatos.TrabajadorVisibleAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));
        if (trabajador.EstaAnonimizado)
            return Result.Fallo(Error.Crear("Trabajador.Anonimizado",
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse."));
        if (ConcurrenciaOptimista.Verificar(trabajador, request.Version, "este trabajador") is { } conflicto)
            return Result.Fallo(conflicto);

        trabajador.ActualizarFicha(request.NumeroAfiliacionSeguridadSocial, request.Puesto,
            request.FechaNacimiento, request.Nacionalidad, request.TipoVinculo, request.FechaAltaEmpresa,
            request.FechaBajaEmpresa, request.FiguraEnRnt, request.TipoPermisoTrabajo,
            request.CaducidadPermisoTrabajo, request.MotivoPermisoTrabajo);
        // null = no se cambia: quien edita la ficha sin conocer el campo no devuelve a un voluntario a cuenta ajena.
        if (request.TipoContratacion is { } tipoContratacion)
            trabajador.CambiarTipoContratacion(tipoContratacion);
        // Mismo criterio: null = no se cambia. El alcance es el de arriba (TrabajadorVisibleAsync); el rol con
        // escritura lo exige AutorizacionEscrituraBehavior antes de llegar aquí. Y quien no puede leer el contacto
        // (la política de ObtenerTrabajadorPorIdQuery) tampoco lo cambia: se ignora como si viniera null, para que
        // nadie borre ni pise a ciegas un dato que el servidor no le enseña.
        if (request.ContactoEmergencia is { } contacto && await politicaNotaInterna.PuedeLeerAsync(cancellationToken))
            trabajador.FijarContactoEmergencia(contacto.Nombre, contacto.Telefono);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
