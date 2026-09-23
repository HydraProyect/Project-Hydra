using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Tenants.Commands.CrearOperadorCaeExterno;

/// <summary>
/// Alta de un Tenant Operador CAE externo (perfil <see cref="PerfilVocabularioTenant.Consultora"/>)
/// nuevo — PD-A9 del diseño de aprovisionamiento de Tenant. Cierra un hueco
/// real: hasta este Command, el único sitio que creaba un tenant con este
/// perfil era <c>DelegacionDemoSeeder</c> (seed de demo, no comando de
/// producto) — ninguna Organización SPA externa real (tipo ArcosSPA) podía
/// nacer con su propio Tenant propietario.
///
/// <b>A diferencia de <see cref="CrearClienteDelegante.CrearClienteDeleganteCommand"/>:</b>
/// el Tenant nuevo nace RAÍZ — sin <c>DelegacionTenant</c> ni
/// <c>AsignacionOperadorDelegado</c>. Un Operador CAE externo no está
/// delegado desde nadie: es él quien más tarde delegará Clientes Delegantes
/// hacia sí mismo, con <c>CrearClienteDeleganteCommand</c> ejecutado sobre
/// SU tenant de origen. Decisión de autorización idéntica a la de ese
/// comando (mismo precedente, ADR-004 § 12.2 P0-7, "solo Administrador de
/// plataforma en v1"): <see cref="IAutorizacionAdminPlataforma.PuedeGlobalmenteAsync"/>,
/// nunca <c>PuedeSobreTenantAsync</c>, porque el tenant objetivo todavía no
/// existe.
///
/// <b>Hueco conocido, fuera de alcance de este Command (UNKNOWN, ver PD-A8):</b>
/// no crea ningún usuario inicial en el Tenant nuevo. Sin un mecanismo de
/// activación de primer usuario (PD-A8, sin implementar todavía) el Tenant
/// queda aprovisionado pero sin nadie que pueda entrar — es la misma
/// asimetría que <c>CrearClienteDeleganteCommand</c> resuelve asignando al
/// propio ejecutor como Operador Delegado, y que aquí no aplica porque no
/// hay delegación de la que colgar esa asignación.
/// </summary>
public record CrearOperadorCaeExternoCommand(string NombreTenantOperador) : ICommand<Guid>;

public class CrearOperadorCaeExternoCommandValidator : AbstractValidator<CrearOperadorCaeExternoCommand>
{
    public CrearOperadorCaeExternoCommandValidator()
    {
        RuleFor(c => c.NombreTenantOperador)
            .NotEmpty().WithMessage("El nombre del Operador CAE externo es obligatorio.")
            .MaximumLength(Tenant.LongitudMaximaNombre);
    }
}

public class CrearOperadorCaeExternoCommandHandler(
    ITenantRepository tenantRepositorio,
    IParametroSistemaRepository parametroSistemaRepositorio,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearOperadorCaeExternoCommand, Result<Guid>>
{
    /// <summary>
    /// Mismos valores por defecto que <c>CrearClienteDeleganteCommand</c> y
    /// <c>ParametroSistemaSeedData</c> — ver el comentario allí sobre por qué
    /// se repiten en vez de referenciarse (Application no puede depender de
    /// Infrastructure).
    /// </summary>
    private const int UmbralAmbarDiasPorDefecto = 30;
    private const int UmbralRojoDiasPorDefecto = 15;

    public async Task<Result<Guid>> Handle(CrearOperadorCaeExternoCommand request, CancellationToken cancellationToken)
    {
        // GLOBAL, y no acotada, por el mismo motivo que CrearClienteDeleganteCommand:
        // el tenant objetivo todavía no existe, no hay nada a lo que acotar
        // la autoridad.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear("OperadorCaeExterno.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeGlobalmenteAsync(usuarioId.Value, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "OperadorCaeExterno.SinPermiso", "Solo la administración de plataforma puede dar de alta un Operador CAE externo."));

        // Trim antes de comprobar, no después: Tenant.EstablecerNombre recorta
        // el nombre al construirse, así que sin este Trim aquí " ArcosSPA "
        // pasaría la comprobación aunque ya exista "ArcosSPA" (hallazgo de
        // Codex). No cierra la ventana de concurrencia — falta un índice único
        // en TenantConfiguration, gap preexistente compartido con
        // CrearClienteDeleganteCommand, fuera de alcance de este incremento.
        var nombreNormalizado = request.NombreTenantOperador.Trim();
        if (await tenantRepositorio.ExisteConNombreAsync(nombreNormalizado, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("OperadorCaeExterno.NombreDuplicado", "Ya existe un tenant con este nombre."));

        // Perfil Consultora: cómo el tenant se ve a sí mismo (lista de
        // Empresas gestionadas, no "Mi empresa" singular) — DDL-072, capa de
        // presentación pura, declarado aquí explícitamente y nunca inferido.
        var tenantOperador = new Tenant(nombreNormalizado, PerfilVocabularioTenant.Consultora);
        tenantOperador.HabilitarComoOperadorCaeExterno();

        // Ámbito explícito contra su PROPIO Id — mismo mecanismo que
        // CrearClienteDeleganteCommand y DelegacionDemoSeeder.AprovisionarTenantClienteAsync
        // (docs/MULTITENANCY.md § 8.4). Todo tenant necesita esta fila:
        // ObtenerKpisDashboardQuery la lee con SingleAsync() y falla si no existe.
        using (AmbitoTenantExplicito.Establecer(tenantOperador.Id))
        {
            tenantRepositorio.Agregar(tenantOperador);
            parametroSistemaRepositorio.Agregar(new ParametroSistema(UmbralAmbarDiasPorDefecto, UmbralRojoDiasPorDefecto));

            // Todo tenant nace con su operación raíz — ancla de sus carteras
            // internas, igual que en CrearClienteDeleganteCommand. Aquí
            // además es la ÚNICA operación que este tenant tendrá hasta que
            // él mismo delegue Clientes hacia sí, porque no nace con ninguna
            // delegación entrante.
            await asignacionesWriter.AsegurarOperacionRaizAsync(
                tenantOperador.Id, tenantOperador.CreadoEnUtc, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Exito(tenantOperador.Id);
    }
}
