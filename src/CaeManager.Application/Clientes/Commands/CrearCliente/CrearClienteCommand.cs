using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Clientes.Commands.CrearCliente;

public record CrearClienteCommand(string RazonSocial, string Cif, bool EsCritico, string? Notas) : ICommand<Guid>;

public class CrearClienteCommandValidator : AbstractValidator<CrearClienteCommand>
{
    public CrearClienteCommandValidator()
    {
        RuleFor(c => c.RazonSocial)
            .NotEmpty().WithMessage("La razón social es obligatoria.")
            .MaximumLength(Empresa.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Empresa.LongitudMaximaRazonSocial} caracteres.");

        RuleFor(c => c.Cif)
            .NotEmpty().WithMessage("La identificación fiscal es obligatoria.")
            .Must(ValidadorIdentificacion.EsIdentificacionFiscalValida)
            .WithMessage(Empresa.MensajeIdentificacionFiscalInvalida);

        RuleFor(c => c.Notas)
            .MaximumLength(Empresa.LongitudMaximaNotas).WithMessage($"Las notas no pueden superar {Empresa.LongitudMaximaNotas} caracteres.");
    }

}

/// <summary>
/// F3b — reemplaza <c>IClienteRepository</c> por <c>IEmpresaRepository</c>:
/// desde la congelación, "crear un Cliente" es crear una Empresa
/// contraparte (<see cref="Empresa.CrearComoCliente"/>). La comprobación
/// de unicidad de RazonSocial/Cif pasa a ser global (contra todas las
/// Empresas, no solo contra los antiguos Clientes) porque el índice único
/// de la base ya es global desde F3a — un mensaje que dijera "ya existe un
/// Cliente" sería inexacto si la colisión es con una Empresa propia o una
/// ex-Subcontrata.
/// </summary>
public class CrearClienteCommandHandler(
    IEmpresaRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService,
    ITransaccionDeComando transaccion, IBloqueoCarteraUsuario bloqueoCartera, IDirectorioDestinosCartera directorio)
    : IRequestHandler<CrearClienteCommand, Result<Guid>>
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo
    // motivo que en AutorizacionEscrituraBehavior.
    private const string RolGestorCae = "GestorCae";

    public async Task<Result<Guid>> Handle(CrearClienteCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Cliente.RazonSocialDuplicada", "Ya existe una organización con esta razón social."));

        if (await repositorio.ExisteConCifAsync(request.Cif, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Cliente.CifDuplicado", "Ya existe una organización con este CIF."));

        // Un Cliente creado por un Gestor CAE lo tiene a él como Gestor CAE de
        // referencia (Empresa.EjecutivoUsuarioId). Es solo una referencia —enrutado
        // de WhatsApp, avisos, columna de la lista—: no escribe ninguna Asignación
        // de Cartera ni concede alcance (D-7, 2026-10-02). El Gestor CAE que crea el
        // Cliente ya lo alcanza si tiene la cartera del Tenant entero, y si no la
        // tiene, crearlo no se la da. El resto de roles que pueden crear Clientes
        // (Administrador, DireccionCae, CoordinadorCae) lo dejan sin referencia
        // hasta indicarla explícitamente.
        var rol = await currentUserService.ObtenerRolEfectivoAsync();
        var ejecutivoUsuarioId = rol == RolGestorCae ? await currentUserService.ObtenerUsuarioActualIdAsync() : null;

        Guid empresaId = default;
        var resultado = await transaccion.EjecutarAsync(async ct =>
        {
            // Revisión Codex de FS-25: con el candado compartido de cartera, el alta espera a una
            // desactivación en curso de este Gestor CAE y, si la cuenta quedó desactivada, no la
            // deja como referencia del Cliente empresarial.
            if (ejecutivoUsuarioId is { } gestorId)
            {
                await bloqueoCartera.BloquearCompartidoAsync([gestorId], ct);
                if (await directorio.ObtenerAsync(gestorId, ct) is not { Activa: true })
                    return Result.Fallo(ReglaDestinoCarteraCliente.Inactivo);
            }

            var empresa = Empresa.CrearComoCliente(request.RazonSocial, request.Cif, request.EsCritico, request.Notas, ejecutivoUsuarioId);
            repositorio.Agregar(empresa);
            empresaId = empresa.Id;

            await unitOfWork.SaveChangesAsync(ct);
            return Result.Exito();
        }, cancellationToken);

        return resultado.EsExitoso ? Result.Exito(empresaId) : Result.Fallo<Guid>(resultado.Error);
    }
}
