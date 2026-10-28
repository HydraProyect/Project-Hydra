using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.ContratosAcceso.LecturaInbound;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.FileStorage;

/// <summary>
/// Almacenamiento sobre disco local, particionado por tenant (ver
/// Project-Hydra-Negocio/tecnico/docs/MULTITENANCY.md § 4.6 y Project-Hydra-Negocio/tecnico/PLAN-MIGRACION-MULTITENANT.md § 5, Etapa 4):
/// todo archivo nuevo se guarda bajo <c>{tenantId}/{archivo}</c>, nunca en la
/// carpeta plana. Registrado como Scoped (antes Singleton) precisamente para
/// poder depender de <see cref="ITenantActual"/>, que es scoped por
/// naturaleza — inyectar un servicio scoped en un singleton sería una
/// dependencia cautiva (capturaría el primer tenant resuelto para siempre).
///
/// <b>Cifrado en reposo</b> (P1-12 de Project-Hydra-Negocio/MATURITY_REVIEW.md): el
/// contenido se cifra con <see cref="IDataProtector"/> antes de escribirse a
/// disco — mismo mecanismo que ya protege las credenciales de plataformas
/// externas en <c>CaeManagerDbContext</c> y las propias claves de Data
/// Protection (envueltas con KMS, ver <c>DataProtectionKmsOptions</c>). Era
/// el dato más sensible del sistema sin cifrar: estos archivos son PDFs de
/// reconocimientos médicos y otra documentación de trabajadores, categoría
/// especial del art. 9 RGPD.
///
/// El fichero se lee entero en memoria para cifrar/descifrar — no hay API de
/// streaming en <see cref="IDataProtector"/> y los documentos de este
/// sistema están acotados a 10 MB (ver <c>LimitesArchivoSubido.TamanoMaximoBytes</c>
/// en Application), así que el coste es aceptable y coherente con
/// cómo el resto del código ya maneja archivos subidos.
///
/// <b>Formato versionado (v2)</b>: todo archivo nuevo se escribe con la marca
/// <c>CAEBLOBv2</c> delante del payload protegido. Existe para deshacer una
/// ambigüedad que era una vulnerabilidad: sin marca, la única forma de saber
/// si un fichero estaba cifrado era intentar descifrarlo, y por tanto
/// cualquier <see cref="CryptographicException"/> tenía que interpretarse
/// como "esto era legado en claro" y servirse tal cual. Eso confunde cuatro
/// situaciones muy distintas —legado, clave equivocada, corrupción y
/// manipulación— y hace que un ciphertext alterado se entregue como
/// contenido legítimo, anulando la autenticidad que aporta Data Protection.
///
/// Con la marca, un archivo v2 se sabe cifrado antes de tocarlo, así que un
/// <c>Unprotect</c> fallido es lo que de verdad es —integridad rota— y
/// <b>falla cerrado</b>: nunca se sirve el contenido, y se emite alerta
/// operativa porque manipulación o pérdida de clave son incidentes, no
/// errores de usuario.
///
/// La clave se deriva <b>por tenant</b> (el tenant entra como propósito
/// adicional del protector), de modo que el aislamiento entre tenants deja de
/// depender solo de la ruta del fichero y pasa a estar también en la
/// criptografía: un archivo de otro tenant no se descifra ni aunque se
/// consiguiera leer su ruta.
///
/// <b>Compatibilidad con lo ya escrito</b>: un fichero SIN la marca se intenta
/// descifrar con el protector global v1 (autenticado). Si eso falla, por
/// defecto se sigue sirviendo tal cual como legado en claro —así ninguna
/// descarga existente se rompe ni hace falta migración—, pero ya no en
/// silencio: emite una alerta operativa crítica, igual que un v2 que no supera
/// la verificación, porque un fichero así no se distingue de uno manipulado en
/// el volumen ni de un v1 cuya clave se perdió. Con
/// <see cref="DiskFileStorageServiceOptions.RechazarLegadoEnClaro"/> a
/// <c>true</c>, esa rama falla cerrado. La ambigüedad queda acotada a lo
/// escrito antes de esta versión: un archivo legado pasa a v2 la próxima vez
/// que algo lo reescriba. Cada lectura legada deja un aviso, de modo que
/// "¿quedan blobs legados?" pasa a ser una pregunta medible en vez de una
/// suposición — que es lo que hace falta antes de poder retirar la rama
/// legada del todo.
/// </summary>
public class DiskFileStorageService : IFileStorageService, IAlmacenOriginalesInbound
{
    /// <summary>
    /// Prefijo de los archivos escritos con el formato v2. No puede colisionar
    /// con nada legado: un documento en claro empieza por su propia firma
    /// (<c>%PDF-</c>, JPEG, PNG) y un payload de Data Protection empieza por
    /// su cabecera binaria, nunca por texto ASCII.
    /// </summary>
    private static readonly byte[] MarcaFormatoV2 = "CAEBLOBv2:"u8.ToArray();

