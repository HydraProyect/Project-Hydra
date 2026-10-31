using System.Security.Cryptography;
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
/// <item>No hay transacción abierta: un rollback borraría el uso de una llamada que ya salió, y una
/// estrategia de reintentos la repetiría (V7.3.3). Tampoco cambios pendientes del llamador: la puerta
/// guarda con su mismo <see cref="IUnitOfWork"/> y se los llevaría aunque negara la operación.</item>
/// <item>Hay Tenant resuelto y no es una Sesión Privilegiada, viva o ya revocada: un Actor de Plataforma
/// TALVEG nunca opera con la credencial de un Tenant (V4.1.3). Mientras la selección nombre una sesión,
/// la conexión sigue abriéndose como cae_app_soporte, que no puede escribir el uso.</item>
/// <item>La conexión, y la Acreditación o el Trabajador, son del Tenant propietario actual, leídos bajo
/// los filtros de Tenant y RLS con la identidad efectiva de la petición, y la Acreditación es de la
/// misma plataforma que la conexión (V4.2.1). Decisión del 2026-10-30, punto 5: nunca con las
/// credenciales de otro.</item>
/// <item>Quien pide tiene rol efectivo con escritura en ese Tenant: una coordenada de contexto no es
/// autoridad, y una Asignación de Operación revocada deja el rol en null aunque el circuito conserve
/// la selección (V4.1.3). Sin usuario no opera nadie: un circuito con la sesión invalidada tampoco tiene
/// usuario, y un <see cref="AmbitoTenantExplicito"/> es una coordenada, no autoridad. La identidad del
/// proceso de fondo llega con el incremento de cola.</item>
/// <item>El kill switch de la plataforma, en el catálogo global, leído de la base en cada operación.</item>
/// <item>El Add-on de subida automática del Tenant, su estado comercial y que no esté suspendido.</item>
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
/// Si el cierre del uso no se puede guardar, la llamada ya salió: el resultado se devuelve, y el uso se
/// queda EnCurso con su clave de idempotencia y un error en el log con su Id. Reconciliar esos usos es
/// del incremento de cola.
/// </para>
///
/// <para>
/// La clave de idempotencia es un Guid: va a un registro que Soporte TALVEG lee y sin purga, y un dato
/// del Trabajador no puede acabar dentro. La puerta no decide el alcance de cartera: eso es de cada Command
/// que la dispare, como en <see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/>. Tampoco
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
    IClienteActivoSeleccionado clienteActivoSeleccionado,
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

        return await EjecutarAsync(acceso, operacion, acreditacionId, trabajadorId: null, claveIdempotencia: null, cancellationToken, (conector, credencial, ct) =>
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

        if (string.IsNullOrWhiteSpace(trabajador.Dni) || !ClaveValida(claveIdempotencia))
        {
            await DenegarAsync(acceso.Conexion.Id, operacion, null, trabajadorId, MotivoUsoConector.DatosInsuficientes, cancellationToken);
            return Result.Fallo<ReferenciaEnPlataforma>(NoDisponible);
        }

        var solicitud = new SolicitudAltaTrabajador(trabajador.Id, trabajador.Nombre, trabajador.Apellidos, trabajador.Dni, claveIdempotencia);
        return await EjecutarAsync(acceso, operacion, acreditacionId: null, trabajadorId, claveIdempotencia, cancellationToken, (conector, credencial, ct) =>
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

        if (string.IsNullOrWhiteSpace(acreditacion.ArchivoUrl) || !ClaveValida(claveIdempotencia))
        {
            await DenegarAsync(acceso.Conexion.Id, operacion, acreditacionId, null, MotivoUsoConector.DatosInsuficientes, cancellationToken);
            return Result.Fallo<ReferenciaEnPlataforma>(NoDisponible);
        }

        return await EjecutarAsync(acceso, operacion, acreditacionId, trabajadorId: null, claveIdempotencia, cancellationToken, async (conector, credencial, ct) =>
        {
            await using var contenido = await archivos.AbrirAsync(acreditacion.ArchivoUrl, ct);
            return await conector.SubirDocumentoAsync(
                credencial, new SolicitudSubidaDocumento(acreditacion.Id, acreditacion.DocumentoId, contenido, claveIdempotencia), ct);
        });
    }

    private static bool ClaveValida(string claveIdempotencia) =>
        Guid.TryParseExact(claveIdempotencia, "D", out var clave) && clave != Guid.Empty;

    private sealed record AccesoAutorizado(
        Guid TenantId, ConexionPlataformaCae Conexion, IConectorPlataformaCae Conector, CredencialApiConector Credencial);

    /// <summary>Null si no se puede operar; la denegación ya ha quedado registrada.</summary>
    private async Task<AccesoAutorizado?> AutorizarAsync(
        Guid conexionId, OperacionConectorPlataformaCae operacion, Guid? acreditacionId, Guid? trabajadorId, CancellationToken cancellationToken)
    {
        if (conexiones.HayTransaccionAbierta || conexiones.HayCambiosPendientes)
        {
            logger.LogError(
                "Operación {Operacion} con la conexión {ConexionId} denegada: hay una transacción abierta o cambios pendientes.",
                operacion, conexionId);
            return null;
        }

        // Se mira la selección además de la revalidación: una sesión ya revocada no revalida, pero la conexión
        // sigue siendo de cae_app_soporte, sin INSERT sobre el uso. Negar aquí no escribe nada.
        if (tenantActual.TenantId is not { } tenantId
            || clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null
            || await sesionPrivilegiadaActual.RevalidarAsync(cancellationToken) is not null)
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

        if (!await TieneAutoridadAsync())
        {
            await DenegarAsync(conexion.Id, operacion, acreditacionId, trabajadorId, MotivoUsoConector.SinAutoridad, cancellationToken);
            return null;
        }

        var proveedor = await proveedores.ObtenerInterruptoresConectorAsync(conexion.ProveedorPlataformaCaeId, cancellationToken);
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

        var credencial = await LeerCredencialAsync(conexion.Id, operacion, cancellationToken);
        if (credencial is null || credencial.TenantId != tenantId || string.IsNullOrWhiteSpace(credencial.SecretoApi))
        {
            await DenegarAsync(conexion.Id, operacion, acreditacionId, trabajadorId, MotivoUsoConector.SinCredencial, cancellationToken);
            return null;
        }

        return new AccesoAutorizado(tenantId, conexion, conector, new CredencialApiConector(credencial.SecretoApi));
    }

    private async Task<bool> TieneAutoridadAsync()
    {
        if (await usuarioActual.ObtenerUsuarioActualIdAsync() is null)
            return false;

        var rol = await usuarioActual.ObtenerRolEfectivoAsync();
        return rol is not null && AutorizacionEscrituraBehavior<object, object>.RolesConEscritura.Contains(rol);
    }

    // Un anillo de Data Protection que no descifra se trata como una conexión sin credencial, con su rastro.
    private async Task<CredencialParaConector?> LeerCredencialAsync(
        Guid conexionId, OperacionConectorPlataformaCae operacion, CancellationToken cancellationToken)
    {
        try
        {
            return await conexiones.LeerCredencialAsync(conexionId, cancellationToken);
        }
        catch (CryptographicException)
        {
            logger.LogError(
                "La credencial de la conexión {ConexionId} no se pudo descifrar en la operación {Operacion}.",
                conexionId, operacion);
            return null;
        }
    }

    private async Task<MotivoUsoConector?> MotivoDeDenegacionAsync(
        InterruptoresConectorProveedor? proveedor, Guid tenantId, CancellationToken cancellationToken)
    {
        if (proveedor is not { Activo: true, ConectorApiActivo: true })
            return MotivoUsoConector.PlataformaDesactivada;

        var habilitacion = await conexiones.ObtenerHabilitacionComercialAsync(tenantId, cancellationToken);
        if (habilitacion is not { AddOnSubidaAutomaticaActivo: true })
            return MotivoUsoConector.AddOnInactivo;

        if (habilitacion.Estado != EstadoTenant.Activo)
            return MotivoUsoConector.TenantSuspendido;

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
        await GuardarUsoNuevoAsync(
            UsoConexionPlataformaCae.Denegar(conexionId, operacion, usuarioId, acreditacionId, trabajadorId, motivo, DateTime.UtcNow),
            cancellationToken);
    }

    private async Task GuardarUsoNuevoAsync(UsoConexionPlataformaCae uso, CancellationToken cancellationToken)
    {
        conexiones.RegistrarUso(uso);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            conexiones.DescartarUso(uso);
            throw;
        }
    }

    private async Task<Result<T>> EjecutarAsync<T>(
        AccesoAutorizado acceso, OperacionConectorPlataformaCae operacion, Guid? acreditacionId, Guid? trabajadorId, string? claveIdempotencia,
        CancellationToken cancellationToken,
        Func<IConectorPlataformaCae, CredencialApiConector, CancellationToken, Task<ResultadoConector<T>>> llamada)
        where T : notnull
    {
        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        var uso = UsoConexionPlataformaCae.Iniciar(
            acceso.Conexion.Id, operacion, usuarioId, acreditacionId, trabajadorId, DateTime.UtcNow, claveIdempotencia);
        await GuardarUsoNuevoAsync(uso, cancellationToken);

        try
        {
            var resultado = await llamada(acceso.Conector, acceso.Credencial, cancellationToken);

            if (resultado.Fallo is { } motivo)
            {
                await CerrarConFalloAsync(uso, motivo);
                return Result.Fallo<T>(FalloEnPlataforma);
            }

            uso.FinalizarConExito(DateTime.UtcNow);
            await GuardarCierreAsync(uso);
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
            // o la respuesta de la plataforma (OWASP ASVS 4.0.3 V7.1.1). El Id del uso permite
            // encontrar su fila desde el log.
            logger.LogError(
                "Fallo inesperado ({TipoExcepcion}) en la operación {Operacion}, uso {UsoId}.",
                ex.GetType().Name, operacion, uso.Id);
            await CerrarConFalloAsync(uso, MotivoUsoConector.Inesperado);
            return Result.Fallo<T>(FalloEnPlataforma);
        }
    }

    private async Task CerrarConFalloAsync(UsoConexionPlataformaCae uso, MotivoUsoConector motivo)
    {
        uso.FinalizarConFallo(motivo, DateTime.UtcNow);
        await GuardarCierreAsync(uso);
    }

    // Sin el token de la petición: la llamada ya salió, y su rastro se cierra aunque se cancele. No lanza,
    // porque un fallo aquí no cambia lo que hizo la plataforma; descartar el uso evita que su cierre
    // pendiente rompa el siguiente guardado del mismo ámbito.
    private async Task GuardarCierreAsync(UsoConexionPlataformaCae uso)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            conexiones.DescartarUso(uso);
            logger.LogError(
                "No se pudo guardar el cierre del uso {UsoId} ({TipoExcepcion}): queda EnCurso.",
                uso.Id, ex.GetType().Name);
        }
    }
}
