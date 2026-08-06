using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Centros.Commands.EditarCanalGestion;

/// <summary>
/// Edición de un acceso de gestión documental del Centro
/// (PLAN-EJECUCION-UX.md § 0.6, Lote 0-E). El <see cref="Tipo"/> no se edita:
/// Plataforma y Email no comparten campos, cambiarlo sería crear otro canal.
///
/// <see cref="CambiarCredenciales"/> existe porque la pantalla nunca muestra
/// el usuario/contraseña guardados (dato cifrado en reposo): sin este flag,
/// guardar cualquier otro cambio con los campos en blanco borraría en
/// silencio unas credenciales que el gestor no podía ver.
/// </summary>
public record EditarCanalGestionCommand(
    Guid Id,
    string EtiquetaProposito,
    string? NombrePlataforma,
    string? UrlAcceso,
    string? EmailsDestinatarios,
    string? NombreContacto,
    string? Notas,
    bool CambiarCredenciales = false,
    string? Usuario = null,
    string? Contrasena = null,
    Guid Version = default) : ICommand;

public class EditarCanalGestionCommandValidator : AbstractValidator<EditarCanalGestionCommand>
{
    public EditarCanalGestionCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.EtiquetaProposito)
            .NotEmpty().WithMessage("Describe para qué sirve este acceso (por ejemplo, «Gestión general»).")
            .MaximumLength(CanalGestionDocumental.LongitudMaximaEtiquetaProposito);

        RuleFor(c => c.NombrePlataforma).MaximumLength(CanalGestionDocumental.LongitudMaximaNombrePlataforma);
        RuleFor(c => c.UrlAcceso).MaximumLength(CanalGestionDocumental.LongitudMaximaUrlAcceso);
        RuleFor(c => c.EmailsDestinatarios).MaximumLength(CanalGestionDocumental.LongitudMaximaEmailsDestinatarios);
        RuleFor(c => c.NombreContacto).MaximumLength(CanalGestionDocumental.LongitudMaximaNombreContacto);
        RuleFor(c => c.Notas).MaximumLength(CanalGestionDocumental.LongitudMaximaNotas);
    }
}

public class EditarCanalGestionCommandHandler(
    ICanalGestionDocumentalRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarCanalGestionCommand, Result>
{
    public async Task<Result> Handle(EditarCanalGestionCommand request, CancellationToken cancellationToken)
    {
        var canal = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (canal is null || !await alcanceDatos.CentroVisibleAsync(canal.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("CanalGestion.NoEncontrado", "No encontramos este acceso."));

        if (ConcurrenciaOptimista.Verificar(canal, request.Version, "este acceso") is { } conflicto)
            return Result.Fallo(conflicto);

        if (canal.Tipo == TipoCanalGestion.Plataforma)
        {
            if (string.IsNullOrWhiteSpace(request.NombrePlataforma))
                return Result.Fallo(Error.Crear("CanalGestion.PlataformaRequerida", "El nombre de la plataforma es obligatorio."));

            canal.ActualizarPlataforma(request.EtiquetaProposito, request.NombrePlataforma, request.UrlAcceso, request.Notas);

            if (request.CambiarCredenciales)
                canal.ActualizarCredenciales(request.Usuario, request.Contrasena);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.EmailsDestinatarios))
                return Result.Fallo(Error.Crear("CanalGestion.EmailsRequeridos", "Los correos de destino son obligatorios."));

            canal.ActualizarEmail(request.EtiquetaProposito, request.EmailsDestinatarios, request.NombreContacto, request.Notas);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