    private readonly string _rutaBase;
    private readonly ITenantActual _tenantActual;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDataProtector _protectorLegado;
    private readonly IAlertaOperativa _alertaOperativa;
    private readonly ILogger<DiskFileStorageService> _logger;
    private readonly bool _rechazarLegadoEnClaro;

    public DiskFileStorageService(
        IOptions<DiskFileStorageServiceOptions> opciones,
        IHostEnvironment entorno,
        ITenantActual tenantActual,
        IDataProtectionProvider dataProtectionProvider,
        IAlertaOperativa alertaOperativa,
        ILogger<DiskFileStorageService> logger)
    {
        _dataProtectionProvider = dataProtectionProvider;
        _alertaOperativa = alertaOperativa;
        _logger = logger;
        _rutaBase = Path.IsPathRooted(opciones.Value.Ruta)
            ? opciones.Value.Ruta
            : Path.Combine(entorno.ContentRootPath, opciones.Value.Ruta);
        _rechazarLegadoEnClaro = opciones.Value.RechazarLegadoEnClaro;
        _tenantActual = tenantActual;
        // Nombre sin cambiar: renombrarlo rompería el descifrado de los
        // archivos ya escritos con él (mismo criterio que los protectores de
        // credenciales en CaeManagerDbContext). Solo se usa para leer lo que
        // no lleva marca; nada nuevo se escribe ya con él.
        _protectorLegado = dataProtectionProvider.CreateProtector("CaeManager.Archivos.v1");

        Directory.CreateDirectory(_rutaBase);
    }

    public async Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantActual.TenantId
            ?? throw new InvalidOperationException("No se puede guardar un archivo sin un tenant resuelto.");

        var carpetaTenant = CarpetaDeTenant(tenantId);
        Directory.CreateDirectory(Path.Combine(_rutaBase, carpetaTenant));

        // Un nombre recibido puede conservar sufijos de una escritura temporal.
        // Ese espacio de nombres queda reservado al temporal, nunca al definitivo.
        var extension = NombreArchivoDefinitivo.Extension(nombreArchivoOriginal);
        var nombreArchivo = $"{Guid.NewGuid():N}{extension}";
        var identificador = $"{carpetaTenant}/{nombreArchivo}";
        var rutaCompleta = Path.Combine(_rutaBase, carpetaTenant, nombreArchivo);

        using var memoria = new MemoryStream();
        await contenido.CopyToAsync(memoria, cancellationToken);
        var cifrado = ProtectorDeTenant(tenantId).Protect(memoria.ToArray());

        // Marca delante: lo que hace que al leerlo se sepa que está cifrado
        // sin tener que deducirlo de que el descifrado funcione.
        var salida = new byte[MarcaFormatoV2.Length + cifrado.Length];
        MarcaFormatoV2.CopyTo(salida, 0);
        cifrado.CopyTo(salida, MarcaFormatoV2.Length);

