using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Usuarios.Commands.CrearUsuario;

/// <summary>
/// Da de alta una cuenta del Tenant activo, sin contraseña, con su rol, y devuelve
/// el token de activación con el que su titular la establece (P1-I2: antes lo hacía
/// <c>Usuarios.razor.cs</c> contra <c>UserManager</c>, con la autorización en la página).
///
/// <para>
/// <b>Quién</b>: Administrador o Dirección CAE, por su rol efectivo
/// (<see cref="AutoridadSobreCuentas"/>). Administrador y Dirección CAE solo se
/// conceden desde el Tenant de origen de quien da el alta
/// (<see cref="RolesReservadosAlTenantDeOrigen"/>, decisión del 2026-09-23).
/// </para>
///
/// <para>
/// <b>Permiso sensible</b>: solo lo concede un Administrador, y solo a otro
/// Administrador (Codex, HO-099-01): Dirección CAE también da altas, y sin esta
/// regla podría crear un Administrador con el permiso ya concedido.
/// </para>
///
/// <para>
/// <b>Cartera en el alta</b> (<see cref="TenantsCartera"/>, petición del propietario
/// del 2026-09-28): el Operador CAE externo que da de alta a un Gestor CAE puede
/// ponerle ya en cartera Tenants beneficiarios enteros. Es la misma Asignación de
/// Cartera universal que crea aceptar una solicitud de incorporación, con el mismo
/// catálogo (<see cref="ICatalogoIncorporacionCartera"/>): los Tenants ofrecibles
/// salen de <see cref="ICatalogoIncorporacionCartera.ObtenerAsignablesAsync"/> —
/// Asignación de Operación externa, no raíz, universal y vigente del Operador CAE, con
/// su delegación viva— y la escritura de <c>IncorporarAsync</c>. Solo para una cuenta
/// Gestor CAE (el único rol que recibe una cartera así: una cartera no concede roles
/// de Propiedad, y Coordinador CAE y Consulta no llevan cartera propia), y solo si la
/// cuenta nace en el Tenant de origen de quien da el alta, que es el Operador CAE.
/// </para>
///
/// <para>
/// <b>Atomicidad</b>: la cuenta, su rol y todas sus carteras se escriben en una sola
/// transacción (<see cref="ITransaccionDeComando"/>); si una cartera no puede
/// crearse, no queda ni la cuenta. Sin cartera pedida, un fallo del rol no deshace
/// el alta y <see cref="UsuarioCreado.FalloAlAsignarRol"/> lo dice para que la
/// pantalla no lo anuncie como completo (sin rol la cuenta no da acceso a nada); con
/// cartera sí la deshace, porque una cartera de alguien que no es Gestor CAE no
/// tiene sentido.
/// </para>
/// </summary>
public record CrearUsuarioCommand(
    string Email,
    string NombreCompleto,
    string Rol,
    Guid? CoordinadorUsuarioId,
    Guid? ClienteId,
    bool PermisoConsultarAccesoDocumentosSensibles,
    IReadOnlyCollection<Guid>? TenantsCartera = null) : ICommand<UsuarioCreado>;

public record UsuarioCreado(Guid UsuarioId, string TokenActivacion, Error? FalloAlAsignarRol);

