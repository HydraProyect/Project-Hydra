using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.PaqueteDocumental;
using CaeManager.Application.Empresas;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>Un PDF de la unión. El orden de la lista de <see cref="UnirPdfQuery.Entradas"/> es el del PDF unido.</summary>
public abstract record EntradaUnionPdf;

/// <summary>Un PDF que ha subido la persona. Su tamaño lo limita <see cref="LimitesArchivoSubido"/>.</summary>
public sealed record EntradaUnionSubida(byte[] Contenido, string NombreArchivo) : EntradaUnionPdf;

/// <summary>El archivo vigente de un Documento del Tenant; se lee aquí, dentro del alcance, y no lo trae la pantalla.</summary>
public sealed record EntradaUnionDocumento(Guid DocumentoId) : EntradaUnionPdf;

/// <summary>
/// Une varios PDF, subidos o tomados de Documentos de una Empresa gestionada, en uno nuevo que se entrega para descargar.
/// No guarda nada: el resultado no es un Documento. Autorización y alcance:
/// <list type="number">
/// <item>Tenant propietario: <paramref name="TenantEsperadoId"/> es el del inicio de la pantalla; si el selector cambió, se rechaza.</item>
/// <item>La Empresa gestionada es una Empresa propia del Tenant dentro de la cartera de quien une (alcance de gestión).</item>
/// <item>Cada Documento tiene que estar en el universo de esa Empresa (ver <see cref="DocumentosUnibles"/>) y ser legible con
/// la misma puerta que abre un Documento suelto (<c>DocumentosLegiblesAsync</c>). Lo que no, responde como inexistente.</item>
/// <item>Rol: solo los que escriben leen documentos para sacarlos de TALVEG (lista blanca, como el paquete de Empresa 360); una
/// Sesión Privilegiada de plataforma no lee Documentos aquí (la misma decisión provisional que ese paquete).</item>
/// </list>
/// DEC-36: cada Documento cuyo archivo se lee cuenta como una apertura, igual que una descarga normal, salga o no el PDF
/// unido (un PDF rechazado o una firma sin confirmar también revelan algo del Documento), y se registra antes de devolver
/// nada si es sensible: uno de un tipo <c>SinDatosPersonales</c> no deja fila («nunca registrar de más», lo decide
/// <see cref="IRegistroAccesoDocumentoSensibleService.RegistrarSiSensibleAsync(Guid, TipoAccesoDocumentoSensible, CancellationToken)"/>).
/// El registro falla en cerrado, es atómico y no se cancela con la petición: un Documento ya leído deja su constancia
/// aunque quien pidió se haya ido. Un Documento que no se llegó a leer no deja registro.
///
/// Memoria (OWASP API4:2023): cada Documento se abre solo si su tamaño almacenado cabe en lo que queda del total, la copia
/// se corta al pasar de ahí y lo que la unión retiene de cada PDF cuenta en el presupuesto de memoria de documentos. El PDF
/// unido sigue contando después: su reserva va en <see cref="ResultadoUnionPdf.Reserva"/> y la devuelve quien lo entrega.
/// </summary>
/// <param name="FirmasConfirmadas">
/// Si algún PDF lleva firma electrónica, sin esto no se une: se responde <see cref="EstadoUnionPdf.FirmasSinConfirmar"/> con
/// las posiciones afectadas y su <see cref="ResultadoUnionPdf.HuellaFirmas"/>, sin unir nada (los Documentos ya leídos para
/// comprobarlo sí dejan su registro de apertura). Para unir hay que devolver esa huella: liga la confirmación a los PDF
/// firmados que se mostraron (posición, origen y contenido). Si cambia cualquiera, se vuelve a pedir.
/// </param>
public record UnirPdfQuery(
    IReadOnlyList<EntradaUnionPdf> Entradas, Guid TenantEsperadoId, Guid EmpresaGestionadaId, string? FirmasConfirmadas)
    : IRequest<Result<ResultadoUnionPdf>>;

public enum EstadoUnionPdf
{
    Unido = 0,

    /// <summary>Hay PDF firmados y no se confirmó: <see cref="ResultadoUnionPdf.Posiciones"/> dice cuáles.</summary>
    FirmasSinConfirmar = 1,

    /// <summary>Un PDF no sirve: <see cref="ResultadoUnionPdf.Posiciones"/> dice cuál y <see cref="ResultadoUnionPdf.CodigoRechazo"/> por qué.</summary>
    EntradaRechazada = 2
}