        // Escritura atómica: se escribe primero a un temporal en la MISMA
        // carpeta (mismo volumen, condición para que el rename sea atómico
        // en vez de una copia) y solo se publica con File.Move al terminar.
        // Sin esto, un proceso interrumpido a mitad de WriteAllBytesAsync
        // (OOM del contenedor, kill -9, caída del host) deja en la ruta
        // final un fichero truncado que ya sería servible en cuanto el
        // identificador se devolviera — salvo que la interrupción sea
        // anterior a ese punto, en cuyo caso el fichero queda huérfano y sin
        // fila propietaria. Eso NO está compensado en general: solo existe
        // limpieza de huérfanos para los dos llamadores de la ingesta de
        // webhooks (ver CompensacionBlobsHuerfanosIngesta) — el resto de los
        // llamadores de GuardarAsync no tiene ese mecanismo. Con el
        // temporal, lo peor que deja una interrupción es el `.tmp` huérfano;
        // la ruta final nunca existe a medias.
        var rutaTemporal = $"{rutaCompleta}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(rutaTemporal, salida, cancellationToken);
            File.Move(rutaTemporal, rutaCompleta);
        }
        catch
        {
            // El borrado del temporal es limpieza best-effort: si él mismo
            // falla (bloqueo transitorio, antivirus) no puede sustituir a la
            // excepción original — eso ocultaría la causa real del fallo de
            // guardado detrás de un fallo de borrado que no es la noticia.
            try { if (File.Exists(rutaTemporal)) File.Delete(rutaTemporal); }
            catch { /* best-effort: el temporal queda huérfano, no se enmascara el error original */ }
            throw;
        }

        return identificador;
    }

    public async Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default)
    {
        var rutaCompleta = ResolverRutaSegura(identificador);

        // Un identificador de otro tenant (o sin tenant resuelto) se
        // comporta exactamente igual que "no existe" — nunca se revela que
        // pertenece a otro tenant, mismo criterio que el fix IDOR del
        // Issue #18 para los Ids de entidades.
        //
        // El tenant se vuelve a leer aquí en vez de dentro del descifrado: sin
        // él no hay ruta que resolver, así que este es el único punto donde la
        // ausencia significa "no existe" y no un descifrado a ciegas.
        if (rutaCompleta is null || _tenantActual.TenantId is not { } tenantId || !File.Exists(rutaCompleta))
            throw new FileNotFoundException("No encontramos el archivo solicitado.");

        var bytesDisco = await File.ReadAllBytesAsync(rutaCompleta, cancellationToken);

        return new MemoryStream(Descifrar(bytesDisco, identificador, tenantId), writable: false);
    }

    /// <summary>
    /// Inbound M1, PR 4: abre un original del Tenant de la Empresa contratista desde la petición del Tenant del Cliente
    /// empresarial. Es la única lectura del almacenamiento que cruza Tenants, y lo hace con el Tenant propietario que
    /// devolvió <c>app_inbound_archivo_autorizado</c> junto con la referencia, en el mismo <see cref="ArchivoInboundDto"/>:
    /// la referencia tiene que empezar por la carpeta de ese Tenant (<see cref="OriginalesInbound.EsDelTenant"/>) y
    /// resolver dentro de ella (si no, «no existe», igual que <see cref="AbrirAsync"/>), y se descifra con la clave derivada
    /// de ese Tenant, nunca con la del Tenant activo. La autorización la comprobó la función; la cartera, Application.
    /// </summary>
    public async Task<Stream> AbrirOriginalAsync(ArchivoInboundDto archivo, CancellationToken cancellationToken = default)
    {
        var tenantPropietarioId = archivo.TenantPropietarioId;
        if (tenantPropietarioId == Guid.Empty || archivo.ArchivoUrl is not { } identificador
            || !OriginalesInbound.EsDelTenant(identificador, tenantPropietarioId))
            throw new FileNotFoundException("No encontramos el archivo solicitado.", archivo.ArchivoUrl);

        var rutaCompleta = ResolverRutaSegura(identificador, tenantPropietarioId);
        if (rutaCompleta is null || !File.Exists(rutaCompleta))
            throw new FileNotFoundException("No encontramos el archivo solicitado.");

        var bytesDisco = await File.ReadAllBytesAsync(rutaCompleta, cancellationToken);

        return new MemoryStream(Descifrar(bytesDisco, identificador, tenantPropietarioId), writable: false);
    }

    public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
    {
        var rutaCompleta = ResolverRutaSegura(identificador);

        // Misma resolución segura que al abrir: un identificador de otro
        // tenant no resuelve, así que la purga de un tenant nunca puede
        // borrar archivos de otro.
        if (rutaCompleta is not null && File.Exists(rutaCompleta))
            File.Delete(rutaCompleta);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Protector derivado del tenant: el identificador entra como propósito
    /// adicional, así que el material de clave de un tenant no sirve para
    /// descifrar los archivos de otro. El aislamiento deja de descansar solo
    /// en que la comprobación de ruta sea correcta.
    ///
    /// <b>Qué tenant, exactamente</b>: el de <see cref="ITenantActual"/>, que
    /// es el tenant <i>operado</i> — bajo un workspace delegado devuelve el
    /// seleccionado, no el de origen del usuario que sube el archivo. Es lo
    /// correcto aquí: el documento pertenece al tenant cuya documentación se
    /// está gestionando, no al del Gestor CAE externo que lo aporta. Queda
    /// escrito porque es una decisión, no una coincidencia: si alguna vez se
    /// cambiara <see cref="ITenantActual"/> para devolver el tenant de origen
    /// (que se pide con <c>ICurrentUserService.ObtenerTenantOrigenIdAsync</c>,
    /// y es otra cosa), todos los archivos escritos hasta entonces dejarían de
    /// descifrarse.
    ///
    /// Sin tenant resuelto no se escribe nada: <see cref="GuardarAsync"/> falla
    /// cerrado. Es deliberado — fuera de una petición HTTP (siembra, hosted
    /// services, trabajos de fondo) no hay tenant, y usar un propósito de
    /// relleno crearía un espacio criptográfico compartido por todos los
    /// tenants justo en el caso que nadie prueba.
    /// </summary>
    private IDataProtector ProtectorDeTenant(Guid tenantId) =>
        _dataProtectionProvider.CreateProtector("CaeManager.Archivos.v2", tenantId.ToString("N"));

    private byte[] Descifrar(byte[] bytesDisco, string identificador, Guid tenantId)
    {
        if (EmpiezaConMarcaV2(bytesDisco))
        {
            try
            {
                return ProtectorDeTenant(tenantId).Unprotect(bytesDisco[MarcaFormatoV2.Length..]);
            }
            catch (CryptographicException ex)
            {
                // Fallo CERRADO. La marca dice que esto se escribió cifrado,
                // así que un descifrado fallido no es un archivo antiguo: es
                // la clave equivocada, corrupción o manipulación. Servir los
                // bytes tal cual entregaría contenido no autenticado como si
                // fuera legítimo, que es exactamente lo que el cifrado existe
                // para impedir.
                _alertaOperativa.Emitir(
                    $"Un archivo cifrado del Tenant propietario {tenantId:N} no supera la verificación de integridad. " +
                    "Puede ser manipulación, corrupción o pérdida del material de claves de Data Protection; la huella está en el aviso del registro.",
                    NivelAlertaOperativa.Critica);
                _logger.LogWarning(
                    "Un archivo cifrado del Tenant propietario {TenantPropietarioId} no supera la verificación de integridad. Huella del archivo: {HuellaArchivo}.",
                    tenantId.ToString("N"), HuellaArchivo(identificador));

                throw new InvalidDataException(
                    "El archivo no supera la verificación de integridad y no se puede servir.", ex);
            }
        }

        // Sin marca: escrito antes del formato v2. Ver el comentario de clase.
        //
        // AMBAS salidas de aquí dejan constancia, y son dos poblaciones
        // distintas de archivo, no una:
        //
        //   · cifrado con el protector v1, sin marca — escrito entre el
        //     2a2c76dd (2026-08-01, cuando entró el cifrado en reposo) y el
        //     formato v2;
        //   · en claro, anterior al cifrado en reposo.
        //
        // Por la cronología, la segunda no debería existir en producción (el
        // primer despliegue real se fecha el 2026-08-24, posterior al cifrado
        // v1), pero eso no está comprobado sobre el volumen. La primera sí
        // puede haberla, y se servía en silencio, de modo que un registro sin
        // avisos se leía como "ya no queda nada legado" justo cuando quedaba
        // todo. Retirar la rama con esa lectura dejaría ilegible cada
        // documento subido antes del despliegue del formato v2. De ahí que se
        // registren las dos.
        try
        {
            var descifrado = _protectorLegado.Unprotect(bytesDisco);

            _logger.LogWarning(
                "Se sirvió un archivo legado cifrado con el protector v1 y sin marca de formato del Tenant propietario {TenantPropietarioId}. Huella del archivo: {HuellaArchivo}. " +
                "Mientras existan, la rama legada no se puede retirar: hacerlo dejaría este archivo ilegible.",
                tenantId.ToString("N"), HuellaArchivo(identificador));

            return descifrado;
        }
        catch (CryptographicException ex)
        {
            // Ni marca v2 ni payload v1 autenticado. Puede ser un legado en
            // claro, pero también un fichero escrito por quien tenga acceso al
            // volumen (ese contenido se entregaría como legítimo del Tenant
            // propietario) o un v1 cuya clave ya no está en el llavero (se
            // entregaría ciphertext como si fuera el documento). Las tres son
            // para mirarlas ya, no mañana: alerta crítica, como la rama v2.
            //
            // La alerta solo nombra al Tenant propietario: ni contenido ni
            // nombre de archivo, que en disco conserva la extensión que trajo
            // el original y esa puede ser personal. El mensaje es el mismo
            // para todos los archivos de un Tenant, así que una población
            // legada agrupa en una incidencia por Tenant en vez de abrir una
            // por archivo. El aviso del registro, a continuación, es el que
            // lleva la huella para localizarlo en el volumen.
            var mensajeAlerta =
                $"{(_rechazarLegadoEnClaro ? "Se rechazó" : "Se sirvió en claro")} un archivo sin cifrar ni marca de formato " +
                $"del Tenant propietario {tenantId:N}. Puede ser un legado en claro, manipulación del volumen de " +
                "almacenamiento o pérdida de la clave del protector v1; la huella está en el aviso del registro.";
            _alertaOperativa.Emitir(mensajeAlerta, NivelAlertaOperativa.Critica);

            if (_rechazarLegadoEnClaro)
            {
                _logger.LogWarning(
                    "Se rechazó un archivo sin cifrar ni marca de formato del Tenant propietario {TenantPropietarioId}. Huella del archivo: {HuellaArchivo}: " +
                    "AlmacenamientoArchivos:RechazarLegadoEnClaro está activo.",
                    tenantId.ToString("N"), HuellaArchivo(identificador));

                throw new InvalidDataException(
                    "El archivo no supera la verificación de integridad y no se puede servir.", ex);
            }

            _logger.LogWarning(
                "Se sirvió un archivo legado sin cifrar ni marca de formato del Tenant propietario {TenantPropietarioId}. Huella del archivo: {HuellaArchivo}. " +
                "Mientras existan, la rama legada no se puede retirar y un contenido manipulado no se distingue de uno antiguo.",
                tenantId.ToString("N"), HuellaArchivo(identificador));

            return bytesDisco;
        }
    }

    // SHA-256 de UTF-8("CaeManager.Archivos.Huella.v1:" + nombre en disco),
    // primeros 12 hexadecimales en minúsculas. El Tenant delimita la búsqueda.
    internal static string HuellaArchivo(string identificador)
    {
        var nombreEnDisco = Path.GetFileName(identificador.Split('/', 2)[1]);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("CaeManager.Archivos.Huella.v1:" + nombreEnDisco));
        return Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    private static bool EmpiezaConMarcaV2(byte[] contenido) =>
        contenido.Length >= MarcaFormatoV2.Length
        && contenido.AsSpan(0, MarcaFormatoV2.Length).SequenceEqual(MarcaFormatoV2);

    private string? ResolverRutaSegura(string identificador) =>
        _tenantActual.TenantId is { } tenantId ? ResolverRutaSegura(identificador, tenantId) : null;

    private string? ResolverRutaSegura(string identificador, Guid tenantId)
    {
        var partes = identificador.Split('/', 2);
        if (partes.Length != 2) return null;

        // El identificador nunca debe permitir escapar del directorio base
        // (path traversal) — se sanea cada segmento por separado.
        var carpetaSolicitada = Path.GetFileName(partes[0]);
        var nombreArchivo = Path.GetFileName(partes[1]);

        if (!string.Equals(carpetaSolicitada, CarpetaDeTenant(tenantId), StringComparison.Ordinal))
            return null;

        return Path.Combine(_rutaBase, carpetaSolicitada, nombreArchivo);
    }

    private static string CarpetaDeTenant(Guid tenantId) => tenantId.ToString("N");
}
