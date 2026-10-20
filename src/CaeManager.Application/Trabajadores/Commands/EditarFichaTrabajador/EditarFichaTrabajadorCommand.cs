using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
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
    ContactoEmergenciaTrabajador? ContactoEmergencia = null,
    DelegacionEmpresaYOficioDeTrabajador? DelegacionEmpresaYOficio = null,
    MarcaExtranjeroCobrableTrabajador? ExtranjeroCobrable = null) : ICommand;

/// <summary>
/// La marca «Trabajador extranjero (cobrable)» que se fija con la ficha (decisión de Chris, 2026-10-20). En
/// <see cref="EditarFichaTrabajadorCommand"/>, <c>null</c> (el objeto entero) = no se cambia. Dentro, <c>Marca</c> en
/// <c>null</c> quita la marca a mano y vuelve a decidir el documento (<see cref="CriterioExtranjeroCobrable"/>).
/// </summary>
public record MarcaExtranjeroCobrableTrabajador(bool? Marca);

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
    ICatalogoTrabajadoresEmpresaRepository catalogo,
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

        // La Delegación y el Oficio se validan antes de tocar la entidad: si fallan, el comando responde el error sin dejar
        // el Trabajador rastreado a medio cambiar (el DbContext del circuito lo guardaría con el siguiente comando).
        // null = no se cambian, y los mismos Ids que ya tiene tampoco cuentan como cambio: quien ve al Trabajador solo por
        // una Asignación (sin gestionar su Empresa) puede guardar el resto de la ficha. Solo un Trabajador de Empresa tiene
        // Delegación y Oficio, y quien los cambia gestiona esa Empresa.
        AsignacionDelegacionEmpresaYOficio.Resuelta? asignacion = null;
        if (request.DelegacionEmpresaYOficio is { } delegacionYOficio
            && (delegacionYOficio.DelegacionEmpresaId != trabajador.DelegacionEmpresaId
                || delegacionYOficio.OficioEmpresaId != trabajador.OficioEmpresaId))
        {
            if (trabajador.EmpresaId is not { } empresaId)
                return Result.Fallo(Error.Crear("Trabajador.SinEmpresa", "Solo un trabajador de empresa tiene delegación y oficio."));
            if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(empresaId, cancellationToken))
                return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));
            (asignacion, var errorCatalogo) = await AsignacionDelegacionEmpresaYOficio.ResolverAsync(trabajador,
                delegacionYOficio.DelegacionEmpresaId, delegacionYOficio.OficioEmpresaId, catalogo, cancellationToken);
            if (errorCatalogo is not null)
                return Result.Fallo(errorCatalogo);
        }

        // La marca de extranjero cobrable decide qué cobra el Operador CAE (decisión de Chris, 2026-10-20): cambiarla
        // exige alcance de GESTIÓN sobre la Empresa o Subcontrata del Trabajador, no basta con verlo. Mismo valor = no
        // es un cambio, para que quien solo lo ve pueda guardar el resto de la ficha.
        var cambiaMarcaExtranjero = request.ExtranjeroCobrable is { } marcaPedida
            && marcaPedida.Marca != trabajador.MarcaExtranjeroCobrable
            && await politicaNotaInterna.PuedeLeerAsync(cancellationToken);
        if (cambiaMarcaExtranjero && !await GestionaElEmpleadorAsync(trabajador, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.ExtranjeroCobrableSinGestion",
                "Solo quien gestiona la empresa de este trabajador puede cambiar si es extranjero cobrable."));

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
        // Con la misma política que el contacto de emergencia (el Usuario de Cliente ni la lee ni la cambia), y
        // null = no se cambia; el alcance de gestión se ha comprobado arriba.
        if (cambiaMarcaExtranjero)
            trabajador.FijarMarcaExtranjeroCobrable(request.ExtranjeroCobrable!.Marca);
        // Se aplica después de ActualizarFicha porque elegir un Oficio copia su nombre en el Puesto.
        if (asignacion is not null)
            AsignacionDelegacionEmpresaYOficio.Aplicar(trabajador, asignacion, renovarVersionDelCatalogo: true);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }

    private async Task<bool> GestionaElEmpleadorAsync(Domain.Trabajadores.Trabajador trabajador, CancellationToken cancellationToken) =>
        trabajador.EmpresaId is { } empresaId
            ? await alcanceDatos.EmpresaParaGestionVisibleAsync(empresaId, cancellationToken)
            : trabajador.SubcontrataId is { } subcontrataId
              && await alcanceDatos.SubcontrataParaGestionVisibleAsync(subcontrataId, cancellationToken);
}
