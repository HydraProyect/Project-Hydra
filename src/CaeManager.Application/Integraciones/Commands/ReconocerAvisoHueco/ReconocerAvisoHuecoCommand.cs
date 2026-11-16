using CaeManager.Application.Common;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Integraciones.Commands.ReconocerAvisoHueco;

/// <summary>
/// La acción Confirmar de Conexiones (issue #2124): da por revisado el aviso «posible correo no recuperado desde» de un
/// buzón de Microsoft 365. Limpia <see cref="ConexionIntegracion.HuecoPendienteDesdeUtc"/>, anula la recuperación en curso
/// y deja una fila de auditoría propia (<see cref="Domain.Auditoria.RegistroAuditoria.AccionHuecoReconocido"/>).
/// <paramref name="MarcaVistaUtc"/> es la fecha del aviso que la persona tenía delante al confirmar: si la marca guardada
/// ya es otra (una pérdida anterior la ha adelantado), no se reconoce un aviso que no ha visto.
/// </summary>
[AreaDelEncargo("Integraciones")]
public record ReconocerAvisoHuecoCommand(Guid ConexionId, DateTime MarcaVistaUtc) : ICommand;

public class ReconocerAvisoHuecoCommandValidator : AbstractValidator<ReconocerAvisoHuecoCommand>
{
    public ReconocerAvisoHuecoCommandValidator()
    {
        RuleFor(c => c.ConexionId).NotEmpty();
        RuleFor(c => c.MarcaVistaUtc).NotEmpty();
    }
}

