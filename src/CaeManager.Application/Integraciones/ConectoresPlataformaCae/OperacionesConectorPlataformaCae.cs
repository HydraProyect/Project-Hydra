using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones.ConectoresPlataformaCae;

/// <summary>
/// La única puerta hacia un <see cref="IConectorPlataformaCae"/>. Decide, en este orden y fallando en
/// cerrado (OWASP ASVS 4.0.3 V4.1.5), si una operación puede salir hacia la plataforma:
/// <list type="number">
/// <item>Hay Tenant resuelto y no es una Sesión Privilegiada: un Actor de Plataforma TALVEG nunca opera
/// con la credencial de un Tenant (V4.1.3).</item>
/// <item>La conexión, y la Acreditación o el Trabajador, son del Tenant propietario actual, leídos bajo
/// los filtros de Tenant y RLS con la identidad efectiva de la petición, y la Acreditación es de la
/// misma plataforma que la conexión (V4.2.1). Decisión del 2026-10-30, punto 5: nunca con las
/// credenciales de otro.</item>
/// <item>El kill switch de la plataforma, en el catálogo global.</item>
/// <item>El Add-on de subida automática del Tenant y su estado comercial.</item>
/// <item>Hay credencial y hay conector registrado.</item>
/// </list>
///
/// <para>
/// Cada decisión deja rastro (V7.2.2): una fila en <see cref="UsoConexionPlataformaCae"/> si hay
/// conexión del Tenant a la que colgarla, y un aviso en el log sin ella. El uso se guarda ANTES de
/// llamar: si no se puede guardar, no sale la llamada.
/// </para>
///
/// <para>
/// No decide el alcance de cartera ni el rol de quien pide la operación: eso es de cada Command que la
/// dispare, como en <see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/>. Tampoco
/// guarda lo que devuelve la plataforma en la Acreditación: es del incremento de cola y lectura
/// programada.
/// </para>
/// </summary>
public interface IOperacionesConectorPlataformaCae
{
    Task<Result<EstadoDocumentoEnPlataforma>> LeerEstadoDocumentoAsync(
        Guid conexionId, Guid acreditacionId, CancellationToken cancellationToken = default);

    Task<Result<ReferenciaEnPlataforma>> DarDeAltaTrabajadorAsync(
        Guid conexionId, Guid trabajadorId, string claveIdempotencia, CancellationToken cancellationToken = default);

    Task<Result<ReferenciaEnPlataforma>> SubirDocumentoAsync(
        Guid conexionId, Guid acreditacionId, string claveIdempotencia, CancellationToken cancellationToken = default);
}

