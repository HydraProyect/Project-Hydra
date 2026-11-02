using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;

/// <summary>Null retira al responsable: las conversaciones nuevas del buzón nacerán sin asignar.</summary>
public record FijarResponsableBuzonCommand(Guid ConexionId, Guid? ResponsableUsuarioId) : ICommand;

public class FijarResponsableBuzonCommandValidator : AbstractValidator<FijarResponsableBuzonCommand>
{
    public FijarResponsableBuzonCommandValidator()
    {
        RuleFor(c => c.ConexionId).NotEmpty();
        RuleFor(c => c.ResponsableUsuarioId).NotEqual(Guid.Empty);
    }
}

/// <summary>
/// Cambiar el responsable solo afecta a las conversaciones nuevas. El rol se mira en el handler y no solo en la
/// página: la pantalla no es una frontera de autorización. El responsable se valida con
/// <see cref="ResponsableBuzonElegible"/>, la misma regla que aplica la ingesta a cada conversación nueva.
///
/// <para>
/// El vínculo con la cuenta no tiene FK: el usuario vive en Identity. Por eso el nombramiento toma el candado
/// compartido de <see cref="IBloqueoCarteraUsuario"/> antes de leer la cuenta y lo retiene hasta confirmar, y la
/// baja de una cuenta pendiente toma el exclusivo antes de buscar vínculos: o la baja ve al responsable ya
/// fijado, o aquí ya no se encuentra la cuenta. El candado solo existe dentro de la transacción.
/// </para>
/// </summary>
public class FijarResponsableBuzonCommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    IAlcanceDatosService alcanceDatos,
    ResponsableBuzonElegible elegibilidad,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork,
    ITransaccionDeComando transaccion,
    IBloqueoCarteraUsuario bloqueoCartera)
    : IRequestHandler<FijarResponsableBuzonCommand, Result>
{
    private const string RolAdministrador = "Administrador";

    public async Task<Result> Handle(FijarResponsableBuzonCommand request, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerRolEfectivoAsync() != RolAdministrador)
            return Result.Fallo(Error.Crear(
                "Integraciones.Buzon.SoloAdministrador", "Solo el Administrador puede cambiar el responsable del buzón."));

        return await transaccion.EjecutarAsync(async ct =>
        {
            var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionId, ct);
            if (conexion is null
                || conexion.Proveedor != ProveedorIntegracion.Microsoft365
                || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, ct)
                || !await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, ct))
                return Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

            if (conexion.GestorPropietarioId is not null)
                return Result.Fallo(Error.Crear(
                    "Integraciones.Buzon.PersonalSinResponsable", "Un buzón personal responde siempre a su propietario."));

            if (request.ResponsableUsuarioId is { } responsableId)
            {
                await bloqueoCartera.BloquearCompartidoAsync([responsableId], ct);
                var motivo = await elegibilidad.EvaluarAsync(responsableId, conexion, ct);
                if (motivo is not null)
                    return Result.Fallo(ErrorDe(motivo.Value));
            }

            conexion.FijarResponsable(request.ResponsableUsuarioId);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Exito();
        }, cancellationToken);
    }

    private static Error ErrorDe(MotivoResponsableNoElegible motivo) => motivo switch
    {
        MotivoResponsableNoElegible.RolSinGestion => Error.Crear(
            "Integraciones.Buzon.ResponsableSinGestion",
            "Ese usuario no tiene un rol de gestión: no podría responder a este buzón."),
        MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa => Error.Crear(
            "Integraciones.Buzon.ResponsableSinCartera",
            "Esa persona no tiene una Asignación de Cartera sobre la Empresa de este buzón."),
        _ => Error.Crear(
            "Integraciones.Buzon.ResponsableNoValido", "No encontramos a ese usuario activo en tu organización."),
    };
}