/// <summary>
/// <para>
/// <b>Autoridad</b> (decisión de Chris, 2026-11-16), comprobada aquí y no solo en la página. Pueden confirmar:
/// <list type="bullet">
/// <item>el Administrador del Tenant propietario de la conexión;</item>
/// <item>el Administrador del Operador CAE externo que opera ese Tenant beneficiario por Asignación de Operación, que
/// llega con el rol efectivo Administrador (<c>RolEfectivoDelWorkspaceMiddleware</c>; decisión del 2026-10-20 en
/// <see cref="Usuarios.RolesReservadosAlTenantDeOrigen"/>);</item>
/// <item>el Gestor CAE que tiene el Tenant en su Asignación de Cartera. La cartera de un Gestor CAE es siempre el Tenant
/// entero, así que la comprobación es <see cref="IAlcanceDatosService.AlcanzaElTenantEnteroAsync"/>, la misma que usan
/// los comandos de Tenant con cartera.</item>
/// </list>
/// Cualquier otro rol (Coordinador CAE, Dirección CAE, Consulta, Cliente) y un Gestor CAE sin cartera en el Tenant
/// reciben <see cref="CodigoSinPermiso"/>. La Propiedad la ponen el filtro de Tenant y la RLS: una conexión de otro Tenant
/// no se lee y sale como no encontrada, igual que una que el alcance no deja ver (por la cartera del Cliente empresarial
/// o, en un buzón personal, por no ser de quien actúa). Soporte TALVEG en Sesión Privilegiada no llega aquí:
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/> la rechaza antes por ser de solo lectura.
/// </para>
/// <para>
/// <b>Estado y concurrencia.</b> Dentro de <see cref="ICerrojoVerificacionBuzon"/>, el cerrojo por conexión que toman
/// Desconectar, Reactivar, la verificación y cada escritura de la recuperación del hueco, la conexión se relee de la base
/// y todo sale de ahí. Sin marca (otra persona confirmó antes o la recuperación terminó), éxito sin guardar. Solo se
/// reconoce con la conexión Habilitada y sin <see cref="ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc"/>
/// (<see cref="CodigoNoReconocible"/>), y solo la marca que se vio (<see cref="CodigoAvisoCambiado"/>). La ingesta puede
/// mover la marca sin el cerrojo: ese choque lo detecta la concurrencia optimista al guardar, se descarta lo rastreado y
/// sale también <see cref="CodigoAvisoCambiado"/>, sin reintentar, porque reintentar reconocería una pérdida que nadie ha
/// visto.
/// </para>
/// </summary>
public class ReconocerAvisoHuecoCommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService usuarioActual,
    ICerrojoVerificacionBuzon cerrojo,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios)
    : IRequestHandler<ReconocerAvisoHuecoCommand, Result>
{
    /// <summary>Quien actúa no es ninguno de los tres perfiles que pueden confirmar el aviso.</summary>
    public const string CodigoSinPermiso = "ConexionIntegracion.ReconocerAvisoSinPermiso";

    /// <summary>La conexión no está Habilitada o tiene una marca del hueco por registrar: el hueco sigue abierto.</summary>
    public const string CodigoNoReconocible = "ConexionIntegracion.AvisoHuecoNoReconocible";

    /// <summary>La marca guardada no es la del aviso que se confirmó, o cambió mientras se guardaba.</summary>
    public const string CodigoAvisoCambiado = "ConexionIntegracion.AvisoHuecoCambiado";

    public async Task<Result> Handle(ReconocerAvisoHuecoCommand request, CancellationToken cancellationToken)
    {
        if (!await PuedeReconocerAsync(cancellationToken))
            return Result.Fallo(Error.Crear(
                CodigoSinPermiso,
                "Solo un Administrador o el Gestor CAE con esta organización en su cartera pueden dar por revisado este aviso."));

        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionId, cancellationToken);
        if (conexion is null || !await DesconectarBuzonCommandHandler.ConexionVisibleAsync(alcanceDatos, conexion, cancellationToken))
            return NoEncontrada();

        Result? resultado = null;
        if (!await cerrojo.IntentarEjecutarAsync(conexion.Id, async ct => resultado = await ReconocerBajoCerrojoAsync(request, ct), cancellationToken))
            return Result.Fallo(Error.Crear(
                DesconectarBuzonCommandHandler.CodigoVerificacionEnCurso,
                "Ya estamos trabajando con este buzón. Inténtalo de nuevo en unos segundos."));

        return resultado!;
    }

    private async Task<bool> PuedeReconocerAsync(CancellationToken cancellationToken) =>
        await usuarioActual.ObtenerRolEfectivoAsync() switch
        {
            "Administrador" => true,
            "GestorCae" => await alcanceDatos.AlcanzaElTenantEnteroAsync(cancellationToken),
            _ => false,
        };

    private async Task<Result> ReconocerBajoCerrojoAsync(ReconocerAvisoHuecoCommand request, CancellationToken cancellationToken)
    {
        var actual = await conexionRepositorio.ObtenerActualPorIdAsync(request.ConexionId, cancellationToken);
        if (actual is null || !await DesconectarBuzonCommandHandler.ConexionVisibleAsync(alcanceDatos, actual, cancellationToken))
            return NoEncontrada();

        if (actual.HuecoPendienteDesdeUtc is not { } marca)
            return Result.Exito();

        if (actual.Estado != EstadoConexionIntegracion.Habilitada || actual.MarcaHuecoPorRegistrarDesdeUtc is not null)
            return Result.Fallo(Error.Crear(
                CodigoNoReconocible,
                "Este buzón no está conectado y funcionando, así que el correo puede seguir sin llegar: no se puede dar el aviso por revisado todavía. Actualizamos el listado."));

        if (marca != request.MarcaVistaUtc)
            return AvisoCambiado();

        actual.ReconocerAvisoHueco();
        try
        {
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            descarteCambios.DescartarCambiosPendientes();
            return AvisoCambiado();
        }
        catch
        {
            descarteCambios.DescartarCambiosPendientes();
            throw;
        }

        return Result.Exito();
    }

    private static Result AvisoCambiado() =>
        Result.Fallo(Error.Crear(
            CodigoAvisoCambiado,
            "El aviso de este buzón ha cambiado mientras lo revisabas. Actualizamos el listado: revisa la nueva fecha antes de confirmar."));

    private static Result NoEncontrada() =>
        Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));
}
