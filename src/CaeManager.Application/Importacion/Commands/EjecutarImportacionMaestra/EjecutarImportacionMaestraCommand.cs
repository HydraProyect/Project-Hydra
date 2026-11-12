using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Empresas;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Importacion;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Importacion.Commands.EjecutarImportacionMaestra;

/// <summary>
/// Escribe un plan ya analizado por <see cref="Queries.AnalizarPlantillaMaestraQuery"/> (Paso 1 del Importador).
///
/// <see cref="ReemplazarExistentes"/> elige entre dos modos sobre un registro que ya existe (clave natural: CIF,
/// identidad, Cliente empresarial + nombre de Centro): <b>Completar vacíos</b> (<c>false</c>, por defecto) solo rellena
/// lo que el registro tiene vacío y nunca sobrescribe un dato presente; <b>Reemplazar</b> (<c>true</c>) deja ganar al
/// archivo cuando trae valor. En Reemplazar, cada valor no vacío que se sobrescribe se devuelve en
/// <see cref="ResultadoImportacionMaestraDto.ValoresSobrescritos"/> (hoja, fila, campo y valor previo): decisión N14,
/// sin reversión en v1. Una celda vacía del archivo nunca borra un dato.
///
/// Idempotencia: misma que la plantilla combinada — <c>Plan.OperacionId</c> y el índice único de
/// <see cref="OperacionImportacion"/> en el MISMO <c>SaveChangesAsync</c> que las altas. Un reintento de la estrategia
/// dentro de la misma confirmación devuelve lo que guardó su intento anterior solo si la marca guardada en la base es la
/// de ese intento (su commit llegó); en cualquier otro caso, una operación ya guardada devuelve
/// <see cref="ResultadoImportacionMaestraDto.YaEjecutada"/>, marcada como tal y sin informe.
/// Autorización: <see cref="IAutorizacionEscrituraEfectiva.EsActoDeAdministradorAsync"/>, como el resto de importaciones.
/// A diferencia de la plantilla combinada, NO es <see cref="IComandoDeAprovisionamiento"/>: ampliar esa lista explícita
/// es una decisión de autorización que este incremento no toma.
///
/// <see cref="HuellaVistaPrevia"/> es la <see cref="ResultadoImportacionMaestraDto.CalcularHuella"/> de la vista previa que
/// se mostró al confirmar. Lo que se escribe tiene que ser exactamente eso (N14): si al escribir, con los datos releídos,
/// el resultado difiere (otro Administrador del Tenant rellenó o cambió un campo, una razón social quedó ocupada…), no se
/// escribe nada y se devuelve <see cref="ErroresImportacionMaestra.VistaPreviaCambiada"/> para que se revise de nuevo.
///
/// Historial: a diferencia de las otras dos importaciones (que lo registran con <c>RegistrarHistorialImportacionCommand</c>
/// después, desde la página), el ÉXITO de esta lo registra el handler en el MISMO guardado que la marca de la operación y
/// las altas: una operación confirmada tiene siempre su entrada de éxito en el Historial, y «ya confirmada» puede remitir a
/// él. Los fallos (no se escribió nada) los sigue registrando la página. Revisión Codex de la PR #2164, r4, MEDIO 2.
/// </summary>
/// <param name="NombreArchivo">Nombre del archivo para la entrada de éxito del Historial; vacío, «(desconocido)»; se recorta a 260.</param>
public record EjecutarImportacionMaestraCommand(
    PlanImportacionMaestraDto Plan, bool ReemplazarExistentes, string HuellaVistaPrevia, string NombreArchivo)
    : ICommand<ResultadoImportacionMaestraDto>, Common.ILecturaRegistradaEnSesionPrivilegiada
{
    /// <summary>Valor de <c>HistorialImportacion.Plantilla</c> de esta importación: dato del Historial, no se localiza.</summary>
    public const string PlantillaHistorial = "Maestra";

    /// <summary>Lo que el Historial guarda como nombre del archivo (su columna admite 260 caracteres).</summary>
    public static string NombreArchivoHistorial(string? nombreArchivo) =>
        string.IsNullOrWhiteSpace(nombreArchivo) ? "(desconocido)" : nombreArchivo[..Math.Min(nombreArchivo.Length, 260)];
}

public static class ErroresImportacionMaestra
{
    /// <summary>Lo que se escribiría ya no es lo que mostró la vista previa: no se ha escrito nada.</summary>
    public static readonly Error VistaPreviaCambiada = Error.Crear(
        "Importacion.VistaPreviaCambiada",
        "Los datos han cambiado desde la vista previa y la importación ya no haría exactamente lo que se mostró. No se ha escrito nada: revisa la vista previa de nuevo antes de confirmar.");
}

/// <summary>
/// Vista previa SIN escribir (es un comando para pasar por la misma tubería de autorización que confirmar): devuelve exactamente lo que confirmar haría con este plan y este modo (cuántos campos se
/// rellenarían y qué valores previos se sobrescribirían), para el aviso de N14. Los valores previos pueden ser datos
/// personales: pasa por la misma autorización que el comando.
/// </summary>
public record PrevisualizarImportacionMaestraCommand(PlanImportacionMaestraDto Plan, bool ReemplazarExistentes)
    : ICommand<ResultadoImportacionMaestraDto>, Common.ILecturaRegistradaEnSesionPrivilegiada;

