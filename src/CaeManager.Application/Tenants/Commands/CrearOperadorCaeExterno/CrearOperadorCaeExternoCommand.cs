using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Tenants.Commands.CrearOperadorCaeExterno;

/// <summary>
/// Alta de un Tenant Operador CAE externo (perfil <see cref="PerfilVocabularioTenant.Consultora"/>)
/// nuevo, con su primer Administrador — PD-A9 del diseño de aprovisionamiento de
/// Tenant. Antes de este Command el único sitio que creaba un Tenant con este perfil
/// era <c>DelegacionDemoSeeder</c> (seed de demo, no comando de producto): ninguna
/// Organización SPA externa real podía nacer con su propio Tenant propietario.
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
/// <para>
/// <b>Primer Administrador, en el mismo acto</b> (decisión del propietario,
/// 2026-10-08: «El Actor de Plataforma TALVEG indica correo y nombre del primer
/// Administrador en el mismo acto del alta»). La cuenta nace en el Tenant NUEVO —nunca
/// en el del Actor de Plataforma TALVEG ni en ningún Tenant beneficiario—, con el rol
/// Administrador y sin contraseña: el Command devuelve el token de activación con el
/// que su titular la establece. Aquí no hay Operación de por medio: es el alta del
/// Tenant propietario, y por eso este es el camino propio que
/// <see cref="RolesReservadosAlTenantDeOrigen"/> deja fuera de /usuarios. No existe,
/// a propósito, un Command que siembre un Administrador después sobre un Tenant ya
/// creado.
/// </para>
///
/// <para>
/// <b>Atomicidad</b>: el Tenant, su <c>ParametroSistema</c>, su operación raíz, la
/// cuenta, su rol y el token se escriben y se generan en una sola transacción
/// (<see cref="ITransaccionDeComando"/>); si la cuenta, el rol o el token fallan, no
/// queda ni el Tenant. El token se genera dentro, y dentro del ámbito del Tenant nuevo:
/// si fallara después de confirmar, quedaría un Operador CAE externo con un
/// Administrador al que nadie puede hacer llegar su enlace. Un correo que ya usa otra
/// cuenta —también la de otro Tenant, que quien da el alta no ve— devuelve
/// <see cref="CrearOperadorCaeExternoCommandHandler.PrimerAdministradorNoCreado"/>,
/// con el mismo texto para cualquier fallo de la cuenta.
/// </para>
/// </summary>
public record CrearOperadorCaeExternoCommand(
    string NombreTenantOperador,
    string EmailPrimerAdministrador,
    string NombrePrimerAdministrador) : ICommand<OperadorCaeExternoCreado>;

/// <param name="TenantId">El Tenant propietario del Operador CAE externo recién creado.</param>
/// <param name="PrimerAdministradorUsuarioId">La cuenta, del Tenant nuevo, con rol Administrador.</param>
/// <param name="TokenActivacion">
/// De un solo uso y ya codificado para ir en una URL: con él el primer Administrador
/// establece su contraseña. Este flujo no lo envía por correo; lo entrega quien da el alta.
/// </param>
public record OperadorCaeExternoCreado(Guid TenantId, Guid PrimerAdministradorUsuarioId, string TokenActivacion);

public class CrearOperadorCaeExternoCommandValidator : AbstractValidator<CrearOperadorCaeExternoCommand>
{
    /// <summary>La longitud de <c>Email</c> y <c>UserName</c> en las cuentas de Identity.</summary>
    public const int LongitudMaximaEmail = 256;

    public const int LongitudMaximaNombreAdministrador = 200;

    public CrearOperadorCaeExternoCommandValidator()
    {
        RuleFor(c => c.NombreTenantOperador)
            .NotEmpty().WithMessage("El nombre del Operador CAE externo es obligatorio.")
            .MaximumLength(Tenant.LongitudMaximaNombre);

        RuleFor(c => c.EmailPrimerAdministrador)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("El correo del primer Administrador es obligatorio.")
            .MaximumLength(LongitudMaximaEmail).WithMessage("El correo del primer Administrador es demasiado largo.")
            .EmailAddress().WithMessage("El correo del primer Administrador no tiene forma de correo.");

        RuleFor(c => c.NombrePrimerAdministrador)
            .NotEmpty().WithMessage("El nombre del primer Administrador es obligatorio.")
            .MaximumLength(LongitudMaximaNombreAdministrador)
            .WithMessage("El nombre del primer Administrador es demasiado largo.");
    }
}

