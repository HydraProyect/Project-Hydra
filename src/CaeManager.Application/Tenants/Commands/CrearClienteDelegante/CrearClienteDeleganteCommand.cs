using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Commands.CrearClienteDelegante;

/// <summary>
/// Alta de un Cliente Delegante nuevo con su Delegated Workspace ya
/// operativo — cierra ADR-004 § 12.2 con la decisión mínima que el propio
/// hallazgo P0-7 de docs/business/MATURITY_REVIEW.md dejaba abierta como
/// aceptable: "solo Administrador de plataforma en v1". Sin este Command no
/// había ningún camino de producto para aprovisionar el segmento consultora
/// — <c>CrearDelegacionTenantCommand</c>/<c>CrearAsignacionOperadorDelegadoCommand</c>
/// ya existían pero solo los despachaba <c>DelegacionDemoSeeder</c>.
///
/// Une en una sola operación lo que el seeder de demo hace en varios pasos:
/// aprovisiona el Tenant, lo une a la Consultora (el tenant de origen de
/// quien ejecuta esto) con una <see cref="DelegacionTenant"/> activa, y dado
/// que una delegación sin ningún Operador Delegado sería inservible, asigna
/// aquí mismo a quien la crea. Quién más puede iniciarla (¿el propio Cliente
/// Delegante?) sigue abierto — ver ADR-004 § 12.2.
///
/// A propósito NO copia el catálogo de <c>TipoDocumento</c> del tenant #1
/// (a diferencia de <c>DelegacionDemoSeeder</c>): ese catálogo de 40+ tipos
/// es una conveniencia de demo, no necesariamente lo que un Cliente
/// Delegante real necesita — la pantalla /tipos-documento ya permite darlos
/// de alta uno a uno tras crear el tenant, sin forzar una copia completa que
/// nadie pidió (YAGNI, ver PROJECT.md).
/// </summary>
public record CrearClienteDeleganteCommand(string NombreTenantCliente) : ICommand<Guid>;

public class CrearClienteDeleganteCommandValidator : AbstractValidator<CrearClienteDeleganteCommand>
{
    public CrearClienteDeleganteCommandValidator()
    {
        RuleFor(c => c.NombreTenantCliente)
            .NotEmpty().WithMessage("El nombre del Cliente Delegante es obligatorio.")
            .MaximumLength(Tenant.LongitudMaximaNombre);
    }
}