/// <summary>
/// Lo que responde la unión. Con <see cref="EstadoUnionPdf.Unido"/>, el PDF unido sigue en memoria hasta que quien lo
/// entrega termina, y por eso trae su reserva del presupuesto de memoria de documentos (<see cref="Reserva"/>): quien recibe
/// el resultado lo desecha cuando ya no retiene <see cref="Contenido"/> (la descarga terminó, falló o la pantalla se fue).
/// Desecharlo más de una vez no devuelve nada de más.
/// </summary>
/// <param name="Posiciones">Posiciones (desde 0) en la lista de entradas a las que se refiere el estado.</param>
/// <param name="HuellaFirmas">Con <see cref="EstadoUnionPdf.FirmasSinConfirmar"/>, lo que se devuelve en <see cref="UnirPdfQuery.FirmasConfirmadas"/> para unir esos mismos PDF.</param>
public record ResultadoUnionPdf(
    EstadoUnionPdf Estado, byte[]? Contenido, string? NombreArchivo, int NumeroPaginas,
    IReadOnlyList<int> Posiciones, string? CodigoRechazo, string? HuellaFirmas = null) : IDisposable
{
    /// <summary>La reserva que cubre <see cref="Contenido"/> mientras alguien lo retiene; <c>null</c> si no hay PDF unido.</summary>
    public IDisposable? Reserva { get; init; }

    public void Dispose()
    {
        Reserva?.Dispose();
        GC.SuppressFinalize(this);
    }

    public static ResultadoUnionPdf Rechazada(int posicion, string codigo) =>
        new(EstadoUnionPdf.EntradaRechazada, null, null, 0, [posicion], codigo);

    public static ResultadoUnionPdf ConFirmas(IReadOnlyList<int> posiciones, string huella) =>
        new(EstadoUnionPdf.FirmasSinConfirmar, null, null, 0, posiciones, null, huella);
}