public class CrearOperadorCaeExternoCommandHandler(
    ITenantRepository tenantRepositorio,
    IParametroSistemaRepository parametroSistemaRepositorio,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter,
    IUnitOfWork unitOfWork,
    IGestionCuentasUsuario cuentas,
    ITransaccionDeComando transaccion,
    ILogger<CrearOperadorCaeExternoCommandHandler> logger)
    : IRequestHandler<CrearOperadorCaeExternoCommand, Result<OperadorCaeExternoCreado>>
{
    /// <summary>
    /// Mismos valores por defecto que <c>CrearClienteDeleganteCommand</c> y
    /// <c>ParametroSistemaSeedData</c> — ver el comentario allí sobre por qué
    /// se repiten en vez de referenciarse (Application no puede depender de
    /// Infrastructure).
    /// </summary>
    private const int UmbralAmbarDiasPorDefecto = 30;
    private const int UmbralRojoDiasPorDefecto = 15;

    /// <summary>
    /// El mismo texto para cualquier fallo de la cuenta, el rol o el token: quien da
    /// el alta no ve las cuentas de otros Tenants, y el motivo de Identity («ese correo
    /// ya está en uso») le diría que existen.
    /// </summary>
    public static readonly Error PrimerAdministradorNoCreado = Error.Crear(
        "OperadorCaeExterno.PrimerAdministradorNoCreado",
        "No pudimos crear la cuenta del primer Administrador. Revisa el correo. No se ha creado el Operador CAE externo.");

    public static readonly Error DatosPrimerAdministradorObligatorios = Error.Crear(
        "OperadorCaeExterno.DatosPrimerAdministradorObligatorios",
        "El correo y el nombre del primer Administrador son obligatorios.");

    public async Task<Result<OperadorCaeExternoCreado>> Handle(CrearOperadorCaeExternoCommand request, CancellationToken cancellationToken)
    {
        // GLOBAL, y no acotada, por el mismo motivo que CrearClienteDeleganteCommand:
        // el tenant objetivo todavía no existe, no hay nada a lo que acotar
        // la autoridad.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<OperadorCaeExternoCreado>(Error.Crear("OperadorCaeExterno.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeGlobalmenteAsync(usuarioId.Value, cancellationToken))
            return Result.Fallo<OperadorCaeExternoCreado>(Error.Crear(
                "OperadorCaeExterno.SinPermiso", "Solo la administración de plataforma puede dar de alta un Operador CAE externo."));

        // El validador ya lo exige; se repite porque sin primer Administrador el
        // Tenant nacería sin nadie que pueda entrar, y este handler no lo da por
        // bueno aunque alguien lo invoque sin pasar por la tubería de validación.
        if (string.IsNullOrWhiteSpace(request.EmailPrimerAdministrador) || string.IsNullOrWhiteSpace(request.NombrePrimerAdministrador))
            return Result.Fallo<OperadorCaeExternoCreado>(DatosPrimerAdministradorObligatorios);

        // Trim antes de comprobar, no después: Tenant.EstablecerNombre recorta
        // el nombre al construirse, así que sin este Trim aquí " Operador Sur "
        // pasaría la comprobación aunque ya exista "Operador Sur" (hallazgo de
        // Codex). No cierra la ventana de concurrencia — falta un índice único
        // en TenantConfiguration, gap preexistente compartido con
        // CrearClienteDeleganteCommand, fuera de alcance de este incremento.
        var nombreNormalizado = request.NombreTenantOperador.Trim();
        if (await tenantRepositorio.ExisteConNombreAsync(nombreNormalizado, cancellationToken))
            return Result.Fallo<OperadorCaeExternoCreado>(Error.Crear("OperadorCaeExterno.NombreDuplicado", "Ya existe una organización con este nombre."));

        // Perfil Consultora: cómo el tenant se ve a sí mismo (lista de
        // Empresas gestionadas, no "Mi empresa" singular) — DDL-072, capa de
        // presentación pura, declarado aquí explícitamente y nunca inferido.
        var tenantOperador = new Tenant(nombreNormalizado, PerfilVocabularioTenant.Consultora);
        tenantOperador.HabilitarComoOperadorCaeExterno();

        // La cuenta nace en el Tenant NUEVO, con su TenantId explícito (a
        // ApplicationUser no lo sella el interceptor de tenant): nunca en el Tenant
        // de quien da el alta. Los tres últimos argumentos, en el orden del record: sin
        // Coordinador CAE, sin Cliente empresarial vinculado (es un Administrador, no una
        // cuenta de portal) y sin el permiso sensible, que solo concede un Administrador
        // a otro.
        var primerAdministrador = new NuevaCuentaUsuario(
            request.EmailPrimerAdministrador.Trim(),
            request.NombrePrimerAdministrador.Trim(),
            tenantOperador.Id,
            null,
            null,
            false);

        Guid primerAdministradorId = default;
        var tokenActivacion = string.Empty;
        var escribiendoCuenta = false;
        Result escrita;

        // Ámbito explícito contra su PROPIO Id — mismo mecanismo que
        // CrearClienteDeleganteCommand y DelegacionDemoSeeder.AprovisionarTenantClienteAsync
        // (Project-Hydra-Negocio/tecnico/docs/MULTITENANCY.md § 8.4). La transacción se
        // abre DENTRO del ámbito y no al revés: las coordenadas de sesión de la RLS
        // (app.tenant_id) se fijan al abrir la conexión, y la transacción la mantiene
        // abierta hasta el final. Abierta fuera, la cuenta se escribiría con el Tenant
        // de quien da el alta como Tenant activo y la política de alta de cuentas la
        // rechazaría.
        using (AmbitoTenantExplicito.Establecer(tenantOperador.Id))
        {
            try
            {
                escrita = await transaccion.EjecutarAsync(async ct =>
                {
                    // Cada reintento de la estrategia de ejecución empieza de cero.
                    primerAdministradorId = default;
                    tokenActivacion = string.Empty;
                    escribiendoCuenta = false;

                    tenantRepositorio.Agregar(tenantOperador);

                    // Todo tenant necesita esta fila: ObtenerKpisDashboardQuery la lee
                    // con SingleAsync() y falla si no existe.
                    parametroSistemaRepositorio.Agregar(new ParametroSistema(UmbralAmbarDiasPorDefecto, UmbralRojoDiasPorDefecto));

                    // Todo tenant nace con su operación raíz — ancla de sus carteras
                    // internas, igual que en CrearClienteDeleganteCommand. Aquí
                    // además es la ÚNICA operación que este tenant tendrá hasta que
                    // él mismo delegue Clientes hacia sí, porque no nace con ninguna
                    // delegación entrante.
                    await asignacionesWriter.AsegurarOperacionRaizAsync(
                        tenantOperador.Id, tenantOperador.CreadoEnUtc, ct);

                    await unitOfWork.SaveChangesAsync(ct);

                    escribiendoCuenta = true;

                    var creada = await cuentas.CrearAsync(primerAdministrador, ct);
                    if (creada.EsFallido)
                        return Result.Fallo(PrimerAdministradorNoCreado);
                    primerAdministradorId = creada.Valor;

                    var rol = await cuentas.AsignarRolAsync(primerAdministradorId, AutoridadSobreCuentas.Administrador, ct);
                    if (rol.EsFallido)
                        return Result.Fallo(PrimerAdministradorNoCreado);

                    var token = await cuentas.GenerarTokenActivacionAsync(primerAdministradorId, ct);
                    if (token.EsFallido)
                        return Result.Fallo(PrimerAdministradorNoCreado);
                    tokenActivacion = token.Valor;

                    return Result.Exito();
                }, cancellationToken);
            }
            catch (DbUpdateException ex) when (escribiendoCuenta)
            {
                // El índice único del nombre de usuario, cuando el validador de Identity
                // no llegó a ver la otra cuenta. La transacción ya se deshizo y el
                // contexto quedó vacío (ITransaccionDeComando): no existe ni el Tenant.
                // Se captura fuera de la lambda a propósito: dentro le quitaría a la
                // estrategia de ejecución los fallos transitorios que sí reintenta.
                //
                // Quien da el alta lee el mismo mensaje neutro sea cual sea la causa, para no
                // revelar cuentas de otros Tenants; por eso la causa queda en el registro. Un
                // rechazo de la RLS o de una clave foránea llega por aquí igual que el índice
                // único, y sin esto nadie lo vería. Sin el correo: es un dato personal.
                logger.LogError(
                    ex, "El alta del Operador CAE externo no pudo escribir la cuenta de su primer Administrador; no se creó el Tenant.");
                return Result.Fallo<OperadorCaeExternoCreado>(PrimerAdministradorNoCreado);
            }
        }

        if (escrita.EsFallido)
            return Result.Fallo<OperadorCaeExternoCreado>(escrita.Error);

        return Result.Exito(new OperadorCaeExternoCreado(tenantOperador.Id, primerAdministradorId, tokenActivacion));
    }
}
