using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Configuracion.Commands.OlvidarVistaRecordada;

/// <summary>
/// Olvida la vista recordada del Usuario actual en una pantalla, en el Tenant
/// actual: el listado vuelve a abrirse como viene de fábrica. Si no había
/// ninguna, también es un éxito —lo pedido ya se cumple—, y lo mismo si otra
/// pestaña la borró a la vez. Solo toca la fila reservada de
/// <see cref="FiltroGuardado"/>; los filtros con nombre no se ven afectados.
/// </summary>
public record OlvidarVistaRecordadaCommand(string Pantalla) : ICommand, IComandoDeAutoservicio;

public class OlvidarVistaRecordadaCommandValidator : AbstractValidator<OlvidarVistaRecordadaCommand>
{
    public OlvidarVistaRecordadaCommandValidator() =>
        RuleFor(c => c.Pantalla).Must(p => PantallasConVistaRecordada.Admitidas.Contains(p))
            .WithMessage("Esa pantalla no recuerda la vista.");
}

public class OlvidarVistaRecordadaCommandHandler(
    ICurrentUserService currentUserService, ITenantActual tenantActual,
    IFiltroGuardadoRepository repositorio, IUnitOfWork unitOfWork, IDescarteCambiosPendientes descarteCambios)
    : IRequestHandler<OlvidarVistaRecordadaCommand, Result>
{
    public async Task<Result> Handle(OlvidarVistaRecordadaCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("VistaRecordada.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (tenantActual.TenantId is not { } tenantId || tenantId == Guid.Empty)
            return Result.Fallo(Error.Crear(
                "VistaRecordada.SinTenant",
                "No pudimos determinar en qué organización estás trabajando. Vuelve a iniciar sesión."));

        var vista = await repositorio.ObtenerVistaRecordadaAsync(usuarioId.Value, request.Pantalla, cancellationToken);
        if (vista is null)
            return Result.Exito();

        repositorio.Eliminar(vista);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otra pestaña la borró entre la lectura y este borrado: no queda nada que olvidar.
            // Se suelta lo rastreado para que el siguiente comando del circuito no lo reintente.
            descarteCambios.DescartarCambiosPendientes();
        }

        return Result.Exito();
    }
}