public class CrearUsuarioCommandHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual,
    ICatalogoIncorporacionCartera catalogoCartera,
    ITransaccionDeComando transaccion)
    : IRequestHandler<CrearUsuarioCommand, Result<UsuarioCreado>>
{
    public static readonly Error CarteraSoloParaGestorCae = Error.Crear(
        "Usuarios.CarteraSoloParaGestorCae", "Solo una cuenta Gestor CAE puede tener empresas en su cartera.");

    public static readonly Error CarteraSoloDesdeTuOrganizacion = Error.Crear(
        "Usuarios.CarteraSoloDesdeTuOrganizacion",
        "Las empresas de la cartera solo se asignan al dar de alta a alguien de tu propia organización.");

    public static readonly Error EmpresaNoAsignable = Error.Crear(
        "Usuarios.EmpresaNoAsignable",
        "Tu organización ya no gestiona alguna de las empresas marcadas. Revisa la lista y vuelve a guardar.");

    public static readonly Error AltaNoGuardada = Error.Crear(
        "Usuarios.AltaNoGuardada",
        "No pudimos guardar el alta. No se ha creado la cuenta ni su cartera; vuelve a intentarlo.");

    public async Task<Result<UsuarioCreado>> Handle(CrearUsuarioCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return Result.Fallo<UsuarioCreado>(AutoridadSobreCuentas.SinAutoridad);

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.NombreCompleto))
            return Result.Fallo<UsuarioCreado>(Error.Crear("Usuarios.DatosObligatorios", "Correo y nombre son obligatorios."));

        if (!AutoridadSobreCuentas.RolesExistentes.Contains(request.Rol))
            return Result.Fallo<UsuarioCreado>(AutoridadSobreCuentas.RolDesconocido);

        if (request.Rol == AutoridadSobreCuentas.RolCliente && request.ClienteId is null)
            return Result.Fallo<UsuarioCreado>(AutoridadSobreCuentas.ClienteRequerido);

        // ApplicationUser no lo sella el interceptor de tenant: la cuenta nace en
        // el Context Workspace activo, con su TenantId explícito.
        if (tenantActual.TenantId is not { } tenantId)
            return Result.Fallo<UsuarioCreado>(Error.Crear(
                "Usuarios.SinOrganizacion", "No pudimos determinar tu organización. Vuelve a iniciar sesión."));

        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        var rolAsignable = RolesReservadosAlTenantDeOrigen.Verificar(request.Rol, tenantOrigenId, tenantId);
        if (rolAsignable.EsFallido)
            return Result.Fallo<UsuarioCreado>(rolAsignable.Error);

        var cartera = await ResolverCarteraAsync(request, tenantId, tenantOrigenId, cancellationToken);
        if (cartera.EsFallido)
            return Result.Fallo<UsuarioCreado>(cartera.Error);
        var operacionesCartera = cartera.Valor;

        var actorEsAdministrador = await currentUserService.ObtenerRolEfectivoAsync() == AutoridadSobreCuentas.Administrador;
        var nuevaCuenta = new NuevaCuentaUsuario(
            request.Email,
            request.NombreCompleto,
            tenantId,
            request.Rol == AutoridadSobreCuentas.RolGestorCae ? request.CoordinadorUsuarioId : null,
            request.Rol == AutoridadSobreCuentas.RolCliente ? request.ClienteId : null,
            actorEsAdministrador
                && request.Rol == AutoridadSobreCuentas.Administrador
                && request.PermisoConsultarAccesoDocumentosSensibles);

        Guid usuarioId = default;
        Error? falloRol = null;

        Result escrita;
        try
        {
            escrita = await transaccion.EjecutarAsync(async ct =>
            {
                // Cada reintento de la estrategia de ejecución empieza de cero.
                usuarioId = default;
                falloRol = null;

                var creada = await cuentas.CrearAsync(nuevaCuenta, ct);
                if (creada.EsFallido)
                    return Result.Fallo(creada.Error);
                usuarioId = creada.Valor;

                var rol = await cuentas.AsignarRolAsync(usuarioId, request.Rol, ct);
                if (rol.EsFallido)
                {
                    if (operacionesCartera.Count > 0)
                        return Result.Fallo(rol.Error);
                    falloRol = rol.Error;
                }

                foreach (var operacion in operacionesCartera)
                {
                    var incorporada = await IncorporarAsync(operacion, tenantId, usuarioId, ct);
                    if (incorporada.EsFallido)
                        return incorporada;
                }

                return Result.Exito();
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // La transacción ya se deshizo y el contexto quedó vacío (ITransaccionDeComando):
            // ni la cuenta ni ninguna cartera existen.
            return Result.Fallo<UsuarioCreado>(AltaNoGuardada);
        }

        if (escrita.EsFallido)
            return Result.Fallo<UsuarioCreado>(escrita.Error);

        var token = await cuentas.GenerarTokenActivacionAsync(usuarioId, cancellationToken);
        if (token.EsFallido)
            return Result.Fallo<UsuarioCreado>(token.Error);

        return Result.Exito(new UsuarioCreado(usuarioId, token.Valor, falloRol));
    }

    /// <summary>
    /// Las operaciones de las que colgará cada cartera, decididas aquí y no por quien
    /// llama: de la petición solo se toman los Tenants, y cada uno tiene que estar entre
    /// los asignables del Operador CAE en este momento. Sin Tenants, lista vacía.
    /// </summary>
    private async Task<Result<IReadOnlyList<TenantCandidatoIncorporacion>>> ResolverCarteraAsync(
        CrearUsuarioCommand request, Guid tenantId, Guid? tenantOrigenId, CancellationToken cancellationToken)
    {
        var pedidos = request.TenantsCartera?.Distinct().ToList() ?? [];
        if (pedidos.Count == 0)
            return Result.Exito<IReadOnlyList<TenantCandidatoIncorporacion>>([]);

        // Operación no concede Propiedad: ni Administrador ni Dirección CAE por
        // cartera, y la cartera universal de la incorporación es solo de Gestor CAE.
        if (request.Rol != AutoridadSobreCuentas.RolGestorCae)
            return Result.Fallo<IReadOnlyList<TenantCandidatoIncorporacion>>(CarteraSoloParaGestorCae);

        // El Operador CAE es el Tenant de origen de quien da el alta, y la cuenta tiene
        // que nacer en él: desde el Context Workspace de otro Tenant la cuenta sería de
        // ese Tenant, no del Operador CAE que opera los Tenants de la cartera.
        if (tenantOrigenId is not { } operadorTenantId || operadorTenantId != tenantId)
            return Result.Fallo<IReadOnlyList<TenantCandidatoIncorporacion>>(CarteraSoloDesdeTuOrganizacion);

        var asignables = (await catalogoCartera.ObtenerAsignablesAsync(operadorTenantId, cancellationToken))
            .ToDictionary(a => a.PropietarioTenantId);

        var operaciones = new List<TenantCandidatoIncorporacion>(pedidos.Count);
        foreach (var propietarioTenantId in pedidos)
        {
            if (!asignables.TryGetValue(propietarioTenantId, out var asignable))
                return Result.Fallo<IReadOnlyList<TenantCandidatoIncorporacion>>(EmpresaNoAsignable);
            operaciones.Add(asignable);
        }

        return Result.Exito<IReadOnlyList<TenantCandidatoIncorporacion>>(operaciones);
    }

    /// <summary>
    /// La cartera se escribe con el Tenant propietario como Tenant activo, como al aceptar
    /// una solicitud: la política RLS de las carteras solo deja escribir sobre el
    /// propietario contextual. El Guid sale de <see cref="ResolverCarteraAsync"/>, que lo
    /// acaba de validar contra las operaciones del propio Operador CAE.
    /// </summary>
    private async Task<Result> IncorporarAsync(
        TenantCandidatoIncorporacion operacion, Guid operadorTenantId, Guid usuarioId, CancellationToken ct)
    {
        using (AmbitoTenantExplicito.Establecer(operacion.PropietarioTenantId))
        {
            var incorporacion = await catalogoCartera.IncorporarAsync(
                operacion.PropietarioTenantId, operadorTenantId, operacion.AsignacionOperacionId, usuarioId, ct);

            if (incorporacion.MotivoAnulacion is not null)
                return Result.Fallo(incorporacion.MotivoAnulacion == MotivoAnulacionSolicitudCartera.OperacionNoVigente
                    ? EmpresaNoAsignable
                    : AltaNoGuardada);

            return await catalogoCartera.GuardarDetectandoCarreraAsync(ct)
                ? Result.Exito()
                : Result.Fallo(AltaNoGuardada);
        }
    }
}
