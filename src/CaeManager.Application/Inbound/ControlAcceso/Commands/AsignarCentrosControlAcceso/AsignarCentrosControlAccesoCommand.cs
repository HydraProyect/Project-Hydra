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
/// lista entera: los Centros que no vienen se retiran, y una lista vacía deja a la persona sin ningún Centro. Lo hace quien tiene
/// <see cref="CapacidadInbound.GestionarInbound"/> (el Gestor CAE, o un Operador CAE externo por su Asignación de Cartera).
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
        if (centroIds.Count > 0)
        {
            var alcance = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);
            var existentes = await centros.Centros
                .Where(c => centroIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            if (existentes.Count != centroIds.Count || (alcance is not null && centroIds.Any(id => !alcance.Contains(id))))
                return Result.Fallo(ControlAccesoErrores.CentroNoEncontrado);
        }

        var actuales = await repositorio.ObtenerDeUsuarioAsync(request.UsuarioId, cancellationToken);
        var aRetirar = actuales.Where(a => !centroIds.Contains(a.CentroId)).ToList();
        var yaAsignados = actuales.Select(a => a.CentroId).ToHashSet();
        var aAnadir = centroIds.Where(id => !yaAsignados.Contains(id)).ToList();

        if (aRetirar.Count == 0 && aAnadir.Count == 0)
            return Result.Exito();

        foreach (var asignacion in aRetirar)
            repositorio.Retirar(asignacion);
        foreach (var centroId in aAnadir)
            repositorio.Agregar(new UsuarioControlAccesoCentro(request.UsuarioId, centroId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