public class UnirPdfQueryHandler(
    IDisponibilidadHerramientasPdf disponibilidad,
    ITenantActual tenantActual,
    IEmpresasQueryContext empresasContext,
    IDocumentosQueryContext documentosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IProyectosQueryContext proyectosContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IFileStorageService almacenamiento,
    IPresupuestoMemoriaDocumentos presupuestoMemoria,
    IUnidorPdf unidor,
    IRegistroAccesoDocumentoSensibleService registroAcceso,
    ITransaccionDeComando transaccion,
    ILogger<UnirPdfQueryHandler> logger)
    : IRequestHandler<UnirPdfQuery, Result<ResultadoUnionPdf>>
{
    public async Task<Result<ResultadoUnionPdf>> Handle(UnirPdfQuery request, CancellationToken cancellationToken)
    {
        var abiertos = new HashSet<Guid>();
        Result<ResultadoUnionPdf> resultado;
        try
        {
            resultado = await UnirAsync(request, abiertos, cancellationToken);
        }
        catch (Exception excepcion)
        {
            // Una excepción tras leer un Documento no borra la lectura: queda constancia y la excepción sigue su camino.
            if (abiertos.Count > 0)
            {
                try
                {
                    await RegistrarAperturasAsync(abiertos);
                }
                catch (Exception falloDelRegistro) when (excepcion is EntradaPdfRetenidaException)
                {
                    // La retención manda: si un fallo del registro la sustituyera, la pantalla devolvería su reserva con
                    // los PDF quizá aún referenciados. El PDF no sale, pero los Documentos ya se leyeron y pueden quedar sin
                    // constancia de su apertura (DEC-36): el fallo no se calla, se registra como crítico para quien supervisa.
                    try
                    {
                        logger.LogCritical(
                            falloDelRegistro,
                            "Unir PDF no pudo registrar la apertura de {NumeroDocumentos} Documentos ya leídos ({Documentos}) tras retener la entrada del proceso aparte.",
                            abiertos.Count, string.Join(",", abiertos));
                    }
                    catch (Exception)
                    {
                        // Un proveedor de registro que lanza tampoco sustituye a la retención.
                    }
                }
            }

            throw;
        }

        if (abiertos.Count == 0)
            return resultado;

        Result confirmada;
        try
        {
            confirmada = await RegistrarAperturasAsync(abiertos);
        }
        catch
        {
            // Sin constancia no sale el PDF, y nadie más va a desechar su reserva.
            SoltarReserva(resultado);
            throw;
        }

        if (!confirmada.EsFallido)
            return resultado;

        SoltarReserva(resultado);
        return Result.Fallo<ResultadoUnionPdf>(confirmada.Error);
    }

    private static void SoltarReserva(Result<ResultadoUnionPdf> resultado)
    {
        if (resultado.EsExitoso)
            resultado.Valor.Dispose();
    }

    /// <summary>
    /// DEC-36: leer el archivo de un Documento es una apertura, salga o no el PDF unido (un PDF rechazado, una firma sin
    /// confirmar o un tope superado también revelan algo de él). Todas las aperturas o ninguna, y antes de devolver nada:
    /// sin constancia no hay resultado. La señal de extracción masiva se emite solo tras confirmar (ver
    /// DescargarPaqueteEmpresaQueryHandler, del que se copia el mecanismo).
    ///
    /// Sin el token de la petición, como el registro tras una excepción: los Documentos ya se leyeron, y una desconexión
    /// entre la lectura y la confirmación no puede dejarlos sin constancia ni deshacerla a medias.
    /// </summary>
    private async Task<Result> RegistrarAperturasAsync(IReadOnlyCollection<Guid> abiertos)
    {
        ILoteAccesosSensibles? lote = null;
        var confirmada = await transaccion.EjecutarAsync(async ct =>
        {
            lote = registroAcceso.IniciarLote();
            foreach (var documentoId in abiertos)
                await lote.RegistrarSiSensibleAsync(documentoId, TipoAccesoDocumentoSensible.Apertura, ct);
            return Result.Exito();
        }, CancellationToken.None);

        if (confirmada.EsFallido)
            return confirmada;
        if (lote is not null)
            await lote.EmitirSenalesAsync(CancellationToken.None);
        return confirmada;
    }

    private async Task<Result<ResultadoUnionPdf>> UnirAsync(UnirPdfQuery request, HashSet<Guid> abiertos, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Fallo(ErroresUnirPdf.NoDisponible, "Herramientas PDF no está disponible.");

        if (tenantActual.TenantId is not { } tenant || tenant != request.TenantEsperadoId)
            return Fallo(ErroresUnirPdf.TenantCruzado, "La Empresa ha cambiado. Vuelve a empezar para no unir documentos de otra.");

        var entradas = request.Entradas;
        if (entradas.Count is < LimitesUnionPdf.MinimoFicheros or > LimitesUnionPdf.MaximoFicheros)
            return Fallo(ErroresUnirPdf.NumeroDeFicherosInvalido, "Hay que unir entre 2 y 20 PDF.");

        var empresa = await DocumentosUnibles.EmpresaGestionableAsync(request.EmpresaGestionadaId, empresasContext, alcanceDatos, cancellationToken);
        if (empresa is null)
            return Fallo(ErroresUnirPdf.EmpresaNoGestionable, "La Empresa no existe o queda fuera de tu cartera.");

        var documentoIds = entradas.OfType<EntradaUnionDocumento>().Select(e => e.DocumentoId).Distinct().ToList();
        Dictionary<Guid, string> archivos = [];
        if (documentoIds.Count > 0)
        {
            // Tenant propietario y alcance antes de abrir nada. Solo lo que lee Documentos pide rol y descarta la Sesión Privilegiada.
            if (await DocumentosUnibles.RechazoDeLecturaAsync(sesionPrivilegiadaActual, currentUserService, cancellationToken) is { } rechazoDeLectura)
                return Result.Fallo<ResultadoUnionPdf>(rechazoDeLectura);

            archivos = await ArchivosDeLosDocumentosAsync(request.EmpresaGestionadaId, documentoIds, cancellationToken);
        }

        // Se lee y se valida en el orden pedido para que el primer PDF que no sirve sea el que se señala.
        var contenidos = new List<byte[]>(entradas.Count);
        var infos = new List<InfoPdfParaUnir>(entradas.Count);
        var lecturaDeDocumentos = new Dictionary<Guid, LecturaAlmacenadaParaAnalisis>();
        var reservas = new List<IDisposable>();
        long total = 0;
        var paginas = 0;

        // Las reservas que cubren lo que tiene el proceso aparte en curso: si no consta que termine, solo esas se retienen.
        // Las de una inspección ya terminada cubren PDF que ningún proceso vivo referencia y se devuelven en el finally.
        IReadOnlyList<IDisposable> enElProceso = [];

        // Las posiciones cuyos PDF tiene ese proceso, para que la pantalla retenga solo lo suyo de esas: la de la entrada que
        // se inspecciona, o null (todas) en la unión o si no se sabe.
        IReadOnlyList<int>? posicionesEnElProceso = null;

        try
        {
            for (var posicion = 0; posicion < entradas.Count; posicion++)
            {
                var restante = LimitesUnionPdf.TamanoMaximoTotalBytes - total;
                var reservasAntes = reservas.Count;
                var leido = await LeerAsync(entradas[posicion], archivos, lecturaDeDocumentos, abiertos, restante, reservas, cancellationToken);
                if (leido.Rechazo is { } rechazo)
                {
                    // Un Documento que no cabe en lo que queda tras los anteriores: lo que se pasa es el total.
                    return rechazo == NoCabeEnElTotal
                        ? Fallo(ErroresUnirPdf.TotalDemasiadoGrande, "Los PDF pesan juntos más de lo que se puede unir.")
                        : Result.Exito(ResultadoUnionPdf.Rechazada(posicion, rechazo));
                }

                var contenido = leido.Contenido!;
                total += contenido.Length;
                if (total > LimitesUnionPdf.TamanoMaximoTotalBytes)
                    return Fallo(ErroresUnirPdf.TotalDemasiadoGrande, "Los PDF pesan juntos más de lo que se puede unir.");

                enElProceso = ReservasDeLaEntrada(entradas[posicion], reservas, reservasAntes, lecturaDeDocumentos);
                posicionesEnElProceso = [posicion];
                var info = await unidor.InspeccionarAsync(contenido, cancellationToken);
                enElProceso = [];
                posicionesEnElProceso = null;
                if (!info.EsExitoso)
                    return Result.Exito(ResultadoUnionPdf.Rechazada(posicion, CodigoDeEntrada(info.Error.Codigo)));

                // Lo que PDFsharp descomprime de su estructura al abrirlo también se retiene mientras dura la unión. Ocurre en
                // el proceso aparte, pero ese proceso vive en el mismo contenedor y comparte su techo de memoria.
                if (info.Valor.BytesEstructura > 0)
                    reservas.Add(Reservar(info.Valor.BytesEstructura));

                paginas += info.Valor.NumeroPaginas;
                if (paginas > LimitesUnionPdf.MaximoPaginasTotales)
                    return Fallo(ErroresUnirPdf.TotalDemasiadasPaginas, "El PDF unido pasaría del máximo de páginas.");

                contenidos.Add(contenido);
                infos.Add(info.Valor);
            }

            var firmados = infos.Select((info, posicion) => (info, posicion)).Where(x => x.info.EstaFirmado).Select(x => x.posicion).ToList();
            if (firmados.Count > 0)
            {
                var huella = HuellaDeFirmas(entradas, contenidos, firmados);
                if (!string.Equals(request.FirmasConfirmadas, huella, StringComparison.Ordinal))
                    return Result.Exito(ResultadoUnionPdf.ConFirmas(firmados, huella));
            }

            // El proceso de la unión recibe todos los PDF y vuelve a abrir su estructura: le corresponde todo lo reservado.
            enElProceso = [.. reservas];
            var unido = await unidor.UnirAsync(contenidos, cancellationToken);
            enElProceso = [];
            if (!unido.EsExitoso)
                return Result.Exito(ResultadoUnionPdf.Rechazada(0, CodigoDeEntrada(unido.Error.Codigo)));

            var nombre = $"{ArmadoPaqueteDocumental.SanearSegmento(empresa.RazonSocial).Replace(' ', '-')}_unido_{DiaDeNegocio.Hoy():yyyy-MM-dd}.pdf";

            // El PDF unido sobrevive a esta unión: lo retiene quien lo entrega (la descarga) cuando las reservas de arriba ya
            // se han devuelto. Se reserva ahora, sin esperar y mientras aún se retienen las demás (un instante cuenta dos
            // veces, del lado seguro), y la reserva viaja con el resultado: la devuelve quien lo desecha, no este finally.
            var reservaUnido = Reservar(unido.Valor.Contenido.LongLength);
            return Result.Exito(new ResultadoUnionPdf(EstadoUnionPdf.Unido, unido.Valor.Contenido, nombre, unido.Valor.NumeroPaginas, [], null)
            {
                Reserva = reservaUnido,
            });
        }
        catch (EntradaPdfRetenidaException retenida)
        {
            // El proceso aparte que recibió estos PDF no consta terminado: pueden seguir referenciados, así que lo reservado
            // para ellos no se devuelve (ni en el finally). Lo de inspecciones anteriores, cuyo proceso sí consta terminado, se
            // devuelve: retenerlo dejaría inmovilizado presupuesto compartido sin nada que lo use. La excepción sigue, con las
            // posiciones de ese proceso, para que la pantalla retenga también lo suyo de esas y devuelva lo de las demás.
            foreach (var reserva in enElProceso)
            {
                EntradaPdfRetenidaException.Retener(reserva);
                reservas.Remove(reserva);
            }

            if (posicionesEnElProceso is null)
                throw;
            throw new EntradaPdfRetenidaException(posicionesEnElProceso, retenida);
        }
        catch (PresupuestoMemoriaAgotadoException)
        {
            // Transitorio: sin hueco en el presupuesto de memoria de documentos del proceso. Los Documentos ya leídos sí
            // dejan su registro (lo hace Handle); uno que no llegó a leerse, no.
            return Fallo(ErroresUnirPdf.SinEspacio, "No hay memoria libre para unir ahora mismo. Inténtalo de nuevo en unos segundos.");
        }
        finally
        {
            foreach (var reserva in reservas)
                reserva.Dispose();
        }
    }

    /// <summary>
    /// Huella de los PDF firmados tal como se mostraron: por cada uno, su posición, de dónde viene (el Id del Documento o
    /// una subida) y el SHA-256 de su contenido. Si cambia el orden, se quita o se añade uno firmado, o el archivo vigente de
    /// un Documento es otro, la huella cambia y la confirmación anterior ya no vale.
    /// </summary>
    internal static string HuellaDeFirmas(IReadOnlyList<EntradaUnionPdf> entradas, IReadOnlyList<byte[]> contenidos, IReadOnlyList<int> firmados)
    {
        var texto = new StringBuilder();
        foreach (var posicion in firmados)
        {
            var origen = entradas[posicion] is EntradaUnionDocumento documento ? documento.DocumentoId.ToString("N") : "subida";
            texto.Append(posicion).Append(':').Append(origen).Append(':')
                .Append(Convert.ToHexString(SHA256.HashData(contenidos[posicion]))).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto.ToString())));
    }

    /// <summary>
    /// Las reservas que cubren lo que recibe el proceso que inspecciona <paramref name="entrada"/>: las que añadió su lectura
    /// (desde <paramref name="desde"/>) y, si es un Documento repetido, también la lectura que cubre su array, que es el mismo.
    /// </summary>
    private static List<IDisposable> ReservasDeLaEntrada(
        EntradaUnionPdf entrada, List<IDisposable> reservas, int desde, Dictionary<Guid, LecturaAlmacenadaParaAnalisis> lecturas)
    {
        var deLaEntrada = reservas.GetRange(desde, reservas.Count - desde);
        if (entrada is EntradaUnionDocumento documento && lecturas.TryGetValue(documento.DocumentoId, out var lectura)
            && !deLaEntrada.Contains(lectura))
            deLaEntrada.Add(lectura);
        return deLaEntrada;
    }

    /// <summary>Reserva sin esperar: la unión ya retiene otras y esperar reteniendo bloquea a las demás (ver <see cref="IPresupuestoMemoriaDocumentos.TryReservar"/>).</summary>
    private ReservaMemoriaDocumentos Reservar(long bytes) =>
        presupuestoMemoria.TryReservar(bytes, out var reserva) ? reserva : throw new PresupuestoMemoriaAgotadoException();

    /// <summary>Id de Documento a clave de su archivo, solo de los que están en el universo de la Empresa y que esta sesión puede leer.</summary>
    private async Task<Dictionary<Guid, string>> ArchivosDeLosDocumentosAsync(
        Guid empresaId, List<Guid> documentoIds, CancellationToken cancellationToken)
    {
        var trabajadorIds = await DocumentosUnibles.TrabajadorIdsAsync(empresaId, trabajadoresContext, alcanceDatos, cancellationToken);
        var enUniverso = await DocumentosUnibles.De(documentosContext, empresaId, trabajadorIds)
            .Where(d => documentoIds.Contains(d.Id))
            .Select(d => new { d.Id, ArchivoUrl = d.ArchivoUrl! })
            .ToListAsync(cancellationToken);

        var legibles = await alcanceDatos.DocumentosLegiblesAsync(
            enUniverso.Select(d => d.Id).ToList(), documentosContext, proyectosContext, cancellationToken);

        return enUniverso.Where(d => legibles.Contains(d.Id)).ToDictionary(d => d.Id, d => d.ArchivoUrl);
    }

    /// <param name="restante">Bytes que quedan del total de la unión: un Documento que no cabe ahí no se carga.</param>
    /// <param name="reservas">Donde se dejan las reservas de memoria de lo que se lee; las devuelve quien llama, al terminar.</param>
    private async Task<(byte[]? Contenido, string? Rechazo)> LeerAsync(
        EntradaUnionPdf entrada, Dictionary<Guid, string> archivos, Dictionary<Guid, LecturaAlmacenadaParaAnalisis> yaLeidos, HashSet<Guid> abiertos,
        long restante, List<IDisposable> reservas, CancellationToken cancellationToken)
    {
        switch (entrada)
        {
            case EntradaUnionSubida subida:
                var rechazo = ValidacionPdfSubidoParaUnir.Rechazo(subida.Contenido, subida.NombreArchivo);
                if (rechazo is not null)
                    return (null, rechazo);
                // La pantalla ya reserva una vez lo subido; aquí, lo que PDFsharp deriva de él.
                reservas.Add(Reservar((LimitesUnionPdf.FactorMemoriaUnion - 1L) * subida.Contenido.Length));
                return (subida.Contenido, null);

            case EntradaUnionDocumento documento:
                if (yaLeidos.TryGetValue(documento.DocumentoId, out var repetido))
                {
                    // El mismo array, pero PDFsharp lo vuelve a abrir al unir.
                    reservas.Add(Reservar((LimitesUnionPdf.FactorMemoriaUnion - 1L) * repetido.Contenido.Length));
                    return (repetido.Contenido, null);
                }
                if (!archivos.TryGetValue(documento.DocumentoId, out var clave))
                    return (null, ErroresUnirPdf.DocumentoNoDisponible);

                LecturaAlmacenadaParaAnalisis lectura;
                try
                {
                    // Lo almacenado puede pasar del tope por archivo de una subida (un PDF unificado), pero nunca de lo que
                    // queda del total: se comprueba con el tamaño almacenado, antes de cargarlo, y la copia se corta ahí.
                    lectura = await LecturaAlmacenadaParaAnalisis.LeerHastaAsync(
                        almacenamiento, presupuestoMemoria, clave, restante, LimitesUnionPdf.FactorMemoriaUnion, cancellationToken);
                }
                catch (ArchivoAlmacenadoDemasiadoGrandeException)
                {
                    return (null, restante < LimitesUnionPdf.TamanoMaximoTotalBytes ? NoCabeEnElTotal : ErroresUnirPdf.DemasiadoGrande);
                }
                catch (FileNotFoundException)
                {
                    return (null, ErroresUnirPdf.ArchivoNoDisponible);
                }

                reservas.Add(lectura);
                abiertos.Add(documento.DocumentoId);
                var contenido = lectura.Contenido;
                if (contenido.Length == 0 || !ValidadorFirmaArchivo.TieneFirmaValida(contenido, "documento.pdf"))
                    return (null, ErroresUnirPdf.NoEsPdf);

                yaLeidos[documento.DocumentoId] = lectura;
                return (contenido, null);

            default:
                return (null, ErroresUnirPdf.NoEsPdf);
        }
    }

    /// <summary>Marca interna de <see cref="LeerAsync"/>: el Documento no cabe en lo que queda del total; no sale de aquí.</summary>
    private const string NoCabeEnElTotal = "UnirPdf.Interno.NoCabeEnElTotal";

    private static string CodigoDeEntrada(string codigo) => codigo switch
    {
        ErroresUnirPdf.ConContrasena or ErroresUnirPdf.DemasiadoGrande or ErroresUnirPdf.DemasiadasPaginas
            or ErroresUnirPdf.DemasiadoComplejo => codigo,
        _ => ErroresUnirPdf.Ilegible,
    };

    private static Result<ResultadoUnionPdf> Fallo(string codigo, string mensaje) =>
        Result.Fallo<ResultadoUnionPdf>(Error.Crear(codigo, mensaje));
}