/// <param name="CamposRellenados">Campos que estaban vacíos y se rellenan (en cualquiera de los dos modos).</param>
/// <param name="ValoresSobrescritos">Solo en Reemplazar: un elemento por cada valor no vacío que el archivo sustituye.</param>
public record ResultadoImportacionMaestraDto(
    int EmpresasCreadas,
    int EmpresasActualizadas,
    int ClientesCreados,
    int ClientesActualizados,
    int RelacionesCreadas,
    int CentrosCreados,
    int CentrosActualizados,
    int TrabajadoresCreados,
    int TrabajadoresActualizados,
    int CamposRellenados,
    IReadOnlyList<ValorPrevioDto> ValoresSobrescritos,
    IReadOnlyList<ItemImportacionDto> Advertencias,
    IReadOnlyList<ItemImportacionDto> Omitidos)
{
    /// <summary>
    /// Confirmación repetida de una operación que ya estaba guardada cuando esta llamada no tiene en memoria lo que se
    /// escribió (otra llamada la confirmó: doble confirmación, o un reintento de la página tras un fallo). No se escribe
    /// nada y NO es un informe: <see cref="YaConfirmadaAntes"/> lo dice explícitamente y las cifras, todas a cero, no
    /// describen lo escrito; quien lo muestre remite al Historial. El reintento de la estrategia dentro de la MISMA
    /// confirmación no llega aquí: devuelve el resultado real de lo escrito. Revisión Codex de la PR #2164, r3, MEDIO 2.
    /// </summary>
    public static ResultadoImportacionMaestraDto YaEjecutada() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [])
    {
        YaConfirmadaAntes = true
    };

    /// <summary>
    /// La operación ya estaba guardada por otra confirmación y este resultado no trae lo que se escribió (ver
    /// <see cref="YaEjecutada"/>): sus cifras no son un informe.
    /// </summary>
    public bool YaConfirmadaAntes { get; init; }

    /// <summary>
    /// Un elemento por cada registro que se crea o se actualiza y por cada Relación Empresarial que se crea, en el orden
    /// en que se procesa: operación (<see cref="EscrituraImportacionDto.Alta"/>, <see cref="EscrituraImportacionDto.Actualizacion"/>
    /// o <see cref="EscrituraImportacionDto.Relacion"/>), hoja, fila y clave natural (CIF; CIF del Cliente empresarial y
    /// nombre del Centro; identidad del Trabajador; la pareja de CIF de la relación). Identifica QUÉ se escribe detrás de
    /// cada recuento: dos vistas previas con las mismas cifras pero altas o relaciones distintas (se iba a crear el Centro
    /// A y ahora el B, porque otra sesión creó A y dio de baja B) no se confunden. Solo entra en <see cref="CalcularHuella"/>.
    /// Revisión Codex de la PR #2164, r3, MEDIO 1.
    /// </summary>
    public IReadOnlyList<EscrituraImportacionDto> DetalleEscrituras { get; init; } = [];

    /// <summary>
    /// Un elemento por cada campo vacío que se rellena, en el orden en que se rellena (hoja, fila, campo y valor nuevo):
    /// identifica QUÉ se rellena, no solo cuántos (<see cref="CamposRellenados"/>). Solo entra en
    /// <see cref="CalcularHuella"/>: dos vistas previas con el mismo recuento pero distintos campos rellenados (rellenar el
    /// de una fila y vaciar el de otra) no se confunden. Revisión Codex de la PR #2164, r2, MEDIO 1.
    /// </summary>
    public IReadOnlyList<CampoRellenadoDto> DetalleCamposRellenados { get; init; } = [];

    /// <summary>
    /// Huella de TODO lo que este resultado dice que se hará: las cifras, cada alta, actualización y Relación Empresarial
    /// con su clave natural, cada campo que se rellena (hoja, fila, campo y valor nuevo), cada valor sobrescrito (con su
    /// valor previo y el nuevo), cada fila omitida con su motivo y cada advertencia. Ninguna cifra queda sin la lista que
    /// la identifica. Dos resultados con la misma huella muestran y escriben lo mismo. La hoja identifica la entidad y la
    /// fila, su clave natural dentro del mismo plan. Cada campo va precedido de su longitud, así que dos contenidos
    /// distintos nunca se serializan igual. Solo se compara en memoria; nunca se registra.
    /// </summary>
    public string CalcularHuella()
    {
        var texto = new StringBuilder();
        void Campo(string? valor) => texto.Append(valor?.Length ?? -1).Append(':').Append(valor).Append(';');
        void Numero(int valor) => Campo(valor.ToString(CultureInfo.InvariantCulture));
        void Items(IReadOnlyList<ItemImportacionDto> items)
        {
            Numero(items.Count);
            foreach (var i in items) { Campo(i.Hoja); Numero(i.Fila); Campo(i.Descripcion); Campo(i.Motivo); }
        }

        foreach (var cifra in new[]
                 {
                     EmpresasCreadas, EmpresasActualizadas, ClientesCreados, ClientesActualizados, RelacionesCreadas,
                     CentrosCreados, CentrosActualizados, TrabajadoresCreados, TrabajadoresActualizados, CamposRellenados
                 })
            Numero(cifra);
        Numero(DetalleEscrituras.Count);
        foreach (var w in DetalleEscrituras) { Campo(w.Operacion); Campo(w.Hoja); Numero(w.Fila); Campo(w.Clave); }
        Numero(DetalleCamposRellenados.Count);
        foreach (var r in DetalleCamposRellenados) { Campo(r.Hoja); Numero(r.Fila); Campo(r.Campo); Campo(r.ValorNuevo); }
        Numero(ValoresSobrescritos.Count);
        foreach (var v in ValoresSobrescritos) { Campo(v.Hoja); Numero(v.Fila); Campo(v.Campo); Campo(v.ValorPrevio); Campo(v.ValorNuevo); }
        Items(Advertencias);
        Items(Omitidos);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto.ToString())));
    }
}

public record ValorPrevioDto(string Hoja, int Fila, string Campo, string ValorPrevio, string? ValorNuevo);

public record CampoRellenadoDto(string Hoja, int Fila, string Campo, string ValorNuevo);

/// <param name="Operacion"><see cref="Alta"/>, <see cref="Actualizacion"/> o <see cref="Relacion"/>.</param>
/// <param name="Clave">Clave natural del registro escrito; en una relación, «CIF de la Empresa gestionada → CIF del Cliente empresarial».</param>
public record EscrituraImportacionDto(string Operacion, string Hoja, int Fila, string Clave)
{
    public const string Alta = "Alta";
    public const string Actualizacion = "Actualización";
    public const string Relacion = "Relación Empresarial";
}

