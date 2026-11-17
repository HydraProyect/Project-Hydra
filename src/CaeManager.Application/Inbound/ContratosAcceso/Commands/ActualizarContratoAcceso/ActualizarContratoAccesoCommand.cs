using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.ContratosAcceso.Commands.ActualizarContratoAcceso;

/// <summary>
/// Solo cambian el fin contratado y las notas: las coordenadas y el inicio definen el contrato. Solo se edita un contrato
/// abierto, y el fin solo en uno Activo (<c>ContratoAcceso.Actualizar</c>); el de un Suspendido se amplía con
/// <c>RenovarContratoAccesoCommand</c>. Un cambio del fin queda en el historial de vigencia como edición (B-05).
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record ActualizarContratoAccesoCommand(Guid Id, DateOnly? FechaFin, string? Notas, Guid Version = default) : ICommand, IComandoInbound;

public class ActualizarContratoAccesoCommandValidator : AbstractValidator<ActualizarContratoAccesoCommand>
{
    public ActualizarContratoAccesoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Notas).MaximumLength(ContratoAcceso.LongitudMaximaNotas);
    }
}

public class ActualizarContratoAccesoCommandHandler(
    IContratoAccesoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService, TimeProvider reloj, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ActualizarContratoAccesoCommand, Result>
{
    public async Task<Result> Handle(ActualizarContratoAccesoCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo(AutorizacionInboundErrores.SinCapacidad);

        var contrato = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("ContratoAcceso.NoEncontrado", "El contrato de acceso no existe o no tienes acceso."));

        if (ConcurrenciaOptimista.Verificar(contrato, request.Version, "este contrato de acceso") is { } conflicto)
            return Result.Fallo(conflicto);

        // Solo el cambio del fin deja fila en el historial de vigencia, que registra quién lo hizo: editar solo las notas no
        // necesita usuario (el interceptor de auditoría ya identifica al autor por su cuenta).
        var usuarioId = Guid.Empty;
        if (request.FechaFin != contrato.FechaFin)
        {
            if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuario)
                return Result.Fallo(ContratoAccesoErrores.SinUsuario);
            usuarioId = usuario;
        }

        try
        {
            contrato.Actualizar(request.FechaFin, request.Notas, usuarioId, reloj.GetUtcNow().UtcDateTime);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("ContratoAcceso.FechaFinInvalida", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fallo(ContratoAccesoErrores.TransicionNoPermitida(ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
