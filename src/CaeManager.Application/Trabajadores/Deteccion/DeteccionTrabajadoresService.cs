using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Deteccion;

/// <summary>
/// Compara el listado de trabajadores extraído por IA de un Documento de
/// Empresa (p. ej. ITA, RNT/TC2) contra los Trabajadores activos de esa
/// misma Empresa (nunca de Subcontrata — tiene su propia documentación
/// separada, y no aparece en documentos de Seguridad Social de la Empresa
/// contratista), y registra una DeteccionTrabajador pendiente por cada
/// discrepancia: alguien en el documento sin alta (Nuevo), o alguien de
/// alta que ya no aparece (Ausente). Solo corre si el TipoDocumento tiene
/// la lectura IA activa a Nivel 1 (Administrador) y, cuando la Empresa
/// presta servicio a algún Cliente, si al menos uno de esos Clientes no la
/// tiene desactivada a Nivel 2 — ver ConfiguracionIaDocumentoCliente
/// (Fase 35). Si ya hay una detección pendiente sin resolver para el mismo
/// documento, no duplica.
///
/// <b>No confundir "no aplica" con "falló"</b> (mismo criterio que
/// VerificacionIaDocumentoService, D3): los primeros chequeos de
/// <see cref="ProcesarDocumentoAsync"/> (Documento sin archivo/Empresa,
/// TipoDocumento sin detección activa, todos los Clientes la tienen
/// desactivada, detección ya pendiente) terminan en un <c>return</c>
/// silencioso a propósito — son casos legítimos donde no hay nada que
/// detectar. Pero no abrir el archivo o que el proveedor de IA devuelva un
/// <c>Result</c> fallido SÍ es un fallo real de la detección, y por eso
/// ambos casos lanzan en vez de retornar: dejan que
/// <c>ProcesadorAnalisisDocumentoHostedService</c> (Infrastructure, que ya
/// sabe reintentar, capturar en Sentry y avisar sin mentir) lo trate como lo
/// que es, en vez de que <c>MarcarCompletado()</c> + la campana "Detección
/// de personal terminada" mientan sobre un documento que nunca llegó a
/// leerse.
///
/// <b>El listado extraído no se trata como un hecho</b>: sale de un PDF que
/// un tercero pudo preparar y de una lectura que pudo fallar, así que antes
/// de decidir un alta o una baja se deduplica, se acota su tamaño (ver
/// <c>MaximoTrabajadoresPorDocumento</c>), se exige dígito de control válido
/// para proponer altas y —sobre todo— se descarta como fallo de lectura el
/// caso en que ninguno de los trabajadores de alta aparece en el documento,
/// que antes producía la baja simultánea de la plantilla entera.
/// </summary>
public class DeteccionTrabajadoresService(
    IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext,
    IFileStorageService almacenamiento,
    IExtraccionTrabajadoresIaService extraccion,
    IDeteccionTrabajadorRepository deteccionRepositorio,
    INotificacionUsuarioRepository notificacionRepositorio,
    IUnitOfWork unitOfWork) : IDeteccionTrabajadoresService
{
    public async Task ProcesarDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default)
    {
        var documento = await documentosContext.Documentos.FirstOrDefaultAsync(d => d.Id == documentoId, cancellationToken);

        if (documento is null || documento.EmpresaId is null || string.IsNullOrWhiteSpace(documento.ArchivoUrl))
            return;

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == documento.TipoDocumentoId, cancellationToken);

        if (tipoDocumento is null || !tipoDocumento.LecturaIaActiva || !tipoDocumento.DeteccionTrabajadoresActiva)
            return;

        var empresaId = documento.EmpresaId.Value;

        // F4.2c — la arista sustituye a la tabla puente. El JOIN discriminador
        // (EsCritico != null) hace explícito que aquí solo cuentan Clientes
        // reales: RelacionEmpresarial.ClienteId contiene una Empresa propia en
        // la shape Subcontrata→Empresa, y la configuración de lectura IA por
        // cliente no aplica a esas.
        var clientesVinculados = await (
            from r in empresasContext.RelacionesEmpresariales
            where r.ProveedoraId == empresaId && r.VigenciaHasta == null
            join c in empresasContext.Empresas.Where(e => e.EsCritico != null)
                on r.ClienteId equals c.Id
            select r.ClienteId)
            .ToListAsync(cancellationToken);

        if (clientesVinculados.Count > 0 && await TodosLosClientesLoTienenDesactivadoAsync(clientesVinculados, tipoDocumento.Id, cancellationToken))
            return;

        var yaHayDeteccionPendiente = await trabajadoresContext.DeteccionesTrabajador
            .AnyAsync(d => d.DocumentoId == documentoId && !d.Resuelta, cancellationToken);

        if (yaHayDeteccionPendiente)
            return;

        byte[] contenido;
        try
        {
            await using var archivo = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
            using var buffer = new MemoryStream();
            await archivo.CopyToAsync(buffer, cancellationToken);
            contenido = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            // Se relanza SIN envolver, a propósito: ver el mismo catch en
            // VerificacionIaDocumentoService (D3) — Disk/S3FileStorageService
            // ya normalizan a FileNotFoundException el único caso realmente
            // determinista (el archivo no existe o no resuelve a este
            // tenant), y no va a aparecer en un segundo intento.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cualquier otro fallo al abrir (red, el backend de
            // almacenamiento caído, un timeout) sí puede ser transitorio —
            // se envuelve para no perder el contexto, pero se deja
            // reintentar como el resto.
            throw new InvalidOperationException(
                $"No se pudo abrir el archivo del Documento {documentoId} para lectura IA.", ex);
        }

        var resultadoExtraccion = await extraccion.ExtraerAsync(contenido, cancellationToken);

        if (resultadoExtraccion.EsFallido)
        {
            // Mismo criterio que el catch de arriba: un Result fallido del
            // proveedor de IA no es "nada que detectar" — es un fallo real
            // de la detección.
            throw new InvalidOperationException(
                $"Lectura IA del Documento {documentoId} no disponible: {resultadoExtraccion.Error.Codigo} — {resultadoExtraccion.Error.Mensaje}");
        }

        // El listado que devuelve el modelo es una propuesta hostil, no un
        // hecho: sale de un PDF que un tercero pudo preparar y de un OCR que
        // pudo leer mal. Se deduplica y se acota antes de dejarle decidir
        // nada.
        var extraidos = resultadoExtraccion.Valor
            .Where(e => !string.IsNullOrWhiteSpace(e.Dni))
            .GroupBy(NormalizarDni)
            .Select(grupo => grupo.First())
            .ToList();

        if (extraidos.Count > MaximoTrabajadoresPorDocumento)
        {
            throw new InvalidOperationException(
                $"La lectura IA del Documento {documentoId} devolvió {extraidos.Count} trabajadores, " +
                $"por encima del máximo razonable de {MaximoTrabajadoresPorDocumento} para un documento de Seguridad Social.");
        }

        var dnisExtraidos = extraidos.Select(NormalizarDni).ToHashSet();

        var trabajadoresActivos = await trabajadoresContext.Trabajadores
            .Where(t => t.EmpresaId == empresaId && !t.EstaEliminado)
            .ToListAsync(cancellationToken);

        // Guarda contra la baja masiva. Antes, una extracción vacía —el
        // desenlace más común de un OCR que falla sobre un escaneado malo, y
        // también el que produciría un PDF preparado para que el modelo no
        // devuelva nada— dejaba dnisExtraidos vacío y marcaba como Ausente a
        // TODOS los trabajadores activos de la Empresa, con su notificación al
        // gestor. Que ninguno de los trabajadores de alta aparezca en un ITA o
        // un RNT de esa misma Empresa no es una plantilla que se ha ido
        // entera: es un documento que no se ha leído, o que no habla de esta
        // plantilla. Se trata como el fallo de detección que es, y así el
        // usuario recibe "Detección de personal no disponible — revísalo tú
        // manualmente" en vez de una cola de bajas inventadas.
        //
        // El criterio es la intersección vacía, no un porcentaje: no hace
        // falta elegir un umbral arbitrario para separar la rotación real
        // (donde los que siguen de alta sí aparecen) del fallo de lectura.
        if (trabajadoresActivos.Count > 0 && !trabajadoresActivos.Any(t => dnisExtraidos.Contains(NormalizarDni(t.Dni))))
        {
            throw new InvalidOperationException(
                $"La lectura IA del Documento {documentoId} no reconoció a ninguno de los {trabajadoresActivos.Count} " +
                "trabajadores de alta de la Empresa: se descarta como fallo de lectura en vez de proponer su baja.");
        }

        // Altas solo con identificador verificable: un DNI/NIE con dígito de
        // control correcto es la prueba más barata de que la fila salió del
        // documento y no de una alucinación o de una instrucción incrustada.
        // Las bajas, en cambio, se calculan contra TODOS los DNI extraídos,
        // válidos o no — un pasaporte u otro identificador que este validador
        // no reconoce sigue sirviendo para confirmar que ese trabajador está
        // en el documento, y descartarlo lo convertiría en una baja falsa.
        var nuevos = extraidos
            .Where(e => EsDocumentoIdentidadValido(e.Dni))
            .Where(e => trabajadoresActivos.All(t => NormalizarDni(t.Dni) != NormalizarDni(e.Dni)))
            .ToList();

        var ausentes = trabajadoresActivos.Where(t => !dnisExtraidos.Contains(NormalizarDni(t.Dni))).ToList();

        if (nuevos.Count == 0 && ausentes.Count == 0)
            return;

        foreach (var nuevo in nuevos)
            deteccionRepositorio.Agregar(DeteccionTrabajador.Nuevo(documentoId, empresaId, nuevo.Nombre, nuevo.Apellidos, nuevo.Dni));

        foreach (var ausente in ausentes)
            deteccionRepositorio.Agregar(DeteccionTrabajador.Ausente(documentoId, empresaId, ausente.Id, ausente.Nombre, ausente.Apellidos, ausente.Dni));

        await NotificarGestoresAsync(empresaId, tipoDocumento, clientesVinculados, nuevos.Count, ausentes.Count, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> TodosLosClientesLoTienenDesactivadoAsync(
        IReadOnlyList<Guid> clientesVinculados, Guid tipoDocumentoId, CancellationToken cancellationToken)
    {
        var overrides = await tiposDocumentoContext.ConfiguracionesIaDocumentoCliente
            .Where(c => c.TipoDocumentoId == tipoDocumentoId && clientesVinculados.Contains(c.ClienteId))
            .ToListAsync(cancellationToken);

        return clientesVinculados.All(clienteId => overrides.Any(o => o.ClienteId == clienteId && !o.Activa));
    }

    private async Task NotificarGestoresAsync(
        Guid empresaId, TipoDocumento tipoDocumento, IReadOnlyList<Guid> clientesVinculados,
        int nuevos, int ausentes, CancellationToken cancellationToken)
    {
        var empresa = await empresasContext.Empresas.FirstOrDefaultAsync(e => e.Id == empresaId, cancellationToken);

        var gestoresANotificar = await empresasContext.Empresas
            .Where(c => clientesVinculados.Contains(c.Id) && c.EjecutivoUsuarioId != null)
            .Select(c => c.EjecutivoUsuarioId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var partes = new List<string>();
        if (nuevos > 0) partes.Add($"{nuevos} nuevo(s)");
        if (ausentes > 0) partes.Add($"{ausentes} ausente(s)");

        var mensaje = $"Se detectaron posibles cambios de personal en \"{empresa?.RazonSocial}\" a partir de un documento {tipoDocumento.Nombre}: {string.Join(", ", partes)}.";

        foreach (var gestorId in gestoresANotificar)
            notificacionRepositorio.Agregar(new NotificacionUsuario(
                gestorId, "Cambios de personal detectados", mensaje,
                urlAccion: $"/empresas/{empresaId}/deteccion-trabajadores", textoAccion: "Revisar"));
    }

    /// <summary>
    /// Tope de cardinalidad del listado extraído. Un ITA o un RNT de una sola
    /// Empresa no lista decenas de miles de personas; una respuesta así es una
    /// alucinación o una respuesta envenenada, y procesarla significaría
    /// insertar esas filas en la cola de revisión del gestor.
    /// </summary>
    private const int MaximoTrabajadoresPorDocumento = 2000;

    private const string LetrasControlDni = "TRWAGMYFPDXBNJZSQVHLCKE";

    /// <summary>
    /// Dígito de control de DNI y NIE españoles (módulo 23). En el NIE la
    /// letra inicial vale como dígito: X→0, Y→1, Z→2.
    ///
    /// Solo se usa para decidir si se propone un ALTA — ver el comentario en
    /// <see cref="ProcesarDocumentoAsync"/> sobre por qué las bajas no pueden
    /// depender de esto. Un identificador que no sea DNI/NIE (pasaporte,
    /// documento extranjero) devuelve <c>false</c> aquí: es correcto para el
    /// uso que se le da, y por eso ese uso está acotado.
    /// </summary>
    private static bool EsDocumentoIdentidadValido(string documentoIdentidad)
    {
        var valor = NormalizarDni(documentoIdentidad).Replace("-", string.Empty).Replace(" ", string.Empty);

        if (valor.Length != 9)
            return false;

        var cuerpo = valor[..8];
        var letra = valor[8];

        if (cuerpo[0] is 'X' or 'Y' or 'Z')
            cuerpo = (char)('0' + (cuerpo[0] - 'X')) + cuerpo[1..];

        return int.TryParse(cuerpo, out var numero)
            && numero >= 0
            && letra == LetrasControlDni[numero % 23];
    }

    private static string NormalizarDni(string dni) => dni.Trim().ToUpperInvariant();
    private static string NormalizarDni(TrabajadorExtraidoDto trabajador) => NormalizarDni(trabajador.Dni);
}