public class EjecutarImportacionMaestraCommandHandler(
    IEmpresaRepository empresaRepositorio,
    IRelacionEmpresarialRepository relacionEmpresarialRepositorio,
    ICentroRepository centroRepositorio,
    ITrabajadorRepository trabajadorRepositorio,
    ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext,
    IOperacionImportacionRepository operacionImportacionRepositorio,
    IAutorizacionEscrituraEfectiva autorizacionEscrituraEfectiva,
    IAltaAcreditacionesPlataformaService altaAcreditaciones,
    ISerializacionAltasAcreditacionEmpresa serializacionAltas,
    IHistorialImportacionRepository historialRepositorio,
    ICurrentUserService usuarioActual,
    ILimiteEmpresaPropia limiteEmpresaPropia)
    : IRequestHandler<EjecutarImportacionMaestraCommand, Result<ResultadoImportacionMaestraDto>>,
      IRequestHandler<PrevisualizarImportacionMaestraCommand, Result<ResultadoImportacionMaestraDto>>
{
    private const string HojaEmpresas = "Empresas";
    private const string HojaClientes = "Clientes empresariales";
    private const string HojaCentros = "Centros";
    private const string HojaTrabajadores = "Trabajadores";

    // La marca de la operación chocó en el índice único: otra confirmación de la misma operación ganó. Se reinicia en
    // cada ejecución, por si el handler se reutiliza.
    private bool operacionYaEjecutada;

    // Lo que un intento de ESTA confirmación calculó y mandó guardar, con el Id de la marca de la operación que guardó a
    // la vez. Si la estrategia de reintentos repite la operación, el reintento solo devuelve este informe si la marca
    // guardada en la base es ESA fila: entonces el commit de ese intento sí llegó (se comunicó como un error transitorio)
    // y el informe es lo escrito (revisión Codex de la PR #2164, r3, MEDIO 2). Si el commit falló de verdad y otra
    // confirmación guardó después la misma operación, la marca es otra y el informe de este intento, revertido, no
    // describe nada: se devuelve «ya confirmada» (r4, MEDIO 1). Se reinicia en cada ejecución, por si el handler se reutiliza.
    private (Guid MarcaId, ResultadoImportacionMaestraDto Resultado)? intentoGuardado;

    // La entrada de éxito del Historial que va en el mismo guardado (r4, MEDIO 2). Se fija en cada confirmación.
    private string nombreArchivoHistorial = string.Empty;
    private Guid usuarioHistorialId;

    public Task<Result<ResultadoImportacionMaestraDto>> Handle(
        EjecutarImportacionMaestraCommand request, CancellationToken cancellationToken) =>
        EjecutarAsync(request.Plan, request.ReemplazarExistentes, request.HuellaVistaPrevia, request.NombreArchivo, cancellationToken);

    public Task<Result<ResultadoImportacionMaestraDto>> Handle(
        PrevisualizarImportacionMaestraCommand request, CancellationToken cancellationToken) =>
        EjecutarAsync(request.Plan, request.ReemplazarExistentes, huellaVistaPrevia: null, nombreArchivo: null, cancellationToken);

    /// <param name="huellaVistaPrevia">Null: vista previa (no escribe). Si no: confirmar, solo si el resultado sigue teniendo esa huella.</param>
    private async Task<Result<ResultadoImportacionMaestraDto>> EjecutarAsync(
        PlanImportacionMaestraDto plan, bool reemplazar, string? huellaVistaPrevia, string? nombreArchivo,
        CancellationToken cancellationToken)
    {
        var escribir = huellaVistaPrevia is not null;
        operacionYaEjecutada = false;
        intentoGuardado = null;
        if (!await autorizacionEscrituraEfectiva.EsActoDeAdministradorAsync(cancellationToken))
            return Result.Fallo<ResultadoImportacionMaestraDto>(Error.Crear(
                "Importacion.SoloAdministrador", "Solo Administrador puede ejecutar la importación de la plantilla maestra."));

        // Mismo autor que registraba RegistrarHistorialImportacionCommand: el usuario actual, o vacío si no lo hay.
        nombreArchivoHistorial = EjecutarImportacionMaestraCommand.NombreArchivoHistorial(nombreArchivo);
        usuarioHistorialId = escribir ? await usuarioActual.ObtenerUsuarioActualIdAsync() ?? Guid.Empty : Guid.Empty;

        if (escribir && await operacionImportacionRepositorio.ExisteAsync(plan.OperacionId, cancellationToken))
            return ResultadoImportacionMaestraDto.YaEjecutada();

        // El índice único de CIF incluye las Empresas dadas de baja del Tenant propietario: una fila que coincida con
        // una baja abortaría el guardado con un error genérico.
        foreach (var cif in plan.Empresas.Select(e => e.Cif).Concat(plan.Clientes.Select(c => c.Cif)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var duplicada = await empresaRepositorio.ObtenerDuplicadaPorCifAsync(cif, cancellationToken: cancellationToken);
            if (duplicada?.EstaEliminado == true)
                return Result.Fallo<ResultadoImportacionMaestraDto>(DuplicadosEmpresa.ErrorDeBajaEnImportacion(cif));
        }

        try
        {
            // Confirmar corre SIEMPRE bajo el candado del Tenant (ISerializacionAltasAcreditacionEmpresa), en los dos modos,
            // desde la relectura de lo existente hasta el guardado:
            // - Reemplazando, una fila con gestión CAE puede devolver un Centro existente a ella y sus accesos de
            //   plataforma reciben los Documentos de Empresa que exige (regla A-1), igual que en EstablecerGestionCaeCentro.
            // - En los dos modos, la comparación con la vista previa protege también las AUSENCIAS (un Centro que se va a
            //   crear porque no existe): otra importación maestra del mismo Tenant que cree ese Centro espera a que esta
            //   guarde, y al releer lo ve, así que su vista previa ya no coincide y no escribe. Sin el candado, las dos
            //   pasarían la comparación y el Centro quedaría duplicado: no hay índice único por Cliente empresarial y
            //   nombre del Centro. Revisión Codex de la PR #2164, r2, MEDIO 2. El alta manual de un Centro y la plantilla
            //   combinada sin hoja de Asignaciones no toman este candado: frente a ellas la carrera sigue abierta.
            if (!escribir)
                return await ProcesarAsync(plan, reemplazar, huellaEsperada: null, cancellationToken);

            Result<ResultadoImportacionMaestraDto>? procesado = null;
            var serializado = await serializacionAltas.EjecutarAsync(async ct =>
            {
                procesado = await ComprobarYEscribirAsync(plan, reemplazar, huellaVistaPrevia!, ct);
                if (procesado.EsFallido)
                    return Result.Fallo(procesado.Error);
                // La perdedora de dos confirmaciones de la misma operación devuelve «ya ejecutada», que es un éxito para
                // quien llama, pero la transacción del candado tiene que deshacerse, igual que en EjecutarImportacion.
                return operacionYaEjecutada
                    ? Result.Fallo(Error.Crear("Importacion.YaEjecutada", "Esta importación ya se había ejecutado."))
                    : Result.Exito();
            }, cancellationToken);
            if (serializado.EsExitoso || operacionYaEjecutada)
                return procesado!;
            return procesado is { EsFallido: true } ? procesado : Result.Fallo<ResultadoImportacionMaestraDto>(serializado.Error);
        }
        catch
        {
            // Blazor Server comparte un único DbContext por circuito: una excepción a mitad deja entidades en Added.
            if (escribir) operacionImportacionRepositorio.DescartarPendientes();
            throw;
        }
    }

    /// <summary>
    /// Confirmar: primero recalcula la vista previa con los datos de ahora (bajo el candado) sin tocar ninguna entidad; si
    /// ya no coincide con la mostrada, para ahí. Si coincide, escribe, y la escritura vuelve a comparar su propio resultado
    /// antes de guardar (cierra el hueco entre las dos lecturas).
    /// </summary>
    private async Task<Result<ResultadoImportacionMaestraDto>> ComprobarYEscribirAsync(
        PlanImportacionMaestraDto plan, bool reemplazar, string huellaVistaPrevia, CancellationToken cancellationToken)
    {
        // Releída bajo el candado: otra confirmación de la misma operación, o el primer intento de esta si la estrategia
        // de reintentos repite la operación tras confirmar, ya la guardó. Sin esto, la relectura vería lo que ella creó y
        // respondería «la vista previa cambió» por una importación que sí se hizo. Si la marca guardada es la de un intento
        // anterior de esta misma confirmación (su commit llegó), se devuelve lo que ese intento escribió; si no (otra
        // confirmación, o un intento propio revertido y otra confirmación después), «ya confirmada», sin informe.
        if (await operacionImportacionRepositorio.ExisteAsync(plan.OperacionId, cancellationToken))
        {
            operacionYaEjecutada = true;
            return intentoGuardado is { } intento
                   && await operacionImportacionRepositorio.EsLaMarcaGuardadaAsync(plan.OperacionId, intento.MarcaId, cancellationToken)
                ? intento.Resultado
                : ResultadoImportacionMaestraDto.YaEjecutada();
        }

        var actual = await ProcesarAsync(plan, reemplazar, huellaEsperada: null, cancellationToken);
        if (actual.EsFallido) return actual;
        if (actual.Valor.CalcularHuella() != huellaVistaPrevia)
            return Result.Fallo<ResultadoImportacionMaestraDto>(ErroresImportacionMaestra.VistaPreviaCambiada);
        return await ProcesarAsync(plan, reemplazar, huellaVistaPrevia, cancellationToken);
    }

    /// <param name="huellaEsperada">Null: solo calcula (vista previa). Si no: escribe, si el resultado tiene esa huella.</param>
    private async Task<Result<ResultadoImportacionMaestraDto>> ProcesarAsync(
        PlanImportacionMaestraDto plan, bool reemplazar, string? huellaEsperada, CancellationToken cancellationToken)
    {
        var escribir = huellaEsperada is not null;
        var marca = escribir ? OperacionImportacion.Registrar(plan.OperacionId) : null;
        if (marca is not null) operacionImportacionRepositorio.Agregar(marca);

        var omitidos = new List<ItemImportacionDto>();
        var sobrescritos = new List<ValorPrevioDto>();
        var rellenados = new List<CampoRellenadoDto>();
        // Cada alta, actualización y Relación Empresarial, con su clave natural: identifica lo que hay detrás de cada cifra.
        var escrituras = new List<EscrituraImportacionDto>();
        void Escrita(string operacion, string hoja, int fila, string clave) => escrituras.Add(new(operacion, hoja, fila, clave));
        var ahora = DateTime.UtcNow;

        // Valor resultante de un campo de texto bajo el modo elegido, contando rellenos y sobrescrituras.
        string? Texto(string hoja, int fila, string campo, string? actual, string? archivo)
        {
            if (string.IsNullOrWhiteSpace(archivo)) return actual;
            if (string.IsNullOrWhiteSpace(actual)) { rellenados.Add(new(hoja, fila, campo, archivo)); return archivo; }
            if (!reemplazar || actual == archivo) return actual;
            sobrescritos.Add(new ValorPrevioDto(hoja, fila, campo, actual, archivo));
            return archivo;
        }

        DateOnly? Fecha(string hoja, int fila, string campo, DateOnly? actual, DateOnly? archivo)
        {
            if (archivo is null) return actual;
            if (actual is null) { rellenados.Add(new(hoja, fila, campo, archivo.Value.ToString("yyyy-MM-dd"))); return archivo; }
            if (!reemplazar || actual == archivo) return actual;
            sobrescritos.Add(new ValorPrevioDto(hoja, fila, campo, actual.Value.ToString("yyyy-MM-dd"), archivo.Value.ToString("yyyy-MM-dd")));
            return archivo;
        }

        // Aplica una actualización del dominio, que muta campo a campo y valida sobre la marcha. Si una validación falla a
        // mitad, o si es vista previa, se restaura el estado original (válido por construcción): así ni una fila omitida
        // deja la entidad rastreada a medias para el guardado final, ni la vista previa muta nada, y ambas ven lo mismo.
        bool IntentarActualizar(Action aplicar, Action revertir, out string? error, Action? trasAplicar = null)
        {
            error = null;
            try
            {
                aplicar();
                trasAplicar?.Invoke();
                if (!escribir) revertir();
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                revertir();
                error = ex.Message;
                return false;
            }
        }

        // Si el dominio normalizó el teléfono al valor que ya había, no hubo sobrescritura; si lo cambió, el valor nuevo
        // informado es el persistido.
        void AjustarTelefonoSobrescrito(int fila, int sobrescritosAntes, string? previo, string? persistido)
        {
            for (var i = sobrescritosAntes; i < sobrescritos.Count; i++)
            {
                var v = sobrescritos[i];
                if (v.Hoja != HojaTrabajadores || v.Fila != fila || v.Campo != "Teléfono") continue;
                if (persistido == previo) sobrescritos.RemoveAt(i);
                else sobrescritos[i] = v with { ValorNuevo = persistido ?? v.ValorNuevo };
                return;
            }
        }

        // Deshace lo contado por una fila que acaba omitida (rellenos y sobrescrituras de esa fila).
        void DescartarFila(int rellenadosAntes, int sobrescritosAntes)
        {
            if (rellenados.Count > rellenadosAntes) rellenados.RemoveRange(rellenadosAntes, rellenados.Count - rellenadosAntes);
            if (sobrescritos.Count > sobrescritosAntes) sobrescritos.RemoveRange(sobrescritosAntes, sobrescritos.Count - sobrescritosAntes);
        }

        // ---- Empresas gestionadas (CIF) ----
        // Releídas las que el contexto (uno por circuito en Blazor Server) ya rastreaba: una Empresa con el CIF de antes
        // de esperar el candado casaría por él una fila de Centro y podría devolverlo a gestión CAE (regla A-1).
        var todas = await empresaRepositorio.ListarActualizadasAsync(cancellationToken);
        var gestionadasPorCif = todas.Where(e => e.EsPropia && e.Cif != null).ToDictionary(e => e.Cif!, StringComparer.OrdinalIgnoreCase);
        var clientesPorCif = todas.Where(e => e.EsCritico != null && e.Cif != null).ToDictionary(e => e.Cif!, StringComparer.OrdinalIgnoreCase);
        var razonesEscritas = new HashSet<string>(StringComparer.Ordinal);

        async Task<Error?> RazonSocialOcupadaAsync(string razonSocial, Guid? excluirId) =>
            razonesEscritas.Contains(razonSocial.Trim())
                ? DuplicadosEmpresa.ErrorRazonSocialDuplicada(dadaDeBaja: false)
                : await DuplicadosEmpresa.ComprobarRazonSocialAsync(empresaRepositorio, razonSocial, excluirId, cancellationToken: cancellationToken);

        var empresasCreadas = 0;

        var limiteAvisado = false; // D-P11: un solo aviso a Soporte TALVEG por comando, no uno por fila
        var empresasActualizadas = 0;
        foreach (var fila in plan.Empresas)
        {
            if (gestionadasPorCif.TryGetValue(fila.Cif, out var existente))
            {
                var rellenadosAntes = rellenados.Count;
                var sobrescritosAntes = sobrescritos.Count;
                var razonSocial = Texto(HojaEmpresas, fila.Fila, "Razón social", existente.RazonSocial, fila.RazonSocial)!;
                if (razonSocial != existente.RazonSocial && await RazonSocialOcupadaAsync(razonSocial, existente.Id) is { } ocupada)
                {
                    omitidos.Add(new(HojaEmpresas, fila.Fila, fila.RazonSocial, ocupada.Mensaje));
                    DescartarFila(rellenadosAntes, sobrescritosAntes);
                    continue;
                }
                var cnae = Texto(HojaEmpresas, fila.Fila, "CNAE", existente.Cnae, fila.Cnae);
                var convenio = Texto(HojaEmpresas, fila.Fila, "Convenio aplicable", existente.ConvenioAplicable, fila.ConvenioAplicable);
                var anexo = existente.EsActividadAnexoI;
                if (reemplazar && fila.EsActividadAnexoI is { } anexoArchivo && anexoArchivo != anexo)
                {
                    sobrescritos.Add(new(HojaEmpresas, fila.Fila, "Actividad Anexo I", anexo ? "S" : "N", anexoArchivo ? "S" : "N"));
                    anexo = anexoArchivo;
                }

                if (razonSocial != existente.RazonSocial || cnae != existente.Cnae || convenio != existente.ConvenioAplicable
                    || anexo != existente.EsActividadAnexoI)
                {
                    var (rs0, cnae0, conv0, anexo0) = (existente.RazonSocial, existente.Cnae, existente.ConvenioAplicable, existente.EsActividadAnexoI);
                    if (IntentarActualizar(
                            () => existente.Actualizar(razonSocial, existente.Cif, cnae, convenio, anexo),
                            () => existente.Actualizar(rs0, existente.Cif, cnae0, conv0, anexo0), out var error))
                    {
                        razonesEscritas.Add(razonSocial.Trim());
                        empresasActualizadas++;
                        Escrita(EscrituraImportacionDto.Actualizacion, HojaEmpresas, fila.Fila, fila.Cif);
                    }
                    else
                    {
                        omitidos.Add(new(HojaEmpresas, fila.Fila, fila.RazonSocial, error!));
                        DescartarFila(rellenadosAntes, sobrescritosAntes);
                    }
                }
                continue;
            }

            if (await RazonSocialOcupadaAsync(fila.RazonSocial, null) is { } razonOcupada)
            {
                omitidos.Add(new(HojaEmpresas, fila.Fila, fila.RazonSocial, razonOcupada.Mensaje));
                continue;
            }

            // D-P11: la hoja de Empresas gestionadas crea Empresas propias, y el Tenant propietario solo puede tener una.
            // La vista previa muestra la misma omisión, pero solo confirmar avisa a Soporte TALVEG.
            if (await limiteEmpresaPropia.ComprobarAltaAsync(empresasCreadas, esIntento: escribir && !limiteAvisado, cancellationToken) is { } limite)
            {
                omitidos.Add(new(HojaEmpresas, fila.Fila, fila.RazonSocial, limite.Mensaje));
                limiteAvisado = true;
                continue;
            }

            try
            {
                var empresa = new Empresa(fila.RazonSocial, fila.Cif, fila.Cnae, fila.ConvenioAplicable, fila.EsActividadAnexoI ?? false);
                if (escribir) empresaRepositorio.Agregar(empresa);
                gestionadasPorCif[fila.Cif] = empresa;
                razonesEscritas.Add(empresa.RazonSocial);
                empresasCreadas++;
                Escrita(EscrituraImportacionDto.Alta, HojaEmpresas, fila.Fila, fila.Cif);
            }
            catch (ArgumentException ex)
            {
                omitidos.Add(new(HojaEmpresas, fila.Fila, fila.RazonSocial, ex.Message));
            }
        }

        // ---- Clientes empresariales (CIF) y su relación con la Empresa gestionada ----
        var clientesCreados = 0;
        var clientesActualizados = 0;
        var relacionesCreadas = 0;
        var clientesTratados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paresRelacionados = new HashSet<(Guid, Guid)>();
        var relacionesVigentes = (await (
            from r in empresasContext.RelacionesEmpresariales
            where r.VigenciaHasta == null
            select new { r.ProveedoraId, r.ClienteId }).ToListAsync(cancellationToken))
            .Select(r => (r.ProveedoraId, r.ClienteId)).ToHashSet();

        foreach (var fila in plan.Clientes)
        {
            if (!gestionadasPorCif.TryGetValue(fila.CifEmpresaGestionada, out var gestionada))
            {
                omitidos.Add(new(HojaClientes, fila.Fila, fila.RazonSocial,
                    $"No se pudo crear o encontrar la Empresa gestionada con CIF \"{fila.CifEmpresaGestionada}\"."));
                continue;
            }

            Empresa cliente;
            if (clientesPorCif.TryGetValue(fila.Cif, out var existente))
            {
                cliente = existente;
                // Una Empresa que ya existía puede venir repetida por otra pareja: solo la primera fila válida la toca. Si esa
                // fila se rechaza, no cuenta como tratada: la siguiente del mismo Cliente empresarial lo intenta.
                if (clientesTratados.Add(fila.Cif))
                {
                    var rellenadosAntes = rellenados.Count;
                    var sobrescritosAntes = sobrescritos.Count;
                    var razonSocial = Texto(HojaClientes, fila.Fila, "Razón social", existente.RazonSocial, fila.RazonSocial)!;
                    var notas = Texto(HojaClientes, fila.Fila, "Nota interna", existente.Notas, fila.Nota);
                    var critico = existente.EsCritico ?? false;
                    if (reemplazar && critico != fila.EsCritico)
                    {
                        sobrescritos.Add(new(HojaClientes, fila.Fila, "Crítico", critico ? "C" : "N", fila.EsCritico ? "C" : "N"));
                        critico = fila.EsCritico;
                    }

                    if (razonSocial != existente.RazonSocial && await RazonSocialOcupadaAsync(razonSocial, existente.Id) is { } ocupada)
                    {
                        omitidos.Add(new(HojaClientes, fila.Fila, fila.RazonSocial, ocupada.Mensaje));
                        DescartarFila(rellenadosAntes, sobrescritosAntes);
                        clientesTratados.Remove(fila.Cif);
                        continue;
                    }

                    if (razonSocial != existente.RazonSocial || notas != existente.Notas || critico != existente.EsCritico)
                    {
                        var (rs0, notas0, critico0) = (existente.RazonSocial, existente.Notas, existente.EsCritico ?? false);
                        if (IntentarActualizar(
                                () => existente.ActualizarComoCliente(razonSocial, existente.Cif!, critico, notas),
                                () => existente.ActualizarComoCliente(rs0, existente.Cif!, critico0, notas0), out var error))
                        {
                            razonesEscritas.Add(razonSocial.Trim());
                            clientesActualizados++;
                            Escrita(EscrituraImportacionDto.Actualizacion, HojaClientes, fila.Fila, fila.Cif);
                        }
                        else
                        {
                            omitidos.Add(new(HojaClientes, fila.Fila, fila.RazonSocial, error!));
                            DescartarFila(rellenadosAntes, sobrescritosAntes);
                            clientesTratados.Remove(fila.Cif);
                            continue;
                        }
                    }
                }
            }
            else
            {
                if (await RazonSocialOcupadaAsync(fila.RazonSocial, null) is { } ocupada)
                {
                    omitidos.Add(new(HojaClientes, fila.Fila, fila.RazonSocial, ocupada.Mensaje));
                    continue;
                }
                try
                {
                    cliente = Empresa.CrearComoCliente(fila.RazonSocial, fila.Cif, fila.EsCritico, fila.Nota, ejecutivoUsuarioId: null);
                }
                catch (ArgumentException ex)
                {
                    omitidos.Add(new(HojaClientes, fila.Fila, fila.RazonSocial, ex.Message));
                    continue;
                }
                if (escribir) empresaRepositorio.Agregar(cliente);
                clientesPorCif[fila.Cif] = cliente;
                clientesTratados.Add(fila.Cif);
                razonesEscritas.Add(cliente.RazonSocial);
                clientesCreados++;
                Escrita(EscrituraImportacionDto.Alta, HojaClientes, fila.Fila, fila.Cif);
            }

            // Relación Empresa gestionada → Cliente empresarial, una por pareja y solo si no estaba vigente.
            var par = (gestionada.Id, cliente.Id);
            if (!relacionesVigentes.Contains(par) && paresRelacionados.Add(par))
            {
                if (escribir)
                    await relacionEmpresarialRepositorio.AgregarSiNoVigenteAsync(gestionada.Id, cliente.Id, ahora, cancellationToken: cancellationToken);
                relacionesCreadas++;
                // La pareja, por la clave natural de sus dos Empresas (el Id de una Empresa que se crea cambia en cada pasada).
                Escrita(EscrituraImportacionDto.Relacion, HojaClientes, fila.Fila, $"{fila.CifEmpresaGestionada} → {fila.Cif}");
            }
        }

        // ---- Centros (Cliente empresarial + nombre) ----
        var clientePorId = todas.Concat(clientesPorCif.Values).Concat(gestionadasPorCif.Values).DistinctBy(e => e.Id).ToDictionary(e => e.Id);
        static string ClaveCentro(Guid idDelCliente, string nombre) => $"{idDelCliente:N}|{nombre}";
        // Para la huella: CIF del Cliente empresarial y nombre (la clave de la fila), y la Empresa gestionada a la que va.
        static string ClaveNaturalCentro(CentroMaestraDto fila) => $"{fila.CifCliente}|{fila.Nombre}|{fila.CifEmpresaGestionada}";
        // Releídos los que el contexto (uno por circuito en Blazor Server) ya rastreaba: la modalidad de gestión CAE que
        // decide si un Centro vuelve a ella (regla A-1) tiene que ser la de la base bajo el candado, no la de antes.
        var centrosPorClave = CatalogoPorNombre<Centro>.De(
            await centroRepositorio.ListarActualizadosAsync(cancellationToken), c => ClaveCentro(c.ClienteId, c.Nombre), c => c);
        var centrosCreados = 0;
        var centrosActualizados = 0;
        var centrosQueVuelvenAGestionCae = new List<Centro>();

        foreach (var fila in plan.Centros)
        {
            if (!clientesPorCif.TryGetValue(fila.CifCliente, out var cliente) || !gestionadasPorCif.TryGetValue(fila.CifEmpresaGestionada, out var gestionada))
            {
                omitidos.Add(new(HojaCentros, fila.Fila, fila.Nombre, "No se pudo crear o encontrar el Cliente empresarial o la Empresa gestionada de este Centro."));
                continue;
            }

            var busqueda = centrosPorClave.Buscar(ClaveCentro(cliente.Id, fila.Nombre), out var existente);
            if (busqueda == ResultadoBusquedaPorNombre.Ambiguo)
            {
                omitidos.Add(new(HojaCentros, fila.Fila, fila.Nombre,
                    $"Hay varios Centros llamados \"{fila.Nombre}\" en este Cliente empresarial (el nombre solo cambia en mayúsculas); la importación no puede saber cuál actualizar. Unifica o renombra los duplicados y vuelve a importar."));
                continue;
            }

            if (busqueda == ResultadoBusquedaPorNombre.Unico && existente is not null)
            {
                // El Centro ya existe bajo otra Empresa gestionada: el archivo no lo mueve ni lo pisa en silencio.
                if (existente.EmpresaId != gestionada.Id)
                {
                    omitidos.Add(new(HojaCentros, fila.Fila, fila.Nombre,
                        $"Este Centro ya existe y pertenece a otra Empresa gestionada distinta de la del CIF \"{fila.CifEmpresaGestionada}\"; no se modifica."));
                    continue;
                }

                var rellenadosAntes = rellenados.Count;
                var sobrescritosAntes = sobrescritos.Count;
                var codigo = Texto(HojaCentros, fila.Fila, "Código del Centro", existente.CodigoCentro, fila.CodigoCentro);
                var direccion = Texto(HojaCentros, fila.Fila, "Dirección", existente.Direccion, fila.Direccion);
                var contacto = Texto(HojaCentros, fila.Fila, "Contacto", existente.Contacto, fila.Contacto);
                var contrato = Fecha(HojaCentros, fila.Fila, "Contrato vigente hasta", existente.ContratoVigenteHasta, fila.ContratoVigenteHasta);
                var modalidad = fila.SinGestionCae ? ModalidadGestionCae.SinGestionCae : ModalidadGestionCae.ConGestionCae;
                var modalidadResultante = existente.GestionCae;
                if (reemplazar && modalidad != existente.GestionCae)
                {
                    sobrescritos.Add(new(HojaCentros, fila.Fila, "Modalidad de gestión CAE",
                        existente.GestionCae == ModalidadGestionCae.SinGestionCae ? "Sin gestión CAE" : "Con gestión CAE",
                        fila.SinGestionCae ? "Sin gestión CAE" : "Con gestión CAE"));
                    modalidadResultante = modalidad;
                }

                if (codigo != existente.CodigoCentro || direccion != existente.Direccion || contacto != existente.Contacto
                    || contrato != existente.ContratoVigenteHasta || modalidadResultante != existente.GestionCae)
                {
                    var (codigo0, direccion0, contacto0, contrato0, modalidad0) =
                        (existente.CodigoCentro, existente.Direccion, existente.Contacto, existente.ContratoVigenteHasta, existente.GestionCae);
                    if (IntentarActualizar(
                            () =>
                            {
                                existente.Actualizar(existente.Nombre, codigo, direccion, contacto, contrato);
                                existente.EstablecerGestionCae(modalidadResultante);
                            },
                            () =>
                            {
                                existente.Actualizar(existente.Nombre, codigo0, direccion0, contacto0, contrato0);
                                existente.EstablecerGestionCae(modalidad0);
                            }, out var error))
                    {
                        centrosActualizados++;
                        Escrita(EscrituraImportacionDto.Actualizacion, HojaCentros, fila.Fila, ClaveNaturalCentro(fila));
                        if (escribir && modalidad0 == ModalidadGestionCae.SinGestionCae
                            && modalidadResultante == ModalidadGestionCae.ConGestionCae)
                            centrosQueVuelvenAGestionCae.Add(existente);
                    }
                    else
                    {
                        omitidos.Add(new(HojaCentros, fila.Fila, fila.Nombre, error!));
                        DescartarFila(rellenadosAntes, sobrescritosAntes);
                    }
                }
                continue;
            }

            try
            {
                var centro = new Centro(cliente.Id, gestionada.Id, fila.Nombre, fila.CodigoCentro, fila.Direccion, fila.Contacto, fila.ContratoVigenteHasta);
                if (fila.SinGestionCae) centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
                if (escribir) centroRepositorio.Agregar(centro);
                centrosPorClave.Agregar(ClaveCentro(cliente.Id, fila.Nombre), centro);
                centrosCreados++;
                Escrita(EscrituraImportacionDto.Alta, HojaCentros, fila.Fila, ClaveNaturalCentro(fila));
            }
            catch (ArgumentException ex)
            {
                omitidos.Add(new(HojaCentros, fila.Fila, fila.Nombre, ex.Message));
            }
        }

        // ---- Trabajadores (identidad) ----
        // Releídos igual que las Empresas: bajo el candado, la identidad y la Empresa de cada Trabajador son las de la base.
        var trabajadoresPorIdentidad = (await trabajadorRepositorio.ListarConIdentidadActualizadosAsync(cancellationToken))
            .ToDictionary(t => t.Dni!, StringComparer.OrdinalIgnoreCase);
        var enPapelera = await DnisEnPapelera.ObtenerAsync(
            trabajadorRepositorio, plan.Trabajadores.Select(t => t.Identidad).Where(i => !trabajadoresPorIdentidad.ContainsKey(i)), cancellationToken);
        var trabajadoresCreados = 0;
        var trabajadoresActualizados = 0;

        foreach (var fila in plan.Trabajadores)
        {
            var descripcion = $"{fila.Nombre} {fila.Apellidos}";
            // Una identidad en la Papelera no está entre los activos, pero el índice (TenantId, Dni) la incluye.
            if (!trabajadoresPorIdentidad.ContainsKey(fila.Identidad) && enPapelera.Contains(DnisEnPapelera.Normalizar(fila.Identidad)))
            {
                omitidos.Add(new(HojaTrabajadores, fila.Fila, descripcion, DnisEnPapelera.Motivo(fila.Identidad)));
                continue;
            }

            if (!gestionadasPorCif.TryGetValue(fila.CifEmpresa, out var empresa))
            {
                omitidos.Add(new(HojaTrabajadores, fila.Fila, descripcion, $"No se pudo crear o encontrar la Empresa gestionada con CIF \"{fila.CifEmpresa}\"."));
                continue;
            }

            if (trabajadoresPorIdentidad.TryGetValue(fila.Identidad, out var existente))
            {
                // El Trabajador ya existe en otra Empresa gestionada: el archivo no lo pisa en silencio.
                if (existente.EmpresaId != empresa.Id)
                {
                    omitidos.Add(new(HojaTrabajadores, fila.Fila, descripcion,
                        $"Este Trabajador ya existe y pertenece a otra Empresa gestionada distinta de la del CIF \"{fila.CifEmpresa}\"; no se modifica."));
                    continue;
                }

                var rellenadosAntes = rellenados.Count;
                var sobrescritosAntes = sobrescritos.Count;
                var nombre = Texto(HojaTrabajadores, fila.Fila, "Nombre", existente.Nombre, fila.Nombre)!;
                var apellidos = Texto(HojaTrabajadores, fila.Fila, "Apellidos", existente.Apellidos, fila.Apellidos)!;
                var nacimiento = Fecha(HojaTrabajadores, fila.Fila, "Fecha de nacimiento", existente.FechaNacimiento, fila.FechaNacimiento);
                var email = Texto(HojaTrabajadores, fila.Fila, "Correo", existente.Email, fila.Email);
                var telefono = Texto(HojaTrabajadores, fila.Fila, "Teléfono", existente.Telefono, fila.Telefono);
                var puesto = Texto(HojaTrabajadores, fila.Fila, "Categoría / Puesto", existente.Puesto, fila.Puesto);
                var observaciones = Texto(HojaTrabajadores, fila.Fila, "Observaciones", existente.Observaciones, fila.Observaciones);

                if (nombre != existente.Nombre || apellidos != existente.Apellidos || nacimiento != existente.FechaNacimiento
                    || email != existente.Email || telefono != existente.Telefono || puesto != existente.Puesto
                    || observaciones != existente.Observaciones)
                {
                    string? telefonoPersistido = null;
                    var cambioReal = false;
                    var (nombre0, apellidos0, nacimiento0, email0, observaciones0, telefono0, puesto0) =
                        (existente.Nombre, existente.Apellidos, existente.FechaNacimiento, existente.Email, existente.Observaciones, existente.Telefono, existente.Puesto);
                    if (IntentarActualizar(
                            () => existente.Actualizar(nombre, apellidos, nacimiento, email, observaciones, existente.Alias, telefono, puesto),
                            () => existente.Actualizar(nombre0, apellidos0, nacimiento0, email0, observaciones0, existente.Alias, telefono0, puesto0),
                            out var error,
                            // El dominio normaliza el teléfono: N14 informa el valor realmente persistido, no el de la celda.
                            trasAplicar: () =>
                            {
                                telefonoPersistido = existente.Telefono;
                                cambioReal = existente.Nombre != nombre0 || existente.Apellidos != apellidos0
                                    || existente.FechaNacimiento != nacimiento0 || existente.Email != email0
                                    || existente.Puesto != puesto0 || existente.Observaciones != observaciones0
                                    || telefonoPersistido != telefono0;
                            }))
                    {
                        AjustarTelefonoSobrescrito(fila.Fila, sobrescritosAntes, telefono0, telefonoPersistido);
                        if (cambioReal)
                        {
                            trabajadoresActualizados++;
                            Escrita(EscrituraImportacionDto.Actualizacion, HojaTrabajadores, fila.Fila, fila.Identidad);
                        }
                    }
                    else
                    {
                        omitidos.Add(new(HojaTrabajadores, fila.Fila, descripcion, error!));
                        DescartarFila(rellenadosAntes, sobrescritosAntes);
                    }
                }
                continue;
            }

            try
            {
                var trabajador = Trabajador.DeEmpresa(empresa.Id, fila.Nombre, fila.Apellidos, fila.Identidad, fila.FechaNacimiento,
                    fila.Email, fila.Observaciones, alias: null, fila.Telefono, fila.Puesto);
                if (escribir) trabajadorRepositorio.Agregar(trabajador);
                trabajadoresPorIdentidad[fila.Identidad] = trabajador;
                trabajadoresCreados++;
                Escrita(EscrituraImportacionDto.Alta, HojaTrabajadores, fila.Fila, fila.Identidad);
            }
            catch (ArgumentException ex)
            {
                omitidos.Add(new(HojaTrabajadores, fila.Fila, descripcion, ex.Message));
            }
        }

        // Solo se cuentan las sobrescrituras de filas que SÍ se aplicaron (una fila omitida a mitad no escribe).
        var omitidasPorFila = omitidos.Select(o => (o.Hoja, o.Fila)).ToHashSet();
        sobrescritos.RemoveAll(v => omitidasPorFila.Contains((v.Hoja, v.Fila)));

        var resultado = new ResultadoImportacionMaestraDto(
            empresasCreadas, empresasActualizadas, clientesCreados, clientesActualizados, relacionesCreadas,
            centrosCreados, centrosActualizados, trabajadoresCreados, trabajadoresActualizados, rellenados.Count,
            sobrescritos, plan.Advertencias, [.. plan.Omitidos, .. omitidos])
        {
            DetalleCamposRellenados = rellenados,
            DetalleEscrituras = escrituras
        };

        if (!escribir) return resultado;

        // Lo que esta pasada dejaría guardado ya no es lo mostrado (algo cambió entre la comprobación y esta lectura): se
        // descarta todo lo pendiente del contexto y no se guarda nada.
        if (resultado.CalcularHuella() != huellaEsperada)
        {
            operacionImportacionRepositorio.DescartarPendientes();
            return Result.Fallo<ResultadoImportacionMaestraDto>(ErroresImportacionMaestra.VistaPreviaCambiada);
        }

        // Un Centro que vuelve a gestión CAE: sus accesos de plataforma reciben los Documentos de Empresa que exige
        // (regla A-1), en el mismo guardado. Solo los de Empresa: los Documentos de Trabajador no cambian.
        // Cuenta el estado final: una fila posterior del mismo Centro puede devolverlo a Sin gestión CAE.
        var vuelvenAGestionCae = centrosQueVuelvenAGestionCae
            .Where(c => c.GestionCae == ModalidadGestionCae.ConGestionCae)
            .Select(c => c.Id)
            .Distinct()
            .ToList();
        if (vuelvenAGestionCae.Count > 0)
        {
            var accesosPlataforma = await centrosContext.CanalesGestionDocumental
                .Where(c => vuelvenAGestionCae.Contains(c.CentroId) && c.Tipo == TipoCanalGestion.Plataforma)
                .ToListAsync(cancellationToken);
            if (accesosPlataforma.Count > 0)
                await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion
                {
                    Canales = accesosPlataforma,
                    CentrosQueVuelvenAGestionCae = vuelvenAGestionCae,
                    SoloDocumentosDeEmpresa = true
                }, cancellationToken);
        }

        // La entrada de éxito del Historial, en el mismo guardado: queda si y solo si queda lo escrito (r4, MEDIO 2).
        historialRepositorio.Agregar(HistorialImportacion.Exito(
            EjecutarImportacionMaestraCommand.PlantillaHistorial, nombreArchivoHistorial, usuarioHistorialId,
            resultado.EmpresasCreadas + resultado.ClientesCreados + resultado.RelacionesCreadas + resultado.CentrosCreados
            + resultado.TrabajadoresCreados,
            resultado.Advertencias.Count, resultado.Omitidos.Count));

        // Todo lo pendiente junto con la marca de la operación en un único guardado: si dos confirmaciones de la
        // MISMA operación compiten, la perdedora choca en el índice único y no escribe nada (ni su entrada de Historial).
        // Guardado no es confirmado: el commit es de la transacción del candado. Por eso se recuerda con el Id de su marca.
        if (await operacionImportacionRepositorio.GuardarSiOperacionNuevaAsync(cancellationToken))
        {
            intentoGuardado = (marca!.Id, resultado);
            return resultado;
        }

        operacionYaEjecutada = true;
        return ResultadoImportacionMaestraDto.YaEjecutada();
    }
}