public class CrearClienteDeleganteCommandHandler(
    ITenantRepository tenantRepositorio,
    IDelegacionTenantRepository delegacionRepositorio,
    IAsignacionOperadorDelegadoRepository asignacionRepositorio,
    IParametroSistemaRepository parametroSistemaRepositorio,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearClienteDeleganteCommand, Result<Guid>>
{
    /// <summary>
    /// Umbrales por defecto de alerta (ámbar/rojo, en días) para el
    /// ParametroSistema de un tenant nuevo — mismos valores que
    /// ParametroSistemaSeedData usa para el tenant #1. Se repiten aquí en
    /// vez de referenciarla porque esa clase vive en Infrastructure.Persistence.Seed,
    /// y Application no puede depender de Infrastructure — mismo motivo por
    /// el que CrearAsignacionOperadorDelegadoCommand repite los roles de
    /// Infrastructure.Identity.Roles en vez de importarlos.
    /// </summary>
    private const int UmbralAmbarDiasPorDefecto = 30;
    private const int UmbralRojoDiasPorDefecto = 15;

    /// <summary>
    /// Rol con el que queda operando quien crea el Cliente Delegante — mismo
    /// default que DelegacionDemoSeeder.RolOperadorDelegadoDemo. Gestor CAE
    /// y no Administrador/Consulta: puede trabajar de inmediato sobre el
    /// workspace sin heredar privilegios de administración de la
    /// organización del cliente (mismo criterio que CrearAsignacionOperadorDelegadoCommand).
    /// </summary>
    private const string RolInicialOperadorDelegado = "GestorCae";

    public async Task<Result<Guid>> Handle(CrearClienteDeleganteCommand request, CancellationToken cancellationToken)
    {
        // GLOBAL, y no acotada, porque el tenant objetivo TODAVÍA NO EXISTE: no
        // hay nada a lo que acotar la autoridad. Es la asimetría que el
        // inventario de A1 dejó fijada y que no debe diluirse.
        //
        // El tenant de origen sigue siendo la Consultora operadora —eso no
        // cambia—, pero ya no es la fuente de la autoridad. Y esto NO concede
        // capacidad genérica sobre DelegacionTenant: crear una delegación
        // arbitraria sobre un tenant existente sigue exigiendo ser Administrador
        // del Cliente Delegante (Incremento H, ADR-004 § 12.2).
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear("ClienteDelegante.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeGlobalmenteAsync(usuarioId.Value, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "ClienteDelegante.SinPermiso", "Solo la administración de plataforma puede dar de alta un Cliente Delegante."));

        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId is null)
            return Result.Fallo<Guid>(Error.Crear(
                "ClienteDelegante.SinTenantDeOrigen", "No pudimos determinar desde qué organización operas."));

        if (await tenantRepositorio.ExisteConNombreAsync(request.NombreTenantCliente, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("ClienteDelegante.NombreDuplicado", "Ya existe un tenant con este nombre."));

        // Un Cliente Delegante es una única empresa gestionada por la
        // consultora — vocabulario ClienteDirecto para él mismo (DDL-072: el
        // perfil es de cómo el tenant se ve a sí mismo, no de quién lo opera).
        var tenantCliente = new Tenant(request.NombreTenantCliente, PerfilVocabularioTenant.ClienteDirecto);

        // Ámbito explícito: la fila de ParametroSistema del tenant nuevo debe
        // sellarse contra SU PROPIO Id, no contra el tenant de origen de
        // quien ejecuta este Command — mismo mecanismo que
        // DelegacionDemoSeeder.AprovisionarTenantClienteAsync (docs/MULTITENANCY.md § 8.4).
        // Todo tenant necesita esta fila: ObtenerKpisDashboardQuery la lee
        // con SingleAsync() y falla si no existe.
        using (AmbitoTenantExplicito.Establecer(tenantCliente.Id))
        {
            tenantRepositorio.Agregar(tenantCliente);
            parametroSistemaRepositorio.Agregar(new ParametroSistema(UmbralAmbarDiasPorDefecto, UmbralRojoDiasPorDefecto));

            // Todo tenant nace con su operación raíz: es su derecho a operarse
            // a sí mismo y el ancla de sus carteras internas. Sin ella, el día
            // que este cliente internalice su gestión no habría dónde colgar la
            // cartera de su propio equipo.
            await asignacionesWriter.AsegurarOperacionRaizAsync(
                tenantCliente.Id, tenantCliente.CreadoEnUtc, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // Sin ámbito explícito aquí: DelegacionTenant/AsignacionOperadorDelegado
        // son catálogo global (Entity, no EntidadConTenant, ver sus propios
        // comentarios) — mismo tratamiento que AbrirAccesoSoporteCommand, que
        // tampoco lo usa para estas dos entidades.
        var delegacion = new DelegacionTenant(tenantOrigenId!.Value, tenantCliente.Id);
        delegacionRepositorio.Agregar(delegacion);

        // Sin esto, la delegación quedaría creada pero inservible: nadie
        // podría operarla hasta un segundo paso manual que además hoy no
        // tiene UI propia (Delegaciones.razor solo revoca/reactiva).
        asignacionRepositorio.Agregar(new AsignacionOperadorDelegado(delegacion.Id, usuarioId.Value, RolInicialOperadorDelegado));

        // Doble escritura de la delegación y de su primer operador. Este
        // comando escribe por repositorios directos, sin pasar por los comandos
        // que sirven de fachada, así que necesita su propia llamada — es
        // justamente el camino que un inventario superficial habría dejado
        // fuera.
        // La operación se usa por instancia, no se vuelve a buscar: acaba de
        // añadirse al contexto y todavía no está guardada, así que una consulta
        // no la encontraría y la cartera se perdería en silencio.
        var operacion = await asignacionesWriter.AbrirOperacionDelegadaAsync(
            tenantCliente.Id, tenantOrigenId.Value, delegacion.CreadoEnUtc, vigenciaHasta: null, cancellationToken);
        await asignacionesWriter.AbrirCarteraOperadorAsync(
            operacion, usuarioId.Value, RolInicialOperadorDelegado, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(tenantCliente.Id);
    }
}
