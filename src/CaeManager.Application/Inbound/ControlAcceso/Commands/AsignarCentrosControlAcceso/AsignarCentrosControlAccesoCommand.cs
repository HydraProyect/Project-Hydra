using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ControlAcceso;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.ControlAcceso.Commands.AsignarCentrosControlAcceso;

/// <summary>
/// Fija los Centros en los que una persona con el rol «Control de acceso» opera la portería del Tenant TCE (AF-01). Reemplaza la
/// lista entera dentro del alcance de quien asigna: los Centros de su alcance que no vienen se retiran (una lista vacía los retira
/// todos), y las asignaciones a Centros fuera de su alcance no se tocan. Lo hace quien tiene
/// <see cref="CapacidadInbound.GestionarInbound"/>. La pantalla de asignación solo la abren, hoy, quienes gestionan cuentas
/// (Administrador y Dirección CAE) del Tenant TCE; el Operador CAE externo y el Gestor CAE no tienen pantalla desde la que
/// asignar (hueco declarado de AF-01).
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record AsignarCentrosControlAccesoCommand(Guid UsuarioId, IReadOnlyList<Guid> CentroIds) : ICommand, IComandoInbound;

public class AsignarCentrosControlAccesoCommandValidator : AbstractValidator<AsignarCentrosControlAccesoCommand>
{
    public AsignarCentrosControlAccesoCommandValidator()
    {
        RuleFor(c => c.UsuarioId).NotEmpty();
        RuleFor(c => c.CentroIds).NotNull();
        RuleForEach(c => c.CentroIds).NotEmpty();
    }
}

public static class ControlAccesoErrores
{
    /// <summary>La cuenta no existe en este Tenant, está desactivada o no tiene el rol: el mismo error para las tres.</summary>
    public static readonly Error UsuarioNoValido = Error.Crear(
        "ControlAcceso.UsuarioNoValido", "Esa persona no tiene el rol de Control de acceso en esta organización.");

    /// <summary>Otra persona cambió a la vez las asignaciones de esta cuenta.</summary>
    public static readonly Error CambioConcurrente = Error.Crear(
        "ControlAcceso.CambioConcurrente", "Otra persona acaba de cambiar los Centros de esta cuenta. Vuelve a abrirla y repite el cambio.");

    /// <summary>El Centro no existe en el Tenant o queda fuera del alcance de quien asigna: el mismo error para las dos.</summary>
    public static readonly Error CentroNoEncontrado = Error.Crear(
        "ControlAcceso.CentroNoEncontrado", "Alguno de los Centros no existe o no está entre los que puedes gestionar.");
}

public class AsignarCentrosControlAccesoCommandHandler(
    IUsuarioControlAccesoCentroRepository repositorio,
    IDirectorioUsuariosService directorioUsuarios,
    ICentrosQueryContext centros,
    IAlcanceDatosService alcanceDatos,
    ITenantActual tenantActual,
    IUnitOfWork unitOfWork,
    IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<AsignarCentrosControlAccesoCommand, Result>
{
    public async Task<Result> Handle(AsignarCentrosControlAccesoCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo(AutorizacionInboundErrores.SinCapacidad);

        if (tenantActual.TenantId is not { } tenantId
            || !await directorioUsuarios.EsCuentaActivaConRolAsync(
                request.UsuarioId, tenantId, AutoridadSobreCuentas.RolControlAcceso, cancellationToken))
            return Result.Fallo(ControlAccesoErrores.UsuarioNoValido);

        var centroIds = request.CentroIds.Distinct().ToList();

        // Solo Centros del Tenant activo y dentro del alcance de quien asigna (null = sin restricción). El filtro de Tenant y de
        // borrado lógico del contexto hace que un Id de otro Tenant «no exista».
        var alcance = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);
        if (centroIds.Count > 0)
        {
            var existentes = await centros.Centros
                .Where(c => centroIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            if (existentes.Count != centroIds.Count || (alcance is not null && centroIds.Any(id => !alcance.Contains(id))))
                return Result.Fallo(ControlAccesoErrores.CentroNoEncontrado);
        }

        var actuales = await repositorio.ObtenerDeUsuarioAsync(request.UsuarioId, cancellationToken);
        // Quien asigna solo retira lo que está dentro de su alcance: las asignaciones de Centros fuera de él se conservan intactas.
        var aRetirar = actuales
            .Where(a => !centroIds.Contains(a.CentroId) && (alcance is null || alcance.Contains(a.CentroId)))
            .ToList();
        var yaAsignados = actuales.Select(a => a.CentroId).ToHashSet();
        var aAnadir = centroIds.Where(id => !yaAsignados.Contains(id)).ToList();

        if (aRetirar.Count == 0 && aAnadir.Count == 0)
            return Result.Exito();

        foreach (var asignacion in aRetirar)
            repositorio.Retirar(asignacion);
        foreach (var centroId in aAnadir)
            repositorio.Agregar(new UsuarioControlAccesoCentro(request.UsuarioId, centroId));

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Dos guardados a la vez sobre la misma persona: el índice único detiene al segundo. Se pide recargar, no se rompe el circuito.
            return Result.Fallo(ControlAccesoErrores.CambioConcurrente);
        }

        return Result.Exito();
    }
}
