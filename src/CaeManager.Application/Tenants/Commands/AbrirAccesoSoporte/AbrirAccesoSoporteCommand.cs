using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Commands.AbrirAccesoSoporte;

/// <summary>
/// Abre una ventana de acceso de soporte al tenant de un cliente: es el paso
/// que convierte una delegación aprovisionada —y apagada— en acceso real.
///
/// Exige motivo y duración porque es Hydra entrando en datos de los que
/// responde el cliente. Ese par es lo que permite responder después "entramos
/// del día X al Y por la incidencia Z".
/// </summary>
/// <summary>
/// <paramref name="Rol"/> es con qué permisos se entra en el tenant del
/// cliente. <c>Consulta</c> —solo lectura— es lo que basta para reproducir la
/// mayoría de incidencias y lo que menos expone; los de escritura solo cuando
/// haga falta reproducir una acción, porque escribir en datos reales de un
/// cliente para diagnosticar es delicado.
/// </summary>
public record AbrirAccesoSoporteCommand(
    Guid DelegacionTenantId, string Motivo, int HorasDeVentana, string Rol = RolesSoporte.SoloLectura) : ICommand;

/// <summary>
/// Roles con los que puede entrar soporte. Coinciden con los que admite
/// <c>CrearAsignacionOperadorDelegadoCommandValidator</c>: Administrador y
/// DireccionCae quedan fuera a propósito — se opera dentro del alcance del
/// workspace, nunca con privilegios de administración sobre la organización
/// del cliente.
/// </summary>
public static class RolesSoporte
{
    public const string SoloLectura = "Consulta";

    public static readonly string[] Admitidos = [SoloLectura, "GestorCae", "CoordinadorCae"];
}

public class AbrirAccesoSoporteCommandValidator : AbstractValidator<AbrirAccesoSoporteCommand>
{
    /// <summary>
    /// DEC-43 (2026-09-02): el mismo techo de cuatro horas absolutas que rige
    /// <c>SesionPrivilegiada.VentanaMaxima</c> (plano 3) aplica aquí también.
    ///
    /// Esta vía es anterior a ese agregado — abre el acceso activando una
    /// <see cref="CaeManager.Domain.Tenants.DelegacionTenant"/> con
    /// <c>ActivarParaSoporte</c>, no una <c>SesionPrivilegiada</c> — pero sigue
    /// siendo un camino de producción en uso (<c>Delegaciones.razor</c>) para
    /// que un Actor de Plataforma entre en el tenant de un cliente. Por eso la
    /// regla de DEC-43 —ninguna activación privilegiada puntual de más de 4
    /// horas, sin excepción por vía— tiene que cumplirse también aquí.
    ///
    /// No se referencia la constante del otro agregado para no acoplar la
    /// feature <c>Tenants</c> al dominio de <c>Plataforma</c>: se fija el mismo
    /// valor por separado, y un test cruzado
    /// (<c>VentanaDePrivilegioCuatroHorasTests</c> en
    /// <c>CaeManager.Application.Tests</c>) falla si alguna vez divergen.
    /// </summary>
    public const int MaximoHorasDeVentana = 4;

    public AbrirAccesoSoporteCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty();

        RuleFor(c => c.Motivo)
            .NotEmpty().WithMessage("Indica por qué necesitas entrar en los datos de este cliente.")
            .MaximumLength(500);

        RuleFor(c => c.HorasDeVentana)
            .InclusiveBetween(1, MaximoHorasDeVentana)
            .WithMessage($"La ventana de acceso debe estar entre 1 y {MaximoHorasDeVentana} horas.");

        RuleFor(c => c.Rol)
            .Must(rol => RolesSoporte.Admitidos.Contains(rol))
            .WithMessage("Ese rol no está disponible para un acceso de soporte.");
    }
}

public class AbrirAccesoSoporteCommandHandler(
    IDelegacionTenantRepository repositorio,
    IAsignacionOperadorDelegadoRepository asignacionRepositorio,
    ITenantsQueryContext dbContext,
    IRegistroActividadSoporteRepository registroRepositorio,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<AbrirAccesoSoporteCommand, Result>
{
    public async Task<Result> Handle(AbrirAccesoSoporteCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await repositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);
        if (delegacion is null || delegacion.Proposito is not PropositoDelegacion.Soporte)
            return Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación de soporte."));

        // Solo desde el tenant marcado como plataforma. Se comprueba contra el
        // tenant de ORIGEN, nunca contra ITenantActual: ese refleja el
        // Delegated Workspace activo, así que alguien operando ya un tenant
        // ajeno no puede usarlo para abrirse acceso a otros.
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        var esPlataforma = tenantOrigenId is not null && await dbContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);

        if (!esPlataforma || delegacion.TenantConsultoraId != tenantOrigenId!.Value)
            return Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación de soporte."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Soporte.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        // 2FA obligatoria para abrir un acceso de soporte (P1-13 de
        // docs/business/MATURITY_REVIEW.md): quien entra en datos de un
        // cliente ajeno con una cuenta comprometida por contraseña sola es
        // el escenario que este acceso existe precisamente para poder
        // rastrear y contener.
        if (!await currentUserService.TieneDobleFactorActivoAsync())
            return Result.Fallo(Error.Crear(
                "Soporte.SinDobleFactor", "Activa la autenticación en dos pasos en tu cuenta antes de abrir un acceso de soporte."));

        var ahora = DateTime.UtcNow;
        try
        {
            delegacion.ActivarParaSoporte(request.Motivo, ahora.AddHours(request.HorasDeVentana), ahora);
        }
        catch (InvalidOperationException excepcion)
        {
            // La única InvalidOperationException alcanzable aquí es "ya está
            // vigente": la de Proposito ya la descarta la comprobación de
            // arriba. Se traduce a un Result legible en vez de reventar la
            // petición — mismo criterio que AbrirSesionPrivilegiadaCommandHandler.
            return Result.Fallo(Error.Crear("DelegacionTenant.YaVigente", excepcion.Message));
        }

        // Quien abre la ventana es quien va a entrar, así que se asigna aquí
        // mismo: abrir el acceso sin operador asignado dejaría la delegación
        // viva pero inservible, y obligaría a un segundo paso que nadie
        // entendería.
        //
        // Se rehace la asignación en vez de conservar la anterior porque el
        // rol puede cambiar entre visitas: una vez basta con leer y la
        // siguiente hace falta reproducir una acción.
        var asignacionPrevia = await asignacionRepositorio
            .ObtenerPorDelegacionYUsuarioAsync(delegacion.Id, usuarioId.Value, cancellationToken);

        if (asignacionPrevia is not null)
            asignacionRepositorio.Eliminar(asignacionPrevia);

        asignacionRepositorio.Agregar(new AsignacionOperadorDelegado(delegacion.Id, usuarioId.Value, request.Rol));

        // El registro pertenece al tenant VISITADO, no al de plataforma: es el
        // cliente quien debe poder consultar qué se hizo en sus datos. Sin
        // este ámbito explícito, el interceptor lo sellaría contra Hydra y el
        // cliente no lo vería nunca.
        using (AmbitoTenantExplicito.Establecer(delegacion.TenantClienteId))
        {
            registroRepositorio.Agregar(RegistroActividadSoporte.PorViaHeredada(
                usuarioId.Value, delegacion.Id, TipoActividadSoporte.AccesoConcedido,
                $"Ventana de {request.HorasDeVentana} hora(s). Motivo: {request.Motivo}"));

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Exito();
    }
}