public class OperacionesConectorPlataformaCae(
    IConexionPlataformaCaeRepository conexiones,
    IProveedorPlataformaCaeRepository proveedores,
    IEnumerable<IConectorPlataformaCae> conectores,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantActual tenantActual,
    ICurrentUserService usuarioActual,
    IFileStorageService archivos,
    IUnitOfWork unitOfWork,
    ILogger<OperacionesConectorPlataformaCae> logger)
    : IOperacionesConectorPlataformaCae
{
    private static readonly Error NoDisponible = Error.Crear(
        "ConectorPlataformaCae.NoDisponible", "Esta conexión con la plataforma no está disponible.");

    private static readonly Error FalloEnPlataforma = Error.Crear(
        "ConectorPlataformaCae.Fallo", "La plataforma no pudo completar la operación. Inténtalo de nuevo más tarde.");

    public async Task<Result<EstadoDocumentoEnPlataforma>> LeerEstadoDocumentoAsync(
        Guid conexionId, Guid acreditacionId, CancellationToken cancellationToken = default)
    {
        var operacion = OperacionConectorPlataformaCae.LeerEstadoDocumento;
        var acceso = await AutorizarAsync(conexionId, operacion, acreditacionId, trabajadorId: null, cancellationToken);
        if (acceso is null)
            return Result.Fallo<EstadoDocumentoEnPlataforma>(NoDisponible);

        var acreditacion = await ObtenerAcreditacionDeLaConexionAsync(acceso, operacion, acreditacionId, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo<EstadoDocumentoEnPlataforma>(NoDisponible);

        return await EjecutarAsync(acceso, operacion, acreditacionId, trabajadorId: null, cancellationToken, (conector, credencial, ct) =>
            conector.LeerEstadoDocumentoAsync(credencial, new SolicitudLecturaEstado(acreditacion.Id, acreditacion.DocumentoId), ct));
    }

    public async Task<Result<ReferenciaEnPlataforma>> DarDeAltaTrabajadorAsync(
        Guid conexionId, Guid trabajadorId, string claveIdempotencia, CancellationToken cancellationToken = default)
    {
        var operacion = OperacionConectorPlataformaCae.DarDeAltaTrabajador;
        var acceso = await AutorizarAsync(conexionId, operacion, acreditacionId: null, trabajadorId, cancellationToken);
        if (acceso is null)
            return Result.Fallo<ReferenciaEnPlataforma>(NoDisponible);

        var trabajador = await conexiones.ObtenerTrabajadorAsync(trabajadorId, cancellationToken);
        if (trabajador is null || trabajador.TenantId != acceso.TenantId)
        {
            await DenegarAsync(acceso.Conexion.Id, operacion, null, trabajadorId, MotivoUsoConector.ObjetoNoEncontrado, cancellationToken);
            return Result.Fallo<ReferenciaEnPlataforma>(NoDisponible);
        }

        if (string.IsNullOrWhiteSpace(trabajador.Dni))
        {
            await DenegarAsync(acceso.Conexion.Id, operacion, null, trabajadorId, MotivoUsoConector.DatosInsuficientes, cancellationToken);
            return Result.Fallo<ReferenciaEnPlataforma>(NoDisponible);
        }

        var solicitud = new SolicitudAltaTrabajador(trabajador.Id, trabajador.Nombre, trabajador.Apellidos, trabajador.Dni, claveIdempotencia);
        return await EjecutarAsync(acceso, operacion, acreditacionId: null, trabajadorId, cancellationToken, (conector, credencial, ct) =>
            conector.DarDeAltaTrabajadorAsync(credencial, solicitud, ct));
    }

    public async Task<Result<ReferenciaEnPlataforma>> SubirDocumentoAsync(
        Guid conexionId, Guid acreditacionId, string claveIdempotencia, CancellationToken cancellationToken = default)
    {
        var operacion = OperacionConectorPlataformaCae.SubirDocumento;
        var acceso = await AutorizarAsync(conexionId, operacion, acreditacionId, trabajadorId: null, cancellationToken);
        if (acceso is null)
            return Result.Fallo<ReferenciaEnPlataforma>(NoDisponible);

        var acreditacion = await ObtenerAcreditacionDeLaConexionAsync(acceso, operacion, acreditacionId, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo<ReferenciaEnPlataforma>(NoDisponible);

        if (string.IsNullOrWhiteSpace(acreditacion.ArchivoUrl))
        {
            await DenegarAsync(acceso.Conexion.Id, operacion, acreditacionId, null, MotivoUsoConector.DatosInsuficientes, cancellationToken);
            return Result.Fallo<ReferenciaEnPlataforma>(NoDisponible);
        }

        return await EjecutarAsync(acceso, operacion, acreditacionId, trabajadorId: null, cancellationToken, async (conector, credencial, ct) =>
        {
            await using var contenido = await archivos.AbrirAsync(acreditacion.ArchivoUrl, ct);
            return await conector.SubirDocumentoAsync(
                credencial, new SolicitudSubidaDocumento(acreditacion.Id, acreditacion.DocumentoId, contenido, claveIdempotencia), ct);
        });
    }

    private sealed record AccesoAutorizado(
        Guid TenantId, ConexionPlataformaCae Conexion, IConectorPlataformaCae Conector, CredencialApiConector Credencial);

    /// <summary>Null si no se puede operar; la denegación ya ha quedado registrada.</summary>
    private async Task<AccesoAutorizado?> AutorizarAsync(
        Guid conexionId, OperacionConectorPlataformaCae operacion, Guid? acreditacionId, Guid? trabajadorId, CancellationToken cancellationToken)
    {
        // En Sesión Privilegiada tampoco se podría escribir el uso: cae_app_soporte no tiene INSERT.
        if (tenantActual.TenantId is not { } tenantId
            || await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null)
        {
            logger.LogWarning(
                "Operación {Operacion} con la conexión {ConexionId} denegada: sin Tenant resuelto o en Sesión Privilegiada.",
                operacion, conexionId);
            return null;
        }

        // El filtro de Tenant y RLS hacen que la de otro Tenant no se distinga de la que no existe.
        var conexion = await conexiones.ObtenerAsync(conexionId, cancellationToken);
        if (conexion is null || conexion.TenantId != tenantId)
        {
            logger.LogWarning(
                "Operación {Operacion} denegada: la conexión {ConexionId} no existe para el Tenant {TenantId}.",
                operacion, conexionId, tenantId);
            return null;
        }

        var proveedor = await proveedores.ObtenerPorIdAsync(conexion.ProveedorPlataformaCaeId, cancellationToken);
        var motivo = await MotivoDeDenegacionAsync(proveedor, tenantId, cancellationToken);
        if (motivo is { } denegacion)
        {
            await DenegarAsync(conexion.Id, operacion, acreditacionId, trabajadorId, denegacion, cancellationToken);
            return null;
        }

        var conector = conectores.FirstOrDefault(c =>
            string.Equals(c.CodigoProveedor, proveedor?.Codigo, StringComparison.OrdinalIgnoreCase));
        if (conector is null)
        {
            await DenegarAsync(conexion.Id, operacion, acreditacionId, trabajadorId, MotivoUsoConector.SinConector, cancellationToken);
            return null;
        }

        var credencial = await conexiones.ObtenerCredencialAsync(conexion.Id, cancellationToken);
        if (credencial is null || credencial.TenantId != tenantId)
        {
            await DenegarAsync(conexion.Id, operacion, acreditacionId, trabajadorId, MotivoUsoConector.SinCredencial, cancellationToken);
            return null;
        }

        return new AccesoAutorizado(tenantId, conexion, conector, new CredencialApiConector(credencial.SecretoApi));
    }

    private async Task<MotivoUsoConector?> MotivoDeDenegacionAsync(
        ProveedorPlataformaCae? proveedor, Guid tenantId, CancellationToken cancellationToken)
    {
        if (proveedor is not { Activo: true, ConectorApiActivo: true })
            return MotivoUsoConector.PlataformaDesactivada;

        var habilitacion = await conexiones.ObtenerHabilitacionComercialAsync(tenantId, cancellationToken);
        if (habilitacion is not { AddOnSubidaAutomaticaActivo: true })
            return MotivoUsoConector.AddOnInactivo;

        return habilitacion.EstadoComercial is EstadoComercialTenant.SoloLectura or EstadoComercialTenant.Suspendida
            ? MotivoUsoConector.EstadoComercialRestringido
            : null;
    }

    private async Task<AcreditacionParaConector?> ObtenerAcreditacionDeLaConexionAsync(
        AccesoAutorizado acceso, OperacionConectorPlataformaCae operacion, Guid acreditacionId, CancellationToken cancellationToken)
    {
        var acreditacion = await conexiones.ObtenerAcreditacionAsync(acreditacionId, cancellationToken);
        if (acreditacion is null || acreditacion.TenantId != acceso.TenantId)
        {
            await DenegarAsync(acceso.Conexion.Id, operacion, acreditacionId, null, MotivoUsoConector.ObjetoNoEncontrado, cancellationToken);
            return null;
        }

        if (acreditacion.ProveedorPlataformaCaeId != acceso.Conexion.ProveedorPlataformaCaeId)
        {
            await DenegarAsync(acceso.Conexion.Id, operacion, acreditacionId, null, MotivoUsoConector.ObjetoDeOtraPlataforma, cancellationToken);
            return null;
        }

        return acreditacion;
    }

    private async Task DenegarAsync(
        Guid conexionId, OperacionConectorPlataformaCae operacion, Guid? acreditacionId, Guid? trabajadorId,
        MotivoUsoConector motivo, CancellationToken cancellationToken)
    {
        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        conexiones.RegistrarUso(UsoConexionPlataformaCae.Denegar(
            conexionId, operacion, usuarioId, acreditacionId, trabajadorId, motivo, DateTime.UtcNow));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<Result<T>> EjecutarAsync<T>(
        AccesoAutorizado acceso, OperacionConectorPlataformaCae operacion, Guid? acreditacionId, Guid? trabajadorId,
        CancellationToken cancellationToken,
        Func<IConectorPlataformaCae, CredencialApiConector, CancellationToken, Task<ResultadoConector<T>>> llamada)
        where T : notnull
    {
        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        var uso = UsoConexionPlataformaCae.Iniciar(
            acceso.Conexion.Id, operacion, usuarioId, acreditacionId, trabajadorId, DateTime.UtcNow);
        conexiones.RegistrarUso(uso);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            var resultado = await llamada(acceso.Conector, acceso.Credencial, cancellationToken);

            if (resultado.Fallo is { } motivo)
            {
                await CerrarConFalloAsync(uso, motivo);
                return Result.Fallo<T>(FalloEnPlataforma);
            }

            uso.FinalizarConExito(DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            return resultado.Valor;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CerrarConFalloAsync(uso, MotivoUsoConector.Cancelado);
            throw;
        }
        catch (Exception ex) when (uso.Resultado == ResultadoUsoConector.EnCurso)
        {
            // Solo el tipo: el mensaje de una excepción HTTP puede llevar la URL con la credencial
            // o la respuesta de la plataforma (OWASP ASVS 4.0.3 V7.1.1). El Id es el del uso, que
            // sí se puede buscar (V7.4.1).
            logger.LogError(
                "Fallo inesperado ({TipoExcepcion}) en la operación {Operacion}, uso {UsoId}.",
                ex.GetType().Name, operacion, uso.Id);
            await CerrarConFalloAsync(uso, MotivoUsoConector.Inesperado);
            return Result.Fallo<T>(FalloEnPlataforma);
        }
    }

    // Sin el token de la petición: la llamada ya salió, y su rastro se cierra aunque se cancele.
    private async Task CerrarConFalloAsync(UsoConexionPlataformaCae uso, MotivoUsoConector motivo)
    {
        uso.FinalizarConFallo(motivo, DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
    }
}
