using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Integraciones.Commands.GuardarConexionPlataformaCae;

/// <summary>
/// Alta de una conexión del Tenant propietario actual con una plataforma CAE de terceros, o rotación
/// de su credencial si ya existe una con ese nombre y esa plataforma. El Tenant nunca llega en el
/// Command: es el del contexto, porque una conexión solo opera con las credenciales del Tenant
/// propietario.
///
/// <para>
/// Solo un Administrador del propio Tenant propietario: es la llave contractual del Tenant con un
/// tercero, y el rol efectivo descarta a un Gestor CAE del Operador CAE externo en un Workspace
/// operativo derivado. <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/> deniega toda
/// Sesión Privilegiada y <see cref="IEscrituraDeDatosDeCredencial"/> exige el segundo factor.
/// </para>
/// </summary>
public record GuardarConexionPlataformaCaeCommand(Guid ProveedorPlataformaCaeId, string Nombre, string SecretoApi)
    : ICommand<Guid>, IEscrituraDeDatosDeCredencial
{
    public override string ToString() => nameof(GuardarConexionPlataformaCaeCommand);
}

public class GuardarConexionPlataformaCaeCommandValidator : AbstractValidator<GuardarConexionPlataformaCaeCommand>
{
    public GuardarConexionPlataformaCaeCommandValidator()
    {
        RuleFor(c => c.ProveedorPlataformaCaeId).NotEmpty();
        RuleFor(c => c.Nombre).NotEmpty().MaximumLength(ConexionPlataformaCae.LongitudMaximaNombre);
        RuleFor(c => c.SecretoApi).NotEmpty().MaximumLength(CredencialConexionPlataformaCae.LongitudMaximaSecreto);
    }
}

public class GuardarConexionPlataformaCaeCommandHandler(
    IConexionPlataformaCaeRepository conexiones,
    IProveedorPlataformaCaeRepository proveedores,
    ICurrentUserService usuarioActual,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarConexionPlataformaCaeCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(GuardarConexionPlataformaCaeCommand request, CancellationToken cancellationToken)
    {
        if (await usuarioActual.ObtenerRolEfectivoAsync() != "Administrador")
            return Result.Fallo<Guid>(Error.Crear(
                "ConexionPlataformaCae.SinPermiso", "Solo un Administrador puede guardar esta conexión."));

        if (await proveedores.ObtenerPorIdAsync(request.ProveedorPlataformaCaeId, cancellationToken) is null)
            return Result.Fallo<Guid>(Error.Crear("ConexionPlataformaCae.SinPlataforma", "No encontramos esta plataforma."));

        var conexion = await conexiones.ObtenerPorNombreAsync(request.Nombre.Trim(), cancellationToken);
        if (conexion is not null && conexion.ProveedorPlataformaCaeId != request.ProveedorPlataformaCaeId)
            return Result.Fallo<Guid>(Error.Crear(
                "ConexionPlataformaCae.NombreEnUso", "Ya hay otra conexión con ese nombre en otra plataforma."));

        if (conexion is null)
        {
            conexion = new ConexionPlataformaCae(request.ProveedorPlataformaCaeId, request.Nombre);
            conexiones.Agregar(conexion);
        }

        var credencial = await conexiones.ObtenerCredencialAsync(conexion.Id, cancellationToken);
        if (credencial is null)
            conexiones.AgregarCredencial(new CredencialConexionPlataformaCae(conexion.Id, request.SecretoApi, DateTime.UtcNow));
        else
            credencial.Rotar(request.SecretoApi, DateTime.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return conexion.Id;
    }
}
